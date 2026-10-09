using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;
using System.Text.Json;

namespace Agentica.Lab.Web.Runtime;

public sealed class HostRun : IDisposable
{
    private sealed class PendingAction(HostActionRequest request, HostCapability capability)
    {
        public HostActionRequest Request { get; } = request;
        public HostCapability Capability { get; } = capability;
        public bool DeliveryAttempted { get; set; }
        public bool Unresolved { get; set; }
        public HostActionResult? Result { get; set; }
        public TaskCompletionSource<HostActionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, PendingAction> _actions = new(StringComparer.Ordinal);
    private readonly HostContextSession _context;
    private readonly ILabPlannerFactory _planners;
    private readonly ActionCustodyStore? _custody;
    private IHostConnection? _connection;
    private Task? _task;
    private string _status = "starting";
    private OutcomeEnvelope? _outcome;
    private object? _termination;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
    private bool _disposed;

    public HostRun(HostRunRequest request, HostContextSession context, ILabPlannerFactory planners, ActionCustodyStore? custody = null)
    {
        Request = request;
        _context = context;
        _planners = planners;
        _custody = custody;
        ManifestHash = ProtocolValidation.Digest(request.Capabilities);
    }

    public string RunId { get; } = "labrun_" + Guid.NewGuid().ToString("N");
    public HostRunRequest Request { get; }
    public string ManifestHash { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public RunEventFeed Events { get; } = new();
    public Task Completion { get { lock (_gate) return _task ?? Task.CompletedTask; } }
    public bool Terminal { get { lock (_gate) return _task?.IsCompleted == true; } }
    public bool HasUnresolvedActions { get { lock (_gate) return _actions.Values.Any(a => a.Unresolved || (a.DeliveryAttempted && a.Result is null)); } }

    public object Snapshot(bool includeContext = true)
    {
        lock (_gate)
            return new
            {
                runId = RunId,
                Request.HostId,
                Request.SessionId,
                Request.SessionEpoch,
                Request.ObjectiveId,
                Request.Objective,
                Request.ScopeId,
                Request.PerspectiveId,
                status = _status,
                createdAt = CreatedAt,
                updatedAt = _updatedAt,
                connected = _connection is not null,
                manifestHash = ManifestHash,
                pendingActions = _actions.Values.Where(a => a.Result is null || a.Unresolved)
                    .Select(a => new { request = a.Request, a.DeliveryAttempted, a.Unresolved }).ToArray(),
                outcome = includeContext ? _outcome : _outcome is null ? null : DisplayOutcome(_outcome),
                termination = _termination,
                context = includeContext ? _context.Snapshot() : null
            };
    }

    public void Attach(IHostConnection connection)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null && _connection.ConnectionId != connection.ConnectionId)
                ProtocolValidation.Fail("session.connected", "This run already has an active host connection.");
            _connection = connection;
            _updatedAt = DateTimeOffset.UtcNow;
        }
        Publish("connection", new { connected = true });
    }

    public void Detach(IHostConnection connection)
    {
        lock (_gate)
        {
            if (_connection?.ConnectionId != connection.ConnectionId) return;
            _connection = null;
            _updatedAt = DateTimeOffset.UtcNow;
        }
        Publish("connection", new { connected = false });
    }

    public void Start()
    {
        lock (_gate) _task ??= Task.Run(ExecuteAsync);
    }

    public void Cancel()
    {
        _lifetime.Cancel();
        Publish("cancel.requested", new { stopFutureDispatch = true, reconcileDispatchedActions = true });
    }

    public async Task ReplayPendingAsync(CancellationToken cancellationToken)
    {
        PendingAction[] actions;
        IHostConnection? connection;
        lock (_gate)
        {
            actions = _actions.Values.Where(a => a.Result is null || a.Unresolved).ToArray();
            connection = _connection;
        }
        if (connection is null) return;
        await connection.SendAsync(new ServiceMessage("resumed", Snapshot(false), RunId), cancellationToken).ConfigureAwait(false);
        foreach (var action in actions)
        {
            string type;
            lock (_gate)
            {
                if (!_actions.TryGetValue(action.Request.ActionId, out var current) || current != action ||
                    (action.Result is not null && !action.Unresolved)) continue;
                type = action.DeliveryAttempted ? "action.reconcile" : "action.request";
                if (!action.DeliveryAttempted && (_lifetime.IsCancellationRequested ||
                    action.Request.DeadlineAt <= DateTimeOffset.UtcNow || _task?.IsCompleted == true)) continue;
                if (!action.DeliveryAttempted) Reserve(action);
            }
            await connection.SendAsync(new ServiceMessage(type, action.Request, RunId), cancellationToken).ConfigureAwait(false);
        }
        OutcomeEnvelope? outcome;
        lock (_gate) outcome = _outcome;
        if (outcome is not null)
            await connection.SendAsync(new ServiceMessage("outcome", DisplayOutcome(outcome), RunId), cancellationToken).ConfigureAwait(false);
        object? termination;
        lock (_gate) termination = _termination;
        if (termination is not null)
            await connection.SendAsync(new ServiceMessage("run.terminated", termination, RunId), cancellationToken).ConfigureAwait(false);
    }

    public bool AcceptResult(HostActionResult result)
    {
        lock (_gate)
        {
            if (!_actions.TryGetValue(result.ActionId, out var action))
                throw new HostProtocolException("action.unknown", "No action with this identity was dispatched.");
            if (result.SessionId != Request.SessionId || result.SessionEpoch != Request.SessionEpoch)
                ProtocolValidation.Fail("action.session", "Action result belongs to a different session epoch.");
            if (action.Result is { } previous && previous.Disposition != "unresolved")
            {
                if (ProtocolValidation.Digest(previous) != ProtocolValidation.Digest(result))
                    ProtocolValidation.Fail("action.duplicate_changed", "A completed action cannot receive a different result.");
                return false;
            }
            ValidateResult(action, result);
            _custody?.PrevalidateResolution(Request.HostId, Request.SessionId, result.ActionId, result,
                _context.CurrentObservation.Revision);
            if (result.Observation is not null) _context.Record(result.Observation);
            _custody?.Resolve(Request.HostId, Request.SessionId, result.ActionId, result);
            action.Result = result;
            action.Unresolved = result.Disposition == "unresolved";
            _updatedAt = DateTimeOffset.UtcNow;
            action.Completion.TrySetResult(result);
        }
        Publish("action.accepted", new { result.ActionId, result.Disposition, result.EvidenceId, result.AfterRevision });
        return true;
    }

    private void ValidateResult(PendingAction action, HostActionResult result)
    {
        if (!action.DeliveryAttempted) ProtocolValidation.Fail("action.not_dispatched", "Action has not been sent to the host.");
        if (result.Disposition is not ("applied" or "refused" or "conflict" or "unavailable" or "unresolved"))
            ProtocolValidation.Fail("action.disposition", "Unknown action disposition.");
        ProtocolValidation.Identifier(result.EvidenceId);
        ProtocolValidation.Text(result.Summary, 4000, "result summary");
        if (result.BeforeRevision < 0 || result.AfterRevision < result.BeforeRevision)
            ProtocolValidation.Fail("action.revision", "Invalid result revision ordering.");
        if (result.Disposition is "applied" or "refused" && result.BeforeRevision != action.Request.ExpectedRevision)
            ProtocolValidation.Fail("action.revision", "A stale action must be reported as a conflict.");
        if (result.Disposition == "applied" && action.Capability.Effect == ToolEffect.ReadOnly && result.BeforeRevision != result.AfterRevision)
            ProtocolValidation.Fail("action.readonly", "A read-only capability changed the host revision.");
        if (result.Disposition is "refused" or "conflict" or "unavailable" && result.BeforeRevision != result.AfterRevision)
            ProtocolValidation.Fail("action.revision", "A non-applied result cannot claim an effect. Report uncertainty as unresolved.");
        if (result.Disposition is "applied" or "refused" or "conflict" && result.Observation is null)
            ProtocolValidation.Fail("action.observation", "A resolved host action requires a fresh observation.");
        if (result.Observation is not null)
        {
            ProtocolValidation.Observation(result.Observation);
            if (result.Observation.Revision != result.AfterRevision || result.AfterRevision < _context.CurrentObservation.Revision)
                ProtocolValidation.Fail("action.observation_revision", "Observation does not match the resulting host revision.");
        }
        if (result.Completion is not null)
        {
            if (result.Disposition != "applied" || result.Observation is null || result.Completion.ObjectiveId != Request.ObjectiveId)
                ProtocolValidation.Fail("completion.binding", "Completion must bind an applied result to the active objective.");
            ProtocolValidation.Identifier(result.Completion.EvidenceId);
            ProtocolValidation.Text(result.Completion.Summary, 4000, "completion summary");
        }
        if (_actions.Values.Any(a => a != action && a.Result?.EvidenceId == result.EvidenceId))
            ProtocolValidation.Fail("action.evidence_reused", "Host evidence identity was already used for another action.");
    }

    private async Task ExecuteAsync()
    {
        using var feed = new LlmTelemetryFeed(Request.Provider?.Provider ?? "gemini",
            includeThoughtSummaries: Request.Provider?.IncludeThoughtSummaries ?? false);
        var pumping = PumpTelemetryAsync(feed);
        try
        {
            SetStatus("running");
            var settings = Request.Provider ?? new ProviderSettings();
            var planner = settings.Provider == "demo"
                ? DemoPlanner.Create(feed.Report)
                : _planners.Create(settings, feed.Report);
            var registrations = Request.Capabilities.Select(CreateRegistration).Concat(_context.CreateTools()).ToArray();
            var limits = Request.Limits ?? new HostRunLimits();
            var runner = new AgenticaRunner(planner, ToolCatalog.Create(registrations), new RunSink(this),
                new DeterministicOutcomeReporter(), new ExecutionPolicy(
                    MaxSteps: limits.MaxSteps, MaxRefinements: limits.MaxRefinements,
                    Timeout: TimeSpan.FromSeconds(limits.TimeoutSeconds),
                    MaxPlanContinuations: limits.MaxPlanContinuations,
                    PlanningContext: new PlanningContextOptions(limits.MaxRecentObservations, limits.MaxRecentReceipts),
                    MaxBlockedRetries: 0, MaxParallelism: 1, MaxBatchSize: 1,
                    AllowReadOnlyParallelBatches: false, EvaluateCompletionAfterEachBatch: true,
                    EffectPolicy: new ToolEffectPolicy(registrations.Select(r => r.Descriptor.Effect)),
                    SecurityPolicy: new ToolSecurityPolicy(
                        InitialBoundaries: [ToolDataBoundary.HostState, ToolDataBoundary.UserContent],
                        ExternalPlannerAllowedBoundaries: [ToolDataBoundary.HostState, ToolDataBoundary.UserContent,
                            ToolDataBoundary.Public, ToolDataBoundary.ExternalUntrusted])),
                new HostCompletionEvaluator(this), _context);
            var outcome = await runner.RunAsync(new RunRequest(Request.Objective, RequestOrigin.User,
                new Dictionary<string, object?>
                {
                    ["objectiveId"] = Request.ObjectiveId,
                    ["sessionId"] = Request.SessionId,
                    ["scopeId"] = Request.ScopeId,
                    ["perspectiveId"] = Request.PerspectiveId,
                    ["guidance"] = "Use only scoped host observations. Verify the objective through host result evidence. " +
                        "Query exact retained evidence when needed; keep hypotheses separate from observed facts."
                }), _lifetime.Token).ConfigureAwait(false);
            lock (_gate) _outcome = outcome;
            SetStatus(outcome.Outcome.Status.ToString().ToLowerInvariant());
            Publish("outcome", DisplayOutcome(outcome));
            await SendTerminalAsync(outcome).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetStatus(_lifetime.IsCancellationRequested ? "cancelled" : "failed");
            var termination = new
            {
                status = _status,
                code = "run.failed",
                message = "Run could not complete. Check provider configuration and host availability.",
                exceptionType = exception.GetType().Name,
                snapshotUrl = $"/api/runs/{RunId}"
            };
            lock (_gate) _termination = termination;
            Publish("run.terminated", termination);
            await SendTerminalMessageAsync(new ServiceMessage("run.terminated", termination, RunId)).ConfigureAwait(false);
        }
        finally
        {
            feed.Complete();
            await pumping.ConfigureAwait(false);
        }
    }

    private async Task PumpTelemetryAsync(LlmTelemetryFeed feed)
    {
        await foreach (var delivery in feed.ReadAllAsync().ConfigureAwait(false)) Publish("progress", delivery);
    }

    private ToolRegistration CreateRegistration(HostCapability capability) => new(
        new ToolDescriptor(capability.Id, capability.Name, capability.Kind, capability.Effect,
            InputSchema: capability.InputSchema, Description: capability.Description,
            RetrySafety: capability.Effect == ToolEffect.ReadOnly ? ToolRetrySafety.Idempotent : ToolRetrySafety.MutationUnsafe),
        new RemoteTool(this, capability),
        new ToolSecurityDeclaration(capability.Effect, [ToolDataBoundary.HostState],
            [ToolDataBoundary.HostState], ToolExternalOutputClassification.UntrustedStructuredData,
            ToolApprovalRequirement.None,
            capability.Effect == ToolEffect.ReadOnly ? ToolRetrySafety.Idempotent : ToolRetrySafety.MutationUnsafe,
            new ToolProvenance(ToolProvenanceKind.HostAuthored, Request.HostId, ManifestHash)));

    private async Task<ToolResult> InvokeAsync(HostCapability capability, ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PendingAction action;
        IHostConnection? connection;
        var limits = Request.Limits ?? new HostRunLimits();
        lock (_gate)
        {
            if (_actions.Values.Any(a => a.Unresolved || a.Result is null))
                throw new HostProtocolException("action.unresolved", "Reconcile the outstanding action before further dispatch.");
            action = new PendingAction(new HostActionRequest("action_" + Guid.NewGuid().ToString("N"),
                RunId, invocation.RunId, invocation.StepId, Request.SessionId, Request.SessionEpoch,
                capability.Id, ManifestHash, HostProtocol.Element(invocation.Input),
                _context.CurrentObservation.Revision, DateTimeOffset.UtcNow.AddSeconds(limits.ActionTimeoutSeconds)), capability);
            if (JsonSerializer.SerializeToUtf8Bytes(new ServiceMessage("action.request", action.Request, RunId), HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
                ProtocolValidation.Fail("action.bounds", "Proposed host action exceeds the wire bound and was not dispatched.");
            _actions.Add(action.Request.ActionId, action);
            connection = _connection;
            if (connection is not null)
            {
                try { Reserve(action); }
                catch
                {
                    _actions.Remove(action.Request.ActionId);
                    throw;
                }
            }
        }
        SetStatus("awaiting_host");
        Publish("action.pending", action.Request);
        try
        {
            if (connection is not null)
            {
                try { await connection.SendAsync(new ServiceMessage("action.request", action.Request, RunId), token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
                { Detach(connection); }
            }
            var result = await action.Completion.Task.WaitAsync(TimeSpan.FromSeconds(limits.ActionTimeoutSeconds), token).ConfigureAwait(false);
            SetStatus("running");
            return ToolResult(invocation, result);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            lock (_gate)
            {
                action.Unresolved = action.DeliveryAttempted && action.Result is null;
                if (!action.DeliveryAttempted) _actions.Remove(action.Request.ActionId);
            }
            var data = new Dictionary<string, object?>
            {
                ["actionId"] = action.Request.ActionId,
                ["effectUnresolved"] = action.Unresolved,
                ["deliveryAttempted"] = action.DeliveryAttempted
            };
            return new ToolResult(new Receipt("remote_" + action.Request.ActionId, invocation.StepId, invocation.ToolId,
                exception is OperationCanceledException ? ReceiptStatus.Cancelled : ReceiptStatus.Unavailable,
                action.Unresolved ? "Host result is unresolved; reconcile the original action before further effects." : "Host was unavailable before dispatch.",
                DateTimeOffset.UtcNow, data));
        }
    }

    private ToolResult ToolResult(ToolInvocation invocation, HostActionResult result)
    {
        var data = new Dictionary<string, object?>
        {
            ["actionId"] = result.ActionId,
            ["hostEvidenceId"] = result.EvidenceId,
            ["disposition"] = result.Disposition,
            ["beforeRevision"] = result.BeforeRevision,
            ["afterRevision"] = result.AfterRevision,
            ["effectUnresolved"] = result.Disposition == "unresolved",
            ["observationId"] = result.Observation?.ObservationId
        };
        var status = result.Disposition switch
        {
            "applied" => ReceiptStatus.Succeeded,
            "refused" or "conflict" => ReceiptStatus.Refused,
            _ => ReceiptStatus.Unavailable
        };
        var receipt = new Receipt("remote_" + result.ActionId, invocation.StepId, invocation.ToolId,
            status, result.Summary, DateTimeOffset.UtcNow, data);
        var evidence = new[] { new EvidenceRef("receipt", receipt.ReceiptId) };
        var observedData = new Dictionary<string, object?>(data)
        {
            ["observation"] = result.Observation is null ? null : HostProtocol.Element(result.Observation)
        };
        var observation = new Observation("observation_" + result.ActionId, invocation.StepId,
            ObservationKind.ToolResult, result.Summary, observedData, evidence);
        Artifact? artifact = result.Completion is { } complete
            ? new Artifact("completion_" + result.ActionId, "host.objective.completed",
                new Dictionary<string, object?>
                {
                    ["objectiveId"] = complete.ObjectiveId,
                    ["hostEvidenceId"] = complete.EvidenceId,
                    ["actionId"] = result.ActionId,
                    ["summary"] = complete.Summary,
                    ["revision"] = result.AfterRevision
                }, evidence)
            : null;
        return new ToolResult(receipt, observation, artifact);
    }

    private void Reserve(PendingAction action)
    {
        // Persist custody before the first possible delivery. A failed write grants no dispatch.
        _custody?.Reserve(Request.HostId, Request.SessionId, Request.SessionEpoch, RunId,
            action.Request, action.Capability, Request.ObjectiveId);
        action.DeliveryAttempted = true;
    }

    private void SetStatus(string status)
    {
        lock (_gate) { _status = status; _updatedAt = DateTimeOffset.UtcNow; }
        Publish("status", new { status });
    }

    private void Publish(string type, object payload)
    {
        var message = new ServiceMessage(type, payload, RunId);
        if (JsonSerializer.SerializeToUtf8Bytes(message, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
            message = new ServiceMessage(type, new { truncated = true, snapshotUrl = $"/api/runs/{RunId}" }, RunId);
        Events.Publish(message);
        IHostConnection? connection;
        lock (_gate) connection = _connection;
        if (type is not ("outcome" or "run.terminated") && connection is not null)
        {
            try { connection.TrySendProgress(message); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
            { Detach(connection); }
        }
    }

    private Task SendTerminalAsync(OutcomeEnvelope outcome) =>
        SendTerminalMessageAsync(new ServiceMessage("outcome", DisplayOutcome(outcome), RunId));

    private async Task SendTerminalMessageAsync(ServiceMessage message)
    {
        IHostConnection? connection;
        lock (_gate) connection = _connection;
        if (connection is null) return;
        try { await connection.SendAsync(message).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
        { Detach(connection); }
    }

    private object DisplayOutcome(OutcomeEnvelope outcome)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(outcome, HostProtocol.Json).Length <= HostProtocol.MaxMessageBytes / 2) return outcome;
        return new
        {
            outcome = new { outcome.Outcome.RunId, outcome.Outcome.Status, outcome.Outcome.StopReason },
            truncated = true,
            snapshotUrl = $"/api/runs/{RunId}",
            receiptCount = outcome.Receipts.Items.Count,
            observationCount = outcome.Details.Observations.Count
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        // The running task owns its token until completion.
        _ = Completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class RemoteTool(HostRun run, HostCapability capability) : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            run.InvokeAsync(capability, invocation, cancellationToken);
    }

    private sealed class RunSink(HostRun run) : IEventSink
    {
        public void Emit(ExecutionEvent executionEvent) => run.Publish("execution.event", executionEvent);
    }

    private sealed class HostCompletionEvaluator(HostRun run) : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context) => run.HasUnresolvedActions
            ? CompletionEvaluation.Blocked(StopReason.ToolUnavailable, "An external effect remains unresolved.")
            : EvidenceCompletionEvaluator.ForArtifactKind("host.objective.completed").Evaluate(context);
    }
}

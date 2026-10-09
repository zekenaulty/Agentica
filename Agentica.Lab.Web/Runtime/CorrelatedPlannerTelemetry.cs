using System.Runtime.CompilerServices;
using Agentica.Clients.Llm;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Lab.Web.Runtime;

/// <summary>Identity references only; no frame content or provider continuation is retained.</summary>
public sealed record PlannerTelemetryFrame(string FrameId, string Kind, string Version, string? ToolSurfaceId);

public sealed record PlannerTelemetryContext(
    string RunId,
    string? RunnerRunId,
    string ObjectiveId,
    string PlanningOperation,
    string? CurrentPlanId,
    int PlanVersionCount,
    string? AfterStepId,
    IReadOnlyList<PlannerTelemetryFrame> Frames);

public sealed record CorrelatedPlannerTelemetryDelivery(
    LlmTelemetryRecord Record, long DroppedRecords, PlannerTelemetryContext? Context);

/// <summary>
/// Adds invocation identity to the Lab's bounded observational feed. One instance belongs to
/// one sequential runner; it does not own planner sessions, execution receipts, or native history.
/// </summary>
public sealed class CorrelatedPlannerTelemetry : IDisposable
{
    private const int MaximumCallAssociations = 512;
    private readonly object _gate = new();
    private readonly string _runId;
    private readonly string _objectiveId;
    private readonly LlmTelemetryFeed _feed;
    private readonly AsyncLocal<PlannerTelemetryContext?> _invocation = new();
    private readonly Dictionary<string, PlannerTelemetryContext?> _calls = new(StringComparer.Ordinal);
    private readonly Queue<string> _callOrder = new();
    private bool _closed;

    public CorrelatedPlannerTelemetry(string runId, string objectiveId, string provider, bool includeThoughtSummaries = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectiveId);
        _runId = runId;
        _objectiveId = objectiveId;
        _feed = new LlmTelemetryFeed(provider, includeThoughtSummaries: includeThoughtSummaries);
    }

    /// <summary>Pass this callback to the provider planner before wrapping it.</summary>
    public void Report(LlmStreamEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_closed) return;
            // The reader takes this same lock: a newly queued record cannot be read before its
            // call association exists. Repairs get separate call IDs with the same invocation.
            _feed.Report(item);
            if (_feed.Latest is { } record && !_calls.ContainsKey(record.CallId))
            {
                _calls.Add(record.CallId, _invocation.Value);
                _callOrder.Enqueue(record.CallId);
                while (_callOrder.Count > MaximumCallAssociations)
                    _calls.Remove(_callOrder.Dequeue());
            }
        }
    }

    /// <summary>
    /// Preserves the planner's external-boundary marker and its optional runner-owned session
    /// factory. The actual session and its native continuation remain owned by the inner planner.
    /// </summary>
    public IWorkflowPlanner Wrap(IWorkflowPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        if (planner is IWorkflowPlannerSessionFactory factory)
            return planner is IExternalWorkflowPlanner
                ? new ExternalFactoryPlanner(this, planner, factory)
                : new FactoryPlanner(this, planner, factory);
        if (planner is IWorkflowPlannerSession session)
            return WrapSession(session);
        return planner is IExternalWorkflowPlanner
            ? new ExternalPlanner(this, planner)
            : new Planner(this, planner);
    }

    public async IAsyncEnumerable<CorrelatedPlannerTelemetryDelivery> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delivery in _feed.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            PlannerTelemetryContext? context;
            lock (_gate)
                _calls.TryGetValue(delivery.Record.CallId, out context);
            // Missing or pruned associations remain unknown; never borrow another call's frame.
            yield return new CorrelatedPlannerTelemetryDelivery(delivery.Record, delivery.DroppedRecords, context);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _feed.Complete();
        }
    }

    public void Dispose() => Complete();

    private IWorkflowPlannerSession WrapSession(IWorkflowPlannerSession session) =>
        session is IExternalWorkflowPlanner
            ? new ExternalSessionPlanner(this, session)
            : new SessionPlanner(this, session);

    private async Task<WorkflowPlan> InvokeAsync(IWorkflowPlanner planner, PlanningRequest request,
        Observation? observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previous = _invocation.Value;
        _invocation.Value = new PlannerTelemetryContext(
            _runId, request.SessionContext?.RunId, _objectiveId,
            observation is null ? "create" : "refine",
            request.ExecutionContext.CurrentPlanId, request.ExecutionContext.PlanVersionCount,
            observation?.StepId,
            Array.AsReadOnly(request.ContextFrames.Select(frame => new PlannerTelemetryFrame(
                frame.FrameId, frame.Kind, frame.Version, frame.ToolSurfaceId)).ToArray()));
        try
        {
            return observation is null
                ? await planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false)
                : await planner.RefinePlanAsync(request, observation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _invocation.Value = previous;
        }
    }

    private class Planner(CorrelatedPlannerTelemetry owner, IWorkflowPlanner inner) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            owner.InvokeAsync(inner, request, null, cancellationToken);

        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(observation);
            return owner.InvokeAsync(inner, request, observation, cancellationToken);
        }
    }

    private sealed class ExternalPlanner(CorrelatedPlannerTelemetry owner, IWorkflowPlanner inner)
        : Planner(owner, inner), IExternalWorkflowPlanner
    {
    }

    private class FactoryPlanner : Planner, IWorkflowPlannerSessionFactory
    {
        private readonly CorrelatedPlannerTelemetry _owner;
        private readonly IWorkflowPlannerSessionFactory _factory;

        public FactoryPlanner(CorrelatedPlannerTelemetry owner, IWorkflowPlanner inner, IWorkflowPlannerSessionFactory factory)
            : base(owner, inner)
        {
            _owner = owner;
            _factory = factory;
        }

        public IWorkflowPlannerSession BeginSession(PlanningSessionContext context) => _owner.WrapSession(_factory.BeginSession(context));
    }

    private sealed class ExternalFactoryPlanner(CorrelatedPlannerTelemetry owner, IWorkflowPlanner inner,
        IWorkflowPlannerSessionFactory factory) : FactoryPlanner(owner, inner, factory), IExternalWorkflowPlanner
    {
    }

    private class SessionPlanner : Planner, IWorkflowPlannerSession
    {
        private readonly IWorkflowPlannerSession _inner;
        private int _disposed;

        public SessionPlanner(CorrelatedPlannerTelemetry owner, IWorkflowPlannerSession inner) : base(owner, inner)
        {
            _inner = inner;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                _inner.Dispose();
        }
    }

    private sealed class ExternalSessionPlanner(CorrelatedPlannerTelemetry owner, IWorkflowPlannerSession inner)
        : SessionPlanner(owner, inner), IExternalWorkflowPlanner
    {
    }
}

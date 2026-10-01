using Agentica.Clients.Llm;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Clients.Planning;

/// <summary>One host run attempt owns one private carrier. No carrier escapes into plans,
/// tool calls, receipts, or generic serialization.</summary>
internal sealed class LlmPlanningSession : IWorkflowPlannerSession, IExternalWorkflowPlanner
{
    private readonly object _gate = new();
    private readonly PlanningSessionContext _context;
    private readonly LlmPlannerOptions _options;
    private readonly LlmWorkflowPlanner _planner;
    private readonly CancellationTokenSource _lifetime = new();
    private LlmNativeContinuation? _continuation;
    private string? _modelId;
    private string? _instruction;
    private string? _provider;
    private bool _inFlight;
    private bool _disposed;

    public LlmPlanningSession(ILlmClient client, LlmPlannerOptions options,
        Action<LlmStreamEvent>? observer, PlanningSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Objective);
        _context = context;
        _options = options;
        _planner = new LlmWorkflowPlanner(client, options, observer, this);
    }

    public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, token => _planner.CreatePlanAsync(request, token), cancellationToken);

    public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, token => _planner.RefinePlanAsync(request, observation, token), cancellationToken);

    private async Task<WorkflowPlan> ExecuteAsync(PlanningRequest request,
        Func<CancellationToken, Task<WorkflowPlan>> execute, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inFlight) throw new InvalidOperationException("A planning session permits one call at a time.");
            if (request.SessionContext != _context ||
                request.Request.Objective != _context.Objective ||
                request.Request.Origin != _context.Origin ||
                request.Request.AuthorizationScopeId != _context.AuthorizationScopeId)
                throw new LlmPlannerException("Planning session run, objective, or authority binding mismatched.");
            _inFlight = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            return await execute(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            Clear();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = false;
                if (_disposed)
                {
                    Clear();
                    _lifetime.Dispose();
                }
            }
        }
    }

    internal LlmRequest Prepare(LlmRequest request)
    {
        var instruction = string.Join("\n\n", request.Messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content).Where(value => !string.IsNullOrWhiteSpace(value)));
        var disposition = "none";
        if (_continuation is not null &&
            (_continuation.ModelId != request.ModelId ||
             _continuation.SystemInstruction != instruction ||
             _continuation.ProviderName != _provider))
        {
            Clear();
            disposition = "binding_reset";
        }
        _modelId = request.ModelId;
        _instruction = instruction;
        if (_continuation is not null)
        {
            var candidate = request with { NativeContinuation = _continuation };
            if (request.Messages.Sum(message => (long)message.Content.Length) +
                    _continuation.HistoryStepsJson.Length <= _options.MaxInputCharacters &&
                PlanningPromptCompiler.RepairFitsTokens(candidate, _options))
            {
                request = candidate;
                disposition = "native";
            }
            else
            {
                Clear();
                disposition = "budget_reset";
            }
        }
        var metadata = request.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        metadata["agentica.planner.continuation"] = disposition;
        var receipt = request.InputCompilationReceipt;
        if (receipt is not null)
        {
            var native = request.NativeContinuation;
            receipt = receipt with
            {
                InputCharacters = checked(receipt.InputCharacters + (native?.HistoryStepsJson.Length ?? 0)),
                InputSha256 = PlanningPromptCompiler.ComputeInputHash(
                    request.Messages.Select(message => message.Content), native?.HistoryStepsJson),
                EstimatedInputTokens = _options.ContextWindowBudget is null ? null :
                    _options.InputTokenEstimator.EstimateTokens(request),
                Decisions = receipt.Decisions.Append(new LlmInputDecision(
                    "nativeContinuation", _context.RunId, native is not null, disposition)).ToArray()
            };
            metadata["agentica.planner.inputSha256"] = receipt.InputSha256;
            metadata["agentica.planner.inputCharacters"] = receipt.InputCharacters.ToString();
            if (receipt.EstimatedInputTokens is { } estimated)
                metadata["agentica.planner.estimatedInputTokens"] = estimated.ToString();
        }
        return request with { Metadata = metadata, InputCompilationReceipt = receipt };
    }

    internal void Retain(LlmNativeContinuation? continuation)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (continuation is not null &&
            (continuation.ModelId != _modelId || continuation.SystemInstruction != _instruction ||
             _provider is not null && continuation.ProviderName != _provider ||
             continuation.HistoryStepsJson.Length > _options.MaxInputCharacters))
        {
            continuation.Dispose();
            continuation = null;
        }
        if (!ReferenceEquals(_continuation, continuation)) Clear();
        _continuation = continuation;
        if (continuation is not null) _provider = continuation.ProviderName;
    }

    internal bool Owns(LlmNativeContinuation continuation) => ReferenceEquals(_continuation, continuation);

    private void Clear()
    {
        _continuation?.Dispose();
        _continuation = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            // An active adapter may still read the carrier while unwinding cancellation.
            if (!_inFlight)
            {
                Clear();
                _lifetime.Dispose();
            }
        }
    }
}

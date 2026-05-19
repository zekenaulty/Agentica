using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Orchestration.Execution;

public sealed class InProcessAgenticaRunExecutor : IRunExecutor
{
    private readonly Func<RunRequest, IWorkflowPlanner> _plannerFactory;
    private readonly Func<RunRequest, Agentica.Tools.ToolCatalog> _toolCatalogFactory;
    private readonly Agentica.Events.IEventSink _eventSink;
    private readonly IOutcomeReporter _outcomeReporter;
    private readonly Func<RunRequest, ExecutionPolicy> _policyFactory;

    public InProcessAgenticaRunExecutor(
        Func<RunRequest, IWorkflowPlanner> plannerFactory,
        Func<RunRequest, Agentica.Tools.ToolCatalog> toolCatalogFactory,
        Agentica.Events.IEventSink eventSink,
        IOutcomeReporter outcomeReporter,
        Func<RunRequest, ExecutionPolicy>? policyFactory = null)
    {
        _plannerFactory = plannerFactory;
        _toolCatalogFactory = toolCatalogFactory;
        _eventSink = eventSink;
        _outcomeReporter = outcomeReporter;
        _policyFactory = policyFactory ?? (_ => new ExecutionPolicy());
    }

    public Task<OutcomeEnvelope> RunAsync(
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        var runner = new AgenticaRunner(
            _plannerFactory(request),
            _toolCatalogFactory(request),
            _eventSink,
            _outcomeReporter,
            _policyFactory(request));
        return runner.RunAsync(request, cancellationToken);
    }
}

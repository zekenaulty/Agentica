using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Lab.Scenarios.Campaign;

public sealed record CampaignRunnerOptions(
    int MaxRuns = 16,
    int MaxStepsPerRun = 8,
    int MaxRefinementsPerRun = 4,
    PlanningMode PlanningMode = PlanningMode.PlanOnly,
    int MaxBlockedRetries = 0);

public sealed record CampaignRunResult(
    CampaignDefinition Definition,
    CampaignState State,
    IReadOnlyList<OutcomeEnvelope> Envelopes);

public sealed class CampaignRunner
{
    private readonly CampaignDefinition _definition;
    private readonly CampaignState _state;
    private readonly Func<CampaignMilestone, IWorkflowPlanner> _plannerFactory;
    private readonly Func<ToolCatalog> _toolCatalogFactory;
    private readonly IOutcomeReporter _outcomeReporter;
    private readonly IEventSink _eventSink;
    private readonly Func<IReadOnlyDictionary<string, object?>> _hostStateProjection;
    private readonly CampaignRunnerOptions _options;

    public CampaignRunner(
        CampaignDefinition definition,
        CampaignState state,
        Func<CampaignMilestone, IWorkflowPlanner> plannerFactory,
        Func<ToolCatalog> toolCatalogFactory,
        IOutcomeReporter outcomeReporter,
        IEventSink eventSink,
        Func<IReadOnlyDictionary<string, object?>> hostStateProjection,
        CampaignRunnerOptions? options = null)
    {
        _definition = definition;
        _state = state;
        _plannerFactory = plannerFactory;
        _toolCatalogFactory = toolCatalogFactory;
        _outcomeReporter = outcomeReporter;
        _eventSink = eventSink;
        _hostStateProjection = hostStateProjection;
        _options = options ?? new CampaignRunnerOptions();
    }

    public async Task<CampaignRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var envelopes = new List<OutcomeEnvelope>();
        _state.Status = CampaignRunStatus.Running;

        for (var runNumber = 0; runNumber < _options.MaxRuns; runNumber++)
        {
            var available = CampaignGraph.AvailableMilestones(_definition, _state);
            _state.AvailableMilestones.Clear();
            _state.AvailableMilestones.AddRange(available.Select(milestone => milestone.MilestoneId));

            if (CampaignGraph.RequiredMilestonesComplete(_definition, _state))
            {
                _state.Status = CampaignRunStatus.Succeeded;
                _state.ActiveMilestoneId = null;
                _state.ProgressSnapshot = CampaignProgressSnapshotCompiler.Compile(
                    _definition,
                    _state,
                    null,
                    envelopes.LastOrDefault(),
                    _hostStateProjection());
                _state.WorkingContext = CampaignWorkingContextCompiler.Compile(
                    _definition,
                    null,
                    _state.ProgressSnapshot,
                    envelopes.LastOrDefault(),
                    []);
                return new CampaignRunResult(_definition, _state, envelopes);
            }

            var milestone = available.FirstOrDefault(milestone => !milestone.Optional) ?? available.FirstOrDefault();
            if (milestone is null)
            {
                _state.Status = CampaignRunStatus.Blocked;
                _state.ProgressSnapshot = CampaignProgressSnapshotCompiler.Compile(
                    _definition,
                    _state,
                    null,
                    envelopes.LastOrDefault(),
                    _hostStateProjection(),
                    ["No available milestone can advance the campaign."]);
                _state.WorkingContext = CampaignWorkingContextCompiler.Compile(
                    _definition,
                    null,
                    _state.ProgressSnapshot,
                    envelopes.LastOrDefault(),
                    ["No available milestone can advance the campaign."]);
                return new CampaignRunResult(_definition, _state, envelopes);
            }

            _state.ActiveMilestoneId = milestone.MilestoneId;
            var request = new RunRequest(
                milestone.Objective,
                RequestOrigin.Agent,
                BuildRunContext(milestone));
            var planner = _plannerFactory(milestone);
            var runner = new AgenticaRunner(
                planner,
                _toolCatalogFactory(),
                _eventSink,
                _outcomeReporter,
                new ExecutionPolicy(
                    MaxSteps: _options.MaxStepsPerRun,
                    MaxRefinements: _options.MaxRefinementsPerRun,
                    PlanningMode: _options.PlanningMode,
                    MaxBlockedRetries: _options.MaxBlockedRetries,
                    SecurityPolicy: LabSecurityPolicy.ForPlanner(planner)),
                PlanExhaustionCompletionEvaluator.Instance);

            var envelope = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            envelopes.Add(envelope);

            var acceptance = CampaignAcceptance.Evaluate(milestone, envelope, _hostStateProjection());
            if (!acceptance.Accepted)
            {
                _state.BlockedMilestones.Add(milestone.MilestoneId);
                _state.Status = envelope.Outcome.Status is RunOutcomeStatus.Failed or RunOutcomeStatus.PlanInvalid or RunOutcomeStatus.Cancelled
                    ? CampaignRunStatus.Failed
                    : CampaignRunStatus.Blocked;
                _state.ProgressSnapshot = CampaignProgressSnapshotCompiler.Compile(
                    _definition,
                    _state,
                    milestone,
                    envelope,
                    _hostStateProjection(),
                    acceptance.Blockers);
                _state.WorkingContext = CampaignWorkingContextCompiler.Compile(
                    _definition,
                    milestone,
                    _state.ProgressSnapshot,
                    envelope,
                    acceptance.Blockers);
                return new CampaignRunResult(_definition, _state, envelopes);
            }

            _state.CompletedMilestones.Add(milestone.MilestoneId);
            _state.PriorRunRefs.Add(new CampaignPriorRunRef(
                milestone.MilestoneId,
                envelope.Outcome.RunId,
                envelope.Outcome.Status,
                acceptance.Evidence));
            _state.ProgressSnapshot = CampaignProgressSnapshotCompiler.Compile(
                _definition,
                _state,
                milestone,
                envelope,
                _hostStateProjection());
            _state.WorkingContext = CampaignWorkingContextCompiler.Compile(
                _definition,
                milestone,
                _state.ProgressSnapshot,
                envelope,
                []);
        }

        _state.Status = CampaignRunStatus.Blocked;
        _state.ProgressSnapshot = CampaignProgressSnapshotCompiler.Compile(
            _definition,
            _state,
            null,
            envelopes.LastOrDefault(),
            _hostStateProjection(),
            ["Maximum campaign run count reached."]);
        _state.WorkingContext = CampaignWorkingContextCompiler.Compile(
            _definition,
            null,
            _state.ProgressSnapshot,
            envelopes.LastOrDefault(),
            ["Maximum campaign run count reached."]);
        return new CampaignRunResult(_definition, _state, envelopes);
    }

    private IReadOnlyDictionary<string, object?> BuildRunContext(CampaignMilestone milestone)
    {
        var context = new Dictionary<string, object?>(milestone.ContextProjection, StringComparer.Ordinal)
        {
            ["campaign.progress"] = _state.ProgressSnapshot,
            ["campaign.workingContext"] = _state.WorkingContext,
            ["campaign.milestoneId"] = milestone.MilestoneId,
            ["campaign.completedMilestones"] = _state.CompletedMilestones.ToArray()
        };
        return context;
    }
}

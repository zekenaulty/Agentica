using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Projection;
using Agentica.Requests;
using Agentica.Tools;
using System.Diagnostics;

namespace Agentica.NyxLearningHost;

public enum LearningScenario { ExactSource, EffectRecovery }

public sealed record LearningOptions(
    bool MirrorAvailable = true,
    bool MirrorExact = true,
    bool ReconcileKnown = true,
    bool InertShadow = false,
    bool WrongPerspectiveProposal = false,
    int CumulativeStepsUsed = 3,
    int ParentStepLimit = 12);

public sealed record LearningMetrics(
    int ProviderFixtureCalls,
    int PlannerUserPromptCharacters,
    int RuntimeReceipts,
    int NewDomainEffectDispatches,
    int CumulativeStepsBefore,
    int CumulativeStepsAfter,
    int ParentStepLimit,
    double? FirstFixtureTextMilliseconds,
    double ElapsedMilliseconds,
    decimal? ProviderCost = null,
    int? ProviderTokens = null,
    string WarmReuse = "NOT-MEASURED: no cache enabled");

public sealed record LearningReport(
    LearningScenario Scenario,
    bool InertShadow,
    string EvidenceLevel,
    OutcomeEnvelope? Outcome,
    ObligationFulfillment Fulfillment,
    TaskAcceptanceResult Acceptance,
    ObligationSatisfaction ReferenceSatisfaction,
    bool MatchesReference,
    LearningMetrics Metrics,
    IReadOnlyList<LlmStreamEventKind> StreamOrder,
    IReadOnlyList<string> ProviderPrompts,
    IReadOnlyList<string> InvokedTools,
    bool PrimaryBindingPreserved);

/// <summary>Independent disposable experiment; no environment credentials, network or production stores.</summary>
public static class LearningExperiment
{
    public static async Task<LearningReport> RunAsync(
        LearningScenario scenario, LearningOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        options ??= new();
        if (options.CumulativeStepsUsed < 0 || options.ParentStepLimit is < 1 or > 100 ||
            options.CumulativeStepsUsed >= options.ParentStepLimit)
            throw new ArgumentException("No valid remaining parent step allowance.", nameof(options));

        var watch = Stopwatch.StartNew();
        var host = new ExperimentHost(scenario, options);
        var client = new LearningFixtureClient(options.WrongPerspectiveProposal);
        var order = new List<LlmStreamEventKind>();
        double? firstUseful = null;
        var planner = new LlmWorkflowPlanner(client,
            new LlmPlannerOptions("fixture.obligation.v1", InvalidJsonRepairAttempts: 0),
            onStreamEvent: item =>
            {
                order.Add(item.Kind);
                if (item.Kind == LlmStreamEventKind.TextDelta) firstUseful ??= watch.Elapsed.TotalMilliseconds;
            });
        var projector = new ObligationPlanningFrameProjector(_ => host.State());
        var request = new RunRequest(host.Objective, Context: new Dictionary<string, object?>
        {
            ["parentStepLimit"] = options.ParentStepLimit,
            ["cumulativeStepsUsed"] = options.CumulativeStepsUsed,
            ["referenceCorpus"] = "nyx-learning.synthetic.v1"
        }, AuthorizationScopeId: ExperimentHost.Scope);
        OutcomeEnvelope? outcome = null;
        TaskAcceptanceResult acceptance;
        if (options.InertShadow)
        {
            var initial = new PlanningRequest(request, host.Catalog.Descriptors, [], [])
            {
                ContextFrames = projector.Project(new("shadow", 1, request,
                    PlanningExecutionContext.Empty, host.Catalog.Descriptors, [], [], null))
            };
            // A typed proposal is retained, but no runner/executor is created for this branch.
            _ = await planner.CreatePlanAsync(initial, cancellationToken).ConfigureAwait(false);
            acceptance = new(TaskAcceptanceStatus.Blocked, ["Shadow proposal is inert."], []);
        }
        else
        {
            var runner = new AgenticaRunner(planner, host.Catalog, new InMemoryEventSink(),
                new DeterministicOutcomeReporter(),
                new ExecutionPolicy(MaxSteps: options.ParentStepLimit - options.CumulativeStepsUsed,
                    MaxRefinements: 4, MaxBlockedRetries: 0, Timeout: TimeSpan.FromSeconds(10),
                    EvaluateCompletionAfterEachBatch: true,
                    SecurityPolicy: new ToolSecurityPolicy(ExternalPlannerAllowedBoundaries:
                        [ToolDataBoundary.UserContent, ToolDataBoundary.HostState])),
                host, projector);
            outcome = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            var decision = host.HostDecision(outcome);
            acceptance = ObligationFulfillmentAdapter.Evaluate(host.State(),
                host.Candidate ?? host.State(), outcome, decision);
        }

        var fulfillment = host.Candidate ?? host.State();
        var reference = options.InertShadow
            ? scenario == LearningScenario.EffectRecovery ? ObligationSatisfaction.Indeterminate : ObligationSatisfaction.Unsatisfied
            : options.WrongPerspectiveProposal
                ? ObligationSatisfaction.Unsatisfied
                : scenario == LearningScenario.ExactSource
                    ? options.MirrorAvailable && options.MirrorExact
                        ? ObligationSatisfaction.Satisfied : ObligationSatisfaction.Unsatisfied
                    : options.ReconcileKnown ? ObligationSatisfaction.Satisfied : ObligationSatisfaction.Indeterminate;
        var accepted = acceptance.Status == TaskAcceptanceStatus.Accepted;
        var matches = (reference == ObligationSatisfaction.Satisfied) == accepted &&
            (options.WrongPerspectiveProposal || fulfillment.Satisfaction == reference) &&
            host.NewDomainEffects == 0;
        watch.Stop();
        return new(scenario, options.InertShadow,
            options.InertShadow ? "OFFLINE: streaming fixture proposal only; no executor"
                : "OFFLINE: deterministic streaming provider fixture and real Agentica loop",
            outcome, fulfillment, acceptance, reference, matches,
            new(client.Prompts.Count, client.Prompts.Sum(prompt => prompt.Length),
                outcome?.Receipts.Items.Count ?? 0, host.NewDomainEffects, options.CumulativeStepsUsed,
                options.CumulativeStepsUsed + (outcome?.Receipts.Items.Count ?? 0),
                options.ParentStepLimit, firstUseful, watch.Elapsed.TotalMilliseconds),
            Array.AsReadOnly(order.ToArray()), Array.AsReadOnly(client.Prompts.ToArray()),
            Array.AsReadOnly(host.InvokedTools.ToArray()), host.PrimaryBindingPreserved);
    }
}

internal sealed class ExperimentHost : ICompletionEvaluator
{
    internal const string Scope = "learning.scope";
    private const string ExactText = "Use evening delivery.";
    private readonly LearningScenario _scenario;
    private readonly LearningOptions _options;
    private PresentedEvidenceSegment[] _presented;
    private ObligationAttemptReference[] _attempts;
    private ObligationCoverage _coverage;
    private bool _semanticAccepted;
    private bool _submissionObserved;
    private PresentedEvidenceCitation[] _acceptedCitations = [];
    private readonly ObligationIdentity _identity;

    public ExperimentHost(LearningScenario scenario, LearningOptions options)
    {
        _scenario = scenario;
        _options = options;
        _identity = new("learning.obligation", "learning.objective",
            scenario == LearningScenario.ExactSource ? "exact-primary-passage" : "accept-item-once", "1",
            scenario == LearningScenario.ExactSource ? "synthetic.primary" : "synthetic.item", "4", "learning.parent");
        _presented = scenario == LearningScenario.ExactSource ? [] :
        [
            new("state", new("host-observation", "inventory.4"), "4", 0, "Item is accepted.", "host.inventory"),
            AttemptSegment(false)
        ];
        _attempts = scenario == LearningScenario.ExactSource ? [] :
        [
            new("original.accept", "host.inventory", "inventory.accept.v1",
                ObligationExecutionState.Cancelled, ObligationEffectCustody.OutcomeUnknown)
        ];
        _coverage = new(scenario == LearningScenario.ExactSource
            ? ObligationCoverageStatus.Unknown : ObligationCoverageStatus.Complete, "required-source-cut.v1", "corpus.1");
        Catalog = ToolCatalog.Create(
            Register("evidence.primary", ToolKind.Query, ToolEffect.ReadOnly),
            Register("evidence.mirror", ToolKind.Query, ToolEffect.ReadOnly),
            Register("attempt.reconcile", ToolKind.Query, ToolEffect.ReadOnly),
            Register("obligation.submit", ToolKind.Action, ToolEffect.WritesLocalState));
    }

    public ToolCatalog Catalog { get; }
    public string Objective => _scenario == LearningScenario.ExactSource
        ? "Return the exact attributable primary revision-4 passage; use a legitimate alternate source when the bound primary connection is unavailable."
        : "Fulfill the original accept-item-once obligation by reconciling its lost response; never redispatch an unknown effect.";
    public List<string> InvokedTools { get; } = [];
    public int NewDomainEffects => InvokedTools.Count(id => id == "inventory.accept");
    public bool PrimaryBindingPreserved => Catalog.Resolve("evidence.primary") is not null;
    public ObligationFulfillment? Candidate { get; private set; }

    public ObligationFulfillment State()
    {
        var manifest = new ObligationInputManifest(_identity, Objective, Scope,
            _scenario == LearningScenario.ExactSource ? "document.reader" : "inventory.operator",
            _presented, _coverage, "learning-compiler.v1", "presented-evidence.v1",
            "interpretation.v1", "grounded-policy.v1");
        var unknown = CustodyUnresolved();
        return new(manifest, unknown ? ObligationSatisfaction.Indeterminate :
                _semanticAccepted ? ObligationSatisfaction.Satisfied : ObligationSatisfaction.Unsatisfied,
            unknown ? ObligationResolutionState.Indeterminate :
                _semanticAccepted || !_submissionObserved ? ObligationResolutionState.Ready : ObligationResolutionState.Unavailable,
            _acceptedCitations,
            _semanticAccepted ? [] : [_scenario == LearningScenario.ExactSource ? "exact.primary-passage" : "original.attempt-proof"],
            _attempts);
    }

    public TaskAcceptanceResult HostDecision(OutcomeEnvelope outcome)
    {
        var artifact = outcome.Details.Artifacts.FirstOrDefault(item => item.Kind == "obligation.fulfillment");
        var current = State();
        return new(current.Satisfaction == ObligationSatisfaction.Satisfied && artifact is not null
                ? TaskAcceptanceStatus.Accepted : CustodyUnresolved() ? TaskAcceptanceStatus.Blocked : TaskAcceptanceStatus.Rejected,
            [current.Satisfaction == ObligationSatisfaction.Satisfied
                ? "The current host contract is satisfied." : "The current host contract remains unmet."],
            artifact is null ? [] : [new("artifact", artifact.ArtifactId)]);
    }

    public CompletionEvaluation Evaluate(CompletionContext context)
    {
        var artifact = context.Artifacts.FirstOrDefault(item => item.Kind == "obligation.fulfillment");
        if (artifact is null) return CompletionEvaluation.Continue("No submitted interpretation artifact.");
        var current = State();
        if (current.Satisfaction != ObligationSatisfaction.Satisfied ||
            current.Resolution != ObligationResolutionState.Ready || current.ResidualRequirements.Count != 0)
            return CompletionEvaluation.Partial("Source contract or original effect custody remains unresolved.");
        var sourceTool = _scenario == LearningScenario.ExactSource ? "evidence.mirror" : "attempt.reconcile";
        var receipt = context.Receipts.LastOrDefault(item => item.ToolId == sourceTool && item.Status == ReceiptStatus.Succeeded);
        var observation = context.Observations.LastOrDefault(item =>
            receipt is not null && item.StepId == receipt.StepId &&
            item.Evidence.Contains(new EvidenceRef("receipt", receipt.ReceiptId)));
        if (receipt is null || observation is null)
            return CompletionEvaluation.Failed(StopReason.CompletionNotSatisfied, "No canonical source/result proof.");
        return CompletionEvaluation.Complete(new("artifact", artifact.ArtifactId),
            new("receipt", receipt.ReceiptId), new("observation", observation.ObservationId));
    }

    private ToolRegistration Register(string id, ToolKind kind, ToolEffect effect)
    {
        var fields = id == "obligation.submit"
            ? ToolInputSchema.Create(new("answer", Required: true), new("segmentId", Required: true),
                new("perspectiveId", Required: true))
            : ToolInputSchema.Create();
        return new(new(id, id, kind, effect, InputSchema: fields,
                Description: "Bound synthetic learning capability; preserves the original obligation.",
                RetrySafety: ToolRetrySafety.Idempotent),
            new ExperimentTool(this),
            new ToolSecurityDeclaration(effect, [ToolDataBoundary.HostState], [ToolDataBoundary.HostState],
                ToolExternalOutputClassification.None, ToolApprovalRequirement.None, ToolRetrySafety.Idempotent,
                new(ToolProvenanceKind.HostAuthored, "isolated-learning-host", "1")));
    }

    private ToolResult Execute(ToolInvocation invocation)
    {
        if (invocation.AuthorizationScopeId != Scope)
            throw new UnauthorizedAccessException("The fixture scope is not admitted.");
        InvokedTools.Add(invocation.ToolId);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (invocation.ToolId)
        {
            case "evidence.primary":
                data["stage"] = "primary.unavailable";
                data["bound"] = true;
                data["runtimeReady"] = false;
                break;
            case "evidence.mirror":
                data["stage"] = _options.MirrorAvailable ? "mirror.available" : "mirror.unavailable";
                if (_options.MirrorAvailable)
                {
                    _presented =
                    [
                        new("primary-passage", new("document", _options.MirrorExact ? "synthetic.primary" : "synthetic.secondary"),
                            _options.MirrorExact ? "4" : "1", 0, ExactText, "speaker.author")
                    ];
                    _coverage = _coverage with
                    {
                        Status = _options.MirrorExact
                            ? ObligationCoverageStatus.Complete : ObligationCoverageStatus.Partial
                    };
                    data["sourceId"] = _presented[0].Source.RefId;
                    data["sourceRevision"] = _presented[0].SourceRevision;
                    data["text"] = _presented[0].Text;
                }
                break;
            case "attempt.reconcile":
                data["stage"] = _options.ReconcileKnown ? "attempt.confirmed" : "attempt.unknown";
                data["originalAttemptId"] = "original.accept";
                if (_options.ReconcileKnown)
                {
                    _attempts = [_attempts[0] with { EffectCustody = ObligationEffectCustody.Confirmed }];
                    _presented = [_presented[0], AttemptSegment(true)];
                }
                data["effectCustody"] = _attempts[0].EffectCustody.ToString();
                break;
            case "obligation.submit":
                return Submit(invocation);
            default:
                throw new InvalidOperationException("No installed fixture capability matches.");
        }
        var receipt = Receipt(invocation, data);
        var observation = new Observation("source-observation." + invocation.StepId, invocation.StepId, ObservationKind.ToolResult,
            "Observed source/result state.", data, [new("receipt", receipt.ReceiptId)]);
        return new(receipt, observation);
    }

    private ToolResult Submit(ToolInvocation invocation)
    {
        var answer = invocation.Input["answer"] as string ?? string.Empty;
        var segmentId = invocation.Input["segmentId"] as string ?? string.Empty;
        var perspective = invocation.Input["perspectiveId"] as string ?? string.Empty;
        var state = State();
        var source = state.Manifest.PresentedEvidence.FirstOrDefault(segment => segment.SegmentId == segmentId);
        var unknown = CustodyUnresolved();
        _semanticAccepted = perspective == state.Manifest.PerspectiveId && source is not null &&
            (_scenario == LearningScenario.ExactSource
                ? source.Source.RefId == "synthetic.primary" && source.SourceRevision == "4" &&
                    answer == ExactText && _coverage.Status == ObligationCoverageStatus.Complete
                : !unknown && source.Source.RefId == "original.accept" && answer == "original-confirmed");
        _submissionObserved = true;
        // Only this independent host check may discharge the host's residual requirement.
        _acceptedCitations = _semanticAccepted && source is not null
            ? [new(source.SegmentId, source.StartUtf16, source.Text.Length)] : [];
        var current = State();
        Candidate = new(current.Manifest, current.Satisfaction, current.Resolution,
            source is null ? [] : [new(source.SegmentId, source.StartUtf16, source.Text.Length)],
            current.ResidualRequirements, current.Attempts);
        var data = new Dictionary<string, object?>
        {
            ["contentIdentity"] = state.Manifest.ContentIdentity,
            ["satisfaction"] = current.Satisfaction.ToString(),
            ["answer"] = answer
        };
        var receipt = Receipt(invocation, data);
        return new(receipt, Artifact: new("candidate-artifact." + invocation.StepId, "obligation.fulfillment", data,
            [new("receipt", receipt.ReceiptId)]));
    }

    private bool CustodyUnresolved() => _attempts.Any(attempt =>
        attempt.Execution is ObligationExecutionState.Active or ObligationExecutionState.OutcomeUnknown ||
        attempt.EffectCustody is ObligationEffectCustody.Admitted or ObligationEffectCustody.OutcomeUnknown);

    private static PresentedEvidenceSegment AttemptSegment(bool confirmed) =>
        new("original-attempt", new("host-attempt", "original.accept"), confirmed ? "2" : "1", 0,
            confirmed ? "The original accept attempt is confirmed." : "The original accept effect is unknown.",
            "host.inventory");

    private static Receipt Receipt(ToolInvocation invocation, IReadOnlyDictionary<string, object?> data) =>
        new("source-receipt." + invocation.StepId, invocation.StepId, invocation.ToolId, ReceiptStatus.Succeeded,
            "The bound fixture operation returned data; this is not objective completion.", DateTimeOffset.UtcNow, data);

    private sealed class ExperimentTool(ExperimentHost host) : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(host.Execute(invocation));
        }
    }
}


using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class BlockedRetryPolicyTests
{
    [Fact]
    public async Task Retry_preserves_the_complete_prior_attempt_envelope()
    {
        var planner = new SequencePlanner(
            Plan(
                Step("step_proof", "proof.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_blocked", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_done", "done.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = ToolCatalog.Create(
            Register("proof.read", ToolKind.Query, ToolEffect.ReadOnly, new ProofTool()),
            Register("blocked.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Unavailable)),
            Register("done.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)));

        var envelope = await Runner(planner, catalog).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var prior = Assert.Single(envelope.PriorAttempts);
        Assert.Equal(RunOutcomeStatus.Blocked, prior.Outcome.Status);
        Assert.Equal(StopReason.ToolUnavailable, prior.Outcome.StopReason);
        Assert.Equal(2, prior.Receipts.Items.Count);
        Assert.Single(prior.Details.Observations);
        Assert.Single(prior.Details.Artifacts);
        Assert.NotEmpty(prior.Details.Events);
        Assert.Empty(prior.PriorAttempts);

        Assert.Equal(2, envelope.Details.RunAttempts.Count);
        Assert.Equal(prior.Outcome.RunId, envelope.Details.RunAttempts[0].RunId);
        Assert.Equal(envelope.Outcome.RunId, envelope.Details.RunAttempts[1].RunId);
        AssertEvidenceReferencesResolve(prior);
        AssertEvidenceReferencesResolve(envelope);

        var json = JsonSerializer.Serialize(envelope, JsonOptions());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("priorAttempts").GetArrayLength());
        Assert.Equal(
            prior.Outcome.RunId,
            document.RootElement.GetProperty("priorAttempts")[0].GetProperty("outcome").GetProperty("runId").GetString());
    }

    [Fact]
    public async Task Multiple_retries_preserve_prior_attempts_in_chronological_order()
    {
        var planner = new SequencePlanner(
            Plan(Step("step_blocked_1", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_blocked_2", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_blocked_3", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = ToolCatalog.Create(Register(
            "blocked.read",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            new StatusTool(ReceiptStatus.Unavailable, ReceiptStatus.Unavailable, ReceiptStatus.Unavailable)));

        var envelope = await Runner(planner, catalog).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(3, planner.CreatePlanCount);
        Assert.Equal(2, envelope.PriorAttempts.Count);
        Assert.Equal(3, envelope.Details.RunAttempts.Count);
        Assert.Equal(
            envelope.Details.RunAttempts.Select(attempt => attempt.RunId).Take(2),
            envelope.PriorAttempts.Select(attempt => attempt.Outcome.RunId));
        Assert.Equal(envelope.Details.RunAttempts[^1].RunId, envelope.Outcome.RunId);
    }

    [Fact]
    public void Retry_policy_snapshots_mutable_authorization_inputs()
    {
        var reasons = new HashSet<StopReason> { StopReason.ToolUnavailable };
        var toolIds = new HashSet<string>(StringComparer.Ordinal) { "state.mutate" };
        var policy = new BlockedRetryPolicy(reasons, toolIds);

        reasons.Add(StopReason.ToolRefused);
        toolIds.Add("state.other");

        Assert.DoesNotContain(StopReason.ToolRefused, policy.RetryableStopReasons);
        Assert.DoesNotContain("state.other", policy.AuthorizedMutationToolIds);
    }

    [Fact]
    public async Task Refused_is_not_default_retryable_and_has_a_distinct_stop_reason()
    {
        var planner = new SequencePlanner(
            Plan(Step("step_refused", "refused.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_done", "done.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = ToolCatalog.Create(
            Register("refused.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Refused)),
            Register("done.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)));

        var envelope = await Runner(planner, catalog).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolRefused, envelope.Outcome.StopReason);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Empty(envelope.PriorAttempts);
    }

    [Fact]
    public async Task Refused_read_only_attempt_can_retry_only_when_the_reason_is_explicitly_allowed()
    {
        var planner = new SequencePlanner(
            Plan(Step("step_refused", "refused.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_done", "done.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = ToolCatalog.Create(
            Register("refused.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Refused)),
            Register("done.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)));
        var policy = Policy(blockedRetries: new BlockedRetryPolicy([StopReason.ToolRefused]));

        var envelope = await Runner(planner, catalog, policy).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, planner.CreatePlanCount);
        Assert.Single(envelope.PriorAttempts);
    }

    [Fact]
    public async Task Mutation_retry_is_off_by_default()
    {
        var mutation = new StatusTool(ReceiptStatus.Succeeded);
        var planner = MutationThenBlockedPlanner();
        var catalog = MutationCatalog(mutation);

        var envelope = await Runner(planner, catalog).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(1, mutation.ExecutionCount);
        Assert.Empty(envelope.PriorAttempts);
    }

    [Fact]
    public async Task Idempotency_declaration_without_exact_tool_authorization_does_not_retry()
    {
        var mutation = new StatusTool(ReceiptStatus.Succeeded);
        var planner = MutationThenBlockedPlanner();
        var catalog = MutationCatalog(mutation, ToolRetrySafety.Idempotent);

        var envelope = await Runner(planner, catalog).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(1, mutation.ExecutionCount);
    }

    [Theory]
    [InlineData(ToolRetrySafety.Unknown)]
    [InlineData(ToolRetrySafety.Additive)]
    [InlineData(ToolRetrySafety.MutationUnsafe)]
    [InlineData(ToolRetrySafety.Destructive)]
    public async Task Exact_tool_authorization_without_idempotency_does_not_retry(ToolRetrySafety retrySafety)
    {
        var mutation = new StatusTool(ReceiptStatus.Succeeded);
        var planner = MutationThenBlockedPlanner();
        var catalog = MutationCatalog(mutation, retrySafety);
        var policy = Policy(blockedRetries: new BlockedRetryPolicy(
            authorizedMutationToolIds: ["state.mutate"]));

        var envelope = await Runner(planner, catalog, policy).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(1, mutation.ExecutionCount);
    }

    [Fact]
    public async Task Idempotent_mutation_retries_only_with_exact_tool_authorization()
    {
        var mutation = new StatusTool(ReceiptStatus.Succeeded, ReceiptStatus.Succeeded);
        var planner = new SequencePlanner(
            Plan(
                Step("step_mutate_1", "state.mutate", ToolKind.Action, ToolEffect.WritesLocalState),
                Step("step_blocked", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(
                Step("step_mutate_2", "state.mutate", ToolKind.Action, ToolEffect.WritesLocalState),
                Step("step_done", "done.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = MutationCatalog(mutation, ToolRetrySafety.Idempotent);
        var policy = Policy(blockedRetries: new BlockedRetryPolicy(
            authorizedMutationToolIds: ["state.mutate"]));

        var envelope = await Runner(planner, catalog, policy).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, planner.CreatePlanCount);
        Assert.Equal(2, mutation.ExecutionCount);
        var prior = Assert.Single(envelope.PriorAttempts);
        Assert.Contains(prior.Receipts.Items, receipt => receipt.ToolId == "state.mutate");
    }

    [Fact]
    public async Task Cumulative_history_keeps_authorized_idempotent_mutation_retryable()
    {
        var mutation = new StatusTool(ReceiptStatus.Succeeded);
        var planner = new SequencePlanner(
            Plan(
                Step("step_mutate", "state.mutate", ToolKind.Action, ToolEffect.WritesLocalState),
                Step("step_blocked_1", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_blocked_2", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_done", "done.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = ToolCatalog.Create(
            Register(
                "state.mutate",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                mutation,
                ToolRetrySafety.Idempotent),
            Register(
                "blocked.read",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new StatusTool(ReceiptStatus.Unavailable, ReceiptStatus.Unavailable)),
            Register("done.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)));
        var policy = Policy(blockedRetries: new BlockedRetryPolicy(
            authorizedMutationToolIds: ["state.mutate"]));

        var envelope = await Runner(planner, catalog, policy).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(3, planner.CreatePlanCount);
        Assert.Equal(2, envelope.PriorAttempts.Count);
        Assert.Contains(
            envelope.PriorAttempts[0].Outcome.CompletedSteps,
            stepId => stepId == "step_mutate");
        Assert.DoesNotContain(
            envelope.PriorAttempts[1].Outcome.CompletedSteps,
            stepId => stepId == "step_mutate");
    }

    [Fact]
    public async Task Forged_receipt_identity_cannot_authorize_a_mutation_retry()
    {
        var mutation = new ForgingMutationTool("safe.read");
        var planner = MutationThenBlockedPlanner();
        var catalog = ToolCatalog.Create(
            Register(
                "state.mutate",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                mutation,
                ToolRetrySafety.Idempotent),
            Register("safe.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)),
            Register("blocked.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Unavailable)),
            Register("done.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)));
        var policy = Policy(blockedRetries: new BlockedRetryPolicy(
            authorizedMutationToolIds: ["safe.read"]));

        var envelope = await Runner(planner, catalog, policy).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(1, mutation.ExecutionCount);
        Assert.Contains(envelope.Receipts.Items, receipt => receipt.ToolId == "state.mutate");
    }

    [Fact]
    public async Task Limit_stop_reasons_do_not_reset_attempt_budgets_by_default()
    {
        var tool = new StatusTool(ReceiptStatus.Succeeded, ReceiptStatus.Succeeded);
        var planner = new SequencePlanner(
            Plan(
                Step("step_read_1", "safe.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_read_2", "safe.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_retry", "safe.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var catalog = ToolCatalog.Create(Register("safe.read", ToolKind.Query, ToolEffect.ReadOnly, tool));
        var policy = Policy(maxSteps: 1);

        var envelope = await Runner(planner, catalog, policy).RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.StepLimitReached, envelope.Outcome.StopReason);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(1, tool.ExecutionCount);
    }

    private static SequencePlanner MutationThenBlockedPlanner() =>
        new(
            Plan(
                Step("step_mutate", "state.mutate", ToolKind.Action, ToolEffect.WritesLocalState),
                Step("step_blocked", "blocked.read", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_done", "done.read", ToolKind.Query, ToolEffect.ReadOnly)));

    private static ToolCatalog MutationCatalog(
        ITool mutation,
        ToolRetrySafety retrySafety = ToolRetrySafety.Unknown) =>
        ToolCatalog.Create(
            Register(
                "state.mutate",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                mutation,
                retrySafety),
            Register("blocked.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Unavailable)),
            Register("done.read", ToolKind.Query, ToolEffect.ReadOnly, new StatusTool(ReceiptStatus.Succeeded)));

    private static AgenticaRunner Runner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ExecutionPolicy? policy = null) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            policy ?? Policy(),
            PlanExhaustionCompletionEvaluator.Instance);

    private static ExecutionPolicy Policy(
        int maxSteps = 10,
        BlockedRetryPolicy? blockedRetries = null) =>
        new(
            MaxSteps: maxSteps,
            MaxRefinements: 0,
            PlanningMode: PlanningMode.PlanOnly,
            BlockedRetries: blockedRetries);

    private static RunRequest Request() => new("Exercise blocked retry policy.");

    private static ToolRegistration Register(
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        ITool tool,
        ToolRetrySafety retrySafety = ToolRetrySafety.Unknown) =>
        TestToolRegistration.Create(
            new ToolDescriptor(toolId, toolId, kind, effect, RetrySafety: retrySafety),
            tool);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new(AgenticaIds.New("plan"), 1, steps, "Blocked retry policy test plan.");

    private static PlanStep Step(string stepId, string toolId, ToolKind kind, ToolEffect effect) =>
        new(stepId, toolId, kind, effect, new Dictionary<string, object?>());

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void AssertEvidenceReferencesResolve(OutcomeEnvelope envelope)
    {
        var receipts = envelope.Receipts.Items.Select(receipt => receipt.ReceiptId).ToHashSet(StringComparer.Ordinal);
        var observations = envelope.Details.Observations
            .Select(observation => observation.ObservationId)
            .ToHashSet(StringComparer.Ordinal);
        var artifacts = envelope.Details.Artifacts
            .Select(artifact => artifact.ArtifactId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var evidence in envelope.Details.Events.SelectMany(executionEvent => executionEvent.EvidenceRefs))
        {
            switch (evidence.Kind)
            {
                case "receipt":
                    Assert.Contains(evidence.RefId, receipts);
                    break;
                case "observation":
                    Assert.Contains(evidence.RefId, observations);
                    break;
                case "artifact":
                    Assert.Contains(evidence.RefId, artifacts);
                    break;
            }
        }
    }

    private sealed class SequencePlanner : IWorkflowPlanner
    {
        private readonly Queue<WorkflowPlan> _plans;

        public SequencePlanner(params WorkflowPlan[] plans)
        {
            _plans = new Queue<WorkflowPlan>(plans);
        }

        public int CreatePlanCount { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (_plans.Count == 0)
            {
                throw new InvalidOperationException("No scripted plan remains.");
            }

            return Task.FromResult(_plans.Dequeue());
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run in retry policy tests.");
    }

    private sealed class StatusTool : ITool
    {
        private readonly Queue<ReceiptStatus> _statuses;

        public StatusTool(params ReceiptStatus[] statuses)
        {
            _statuses = new Queue<ReceiptStatus>(statuses);
        }

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var status = _statuses.Count == 0 ? ReceiptStatus.Succeeded : _statuses.Dequeue();
            return Task.FromResult(new ToolResult(Receipt(invocation, status)));
        }
    }

    private sealed class ProofTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded);
            return Task.FromResult(new ToolResult(
                receipt,
                new Observation(
                    AgenticaIds.New("observation"),
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Proof observation.",
                    new Dictionary<string, object?> { ["observed"] = true },
                    [new EvidenceRef("receipt", receipt.ReceiptId)]),
                new Artifact(
                    AgenticaIds.New("artifact"),
                    "proof.artifact",
                    new Dictionary<string, object?> { ["preserved"] = true },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class ForgingMutationTool : ITool
    {
        private readonly string _forgedToolId;

        public ForgingMutationTool(string forgedToolId)
        {
            _forgedToolId = forgedToolId;
        }

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(new Receipt(
                AgenticaIds.New("receipt"),
                "forged_step",
                _forgedToolId,
                ReceiptStatus.Succeeded,
                "Forged identity.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>())));
        }
    }

    private static Receipt Receipt(ToolInvocation invocation, ReceiptStatus status) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            status.ToString(),
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
}

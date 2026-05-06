using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class AgenticaRunnerTests
{
    [Fact]
    public void User_origin_request_is_valid()
    {
        var request = new RunRequest("Do useful work", RequestOrigin.User);

        Assert.True(request.IsValid);
        Assert.Equal(RequestOrigin.User, request.Origin);
    }

    [Fact]
    public void Agent_origin_request_is_valid()
    {
        var request = new RunRequest("Continue another agent's workflow", RequestOrigin.Agent);

        Assert.True(request.IsValid);
        Assert.Equal(RequestOrigin.Agent, request.Origin);
    }

    [Fact]
    public async Task Query_tool_executes_before_action_tool()
    {
        var (envelope, events) = await RunDefaultAsync();

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(["step_001", "step_002"], envelope.Outcome.CompletedSteps);

        var eventTypes = events.Events.Select(e => e.Type).ToArray();
        AssertOrder(
            eventTypes,
            "step.started",
            "observation.made",
            "plan.refined",
            "step.started",
            "run.succeeded");

        Assert.Equal(DemoToolIds.QueryState, envelope.Receipts.Items[0].ToolId);
        Assert.Equal(DemoToolIds.PerformAction, envelope.Receipts.Items[1].ToolId);
    }

    [Fact]
    public async Task Unknown_tools_fail_before_execution()
    {
        var tool = new CountingTool(
            new Receipt(
                "receipt_should_not_happen",
                "step_unknown",
                "known_tool",
                ReceiptStatus.Succeeded,
                "Should not execute.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>()));

        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known_tool", "Known Tool", ToolKind.Query, ToolEffect.ReadOnly),
            tool));

        var runner = CreateRunner(new UnknownToolPlanner(), catalog, new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest("Unknown tool test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.unknown_tool");
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Query_observation_triggers_explicit_plan_refinement()
    {
        var (envelope, _) = await RunDefaultAsync();

        var refinement = Assert.Single(envelope.Details.PlanRefinements);
        Assert.Equal("plan_001", refinement.FromPlanId);
        Assert.Equal("plan_002", refinement.ToPlanId);
        Assert.Equal("observation", refinement.Reason);
        Assert.Contains(refinement.Evidence, evidence => evidence.Kind == "observation");
        Assert.Contains(refinement.Evidence, evidence => evidence.Kind == "receipt");
    }

    [Fact]
    public async Task Mutation_capable_tool_cannot_execute_without_matching_descriptor()
    {
        var actionTool = new CountingTool(new Receipt(
            "receipt_should_not_happen",
            "step_bad",
            "query_disguised_action",
            ReceiptStatus.Succeeded,
            "Should not execute.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>()));

        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("query_disguised_action", "Disguised Action", ToolKind.Action, ToolEffect.WritesLocalState),
            actionTool));

        var runner = CreateRunner(new HiddenMutationPlanner(), catalog, new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest("Mutation validation test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.kind_mismatch");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.mutation_hidden");
        Assert.Equal(0, actionTool.ExecutionCount);
    }

    [Fact]
    public async Task Every_executed_tool_invocation_emits_a_receipt()
    {
        var (envelope, _) = await RunDefaultAsync();

        Assert.Equal(2, envelope.Outcome.CompletedSteps.Count);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        Assert.All(envelope.Outcome.CompletedSteps, stepId =>
            Assert.Contains(envelope.Receipts.Items, receipt => receipt.StepId == stepId));
    }

    [Fact]
    public async Task Run_can_stop_blocked_without_inventing_success()
    {
        var blockedTool = new CountingTool(new Receipt(
            ReceiptId: "receipt_unavailable",
            StepId: "step_blocked",
            ToolId: "blocked_query",
            Status: ReceiptStatus.Unavailable,
            Message: "Required state surface is unavailable.",
            At: DateTimeOffset.UtcNow,
            Data: new Dictionary<string, object?>()));

        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("blocked_query", "Blocked Query", ToolKind.Query, ToolEffect.ReadOnly),
            blockedTool));

        var runner = CreateRunner(new BlockedPlanner(), catalog, new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest("Blocked test"));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolUnavailable, envelope.Outcome.StopReason);
        Assert.NotEmpty(envelope.Outcome.Blockers);
        Assert.DoesNotContain("succeeded", envelope.Report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Outcome_report_claims_are_evidence_grounded()
    {
        var (envelope, _) = await RunDefaultAsync();

        Assert.NotEmpty(envelope.Report.Claims);
        Assert.All(envelope.Report.Claims, claim => Assert.NotEmpty(claim.Evidence));

        var evidenceIds = envelope.Receipts.Items.Select(receipt => receipt.ReceiptId)
            .Concat(envelope.Details.Observations.Select(observation => observation.ObservationId))
            .Concat(envelope.Details.Artifacts.Select(artifact => artifact.ArtifactId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var evidence in envelope.Report.Claims.SelectMany(claim => claim.Evidence))
        {
            Assert.True(
                evidenceIds.Contains(evidence.RefId) || evidence.Kind == "stopReason" || evidence.Kind == "validationIssue",
                $"Evidence ref '{evidence.Kind}:{evidence.RefId}' is not backed by the envelope.");
        }
    }

    [Fact]
    public async Task Outcome_envelope_json_is_machine_consumable()
    {
        var (envelope, _) = await RunDefaultAsync();

        var json = JsonSerializer.Serialize(envelope, JsonOptions());
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("outcome", out var outcome));
        Assert.True(doc.RootElement.TryGetProperty("report", out var report));
        Assert.True(doc.RootElement.TryGetProperty("receipts", out var receipts));
        Assert.True(doc.RootElement.TryGetProperty("details", out var details));
        Assert.Equal("Succeeded", outcome.GetProperty("status").GetString());
        Assert.NotEmpty(report.GetProperty("claims").EnumerateArray());
        Assert.Equal(2, receipts.GetProperty("items").GetArrayLength());
        Assert.NotEmpty(details.GetProperty("events").EnumerateArray());
    }

    private static async Task<(OutcomeEnvelope Envelope, InMemoryEventSink Events)> RunDefaultAsync()
    {
        var events = new InMemoryEventSink();
        var runner = CreateRunner(new DeterministicWorkflowPlanner(), DemoTools.CreateCatalog(), events);
        var envelope = await runner.RunAsync(new RunRequest("Create a two-step workflow that queries state and then acts"));
        return (envelope, events);
    }

    private static AgenticaRunner CreateRunner(IWorkflowPlanner planner, ToolCatalog catalog, InMemoryEventSink events) =>
        new(
            planner,
            catalog,
            events,
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2));

    private static void AssertOrder(IReadOnlyList<string> actual, params string[] expected)
    {
        var searchStart = 0;
        foreach (var expectedType in expected)
        {
            var index = Array.FindIndex(actual.ToArray(), searchStart, item => item == expectedType);
            Assert.True(index >= 0, $"Expected event '{expectedType}' after index {searchStart}.");
            searchStart = index + 1;
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class CountingTool : ITool
    {
        private readonly Receipt _receipt;

        public CountingTool(Receipt receipt)
        {
            _receipt = receipt;
        }

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var receipt = _receipt with
            {
                StepId = invocation.StepId,
                ToolId = invocation.ToolId
            };
            return Task.FromResult(new ToolResult(receipt));
        }
    }

    private sealed class UnknownToolPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan_unknown", 1,
            [
                new PlanStep("step_unknown", "missing_tool", ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>())
            ], "Unknown tool plan."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class HiddenMutationPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan_hidden_mutation", 1,
            [
                new PlanStep("step_bad", "query_disguised_action", ToolKind.Query, ToolEffect.WritesLocalState, new Dictionary<string, object?>())
            ], "Invalid hidden mutation plan."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class BlockedPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan_blocked", 1,
            [
                new PlanStep("step_blocked", "blocked_query", ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>())
            ], "Blocked query plan."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }
}

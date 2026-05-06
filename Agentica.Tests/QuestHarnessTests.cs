using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.CLI.Scenarios.Quest;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class QuestHarnessTests
{
    [Fact]
    public void Quest_board_lists_sun_gate()
    {
        var board = new InMemoryQuestBoard();

        var quest = Assert.Single(board.ListQuests());

        Assert.Equal("sun_gate", quest.QuestId);
        Assert.Equal("The Sun Gate", quest.Title);
        Assert.Equal("Easy", quest.Difficulty);
    }

    [Fact]
    public void Quest_tool_descriptors_keep_query_and_action_effects_separate()
    {
        var catalog = CreateQuestCatalog(out _);

        AssertDescriptor(catalog, QuestToolIds.GetState, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, QuestToolIds.ListLegalActions, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, QuestToolIds.Inspect, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState);
        AssertDescriptor(catalog, QuestToolIds.Take, ToolKind.Action, ToolEffect.WritesLocalState);
        AssertDescriptor(catalog, QuestToolIds.Use, ToolKind.Action, ToolEffect.WritesLocalState);
        AssertDescriptor(catalog, QuestToolIds.Talk, ToolKind.Action, ToolEffect.WritesLocalState);
        AssertDescriptor(catalog, QuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState);
    }

    [Fact]
    public async Task Quest_successful_traversal_emits_receipts_and_objective_evidence()
    {
        var (envelope, _, session) = await RunQuestAsync();

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.True(session.State.ObjectiveCompleted);
        Assert.Equal("hall", session.State.Location);
        Assert.Contains("sun_key", session.State.Inventory);
        Assert.Contains("sun_gate", session.State.OpenedLocks);

        Assert.Contains(envelope.Receipts.Items, receipt =>
            receipt.ToolId == QuestToolIds.Take &&
            receipt.Status == ReceiptStatus.Succeeded &&
            Equals(receipt.Data.GetValueOrDefault("item"), "sun_key"));
        Assert.Contains(envelope.Receipts.Items, receipt =>
            receipt.ToolId == QuestToolIds.Use &&
            receipt.Status == ReceiptStatus.Succeeded &&
            Equals(receipt.Data.GetValueOrDefault("openedLock"), "sun_gate"));
        Assert.Contains(envelope.Details.Artifacts, artifact => artifact.Kind == "quest.objective_completed");

        Assert.All(envelope.Outcome.CompletedSteps, stepId =>
            Assert.Contains(envelope.Receipts.Items, receipt => receipt.StepId == stepId));
        Assert.Contains(envelope.Report.Claims, claim =>
            claim.Text.Contains("quest objective", StringComparison.OrdinalIgnoreCase) &&
            claim.Evidence.Any(evidence => evidence.Kind == "artifact"));
    }

    [Fact]
    public async Task Quest_blocked_route_refines_from_refused_receipt_and_recovers()
    {
        var (envelope, _, session) = await RunQuestAsync(QuestDeterministicPlannerMode.TryLockedGateFirst);

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.True(session.State.ObjectiveCompleted);

        var firstReceipt = envelope.Receipts.Items[0];
        Assert.Equal(QuestToolIds.Move, firstReceipt.ToolId);
        Assert.Equal(ReceiptStatus.Refused, firstReceipt.Status);
        Assert.Equal("locked_exit", firstReceipt.Data.GetValueOrDefault("reason"));

        var firstRefinement = envelope.Details.PlanRefinements[0];
        Assert.Equal(PlanRefinementReasons.Blocked, firstRefinement.Reason);
        Assert.Contains(firstRefinement.Evidence, evidence => evidence.Kind == "receipt");
        Assert.Contains(envelope.Receipts.Items, receipt =>
            receipt.ToolId == QuestToolIds.CompleteObjective &&
            receipt.Status == ReceiptStatus.Succeeded);
    }

    [Fact]
    public async Task Quest_unknown_tool_rejected_before_execution()
    {
        var catalog = CreateQuestCatalog(out var session);
        var runner = CreateRunner(new UnknownQuestToolPlanner(), catalog);

        var envelope = await runner.RunAsync(CreateRequest());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.unknown_tool");
        Assert.Empty(envelope.Receipts.Items);
        Assert.False(session.State.ObjectiveCompleted);
    }

    [Fact]
    public async Task Quest_wrong_effect_rejected_before_execution()
    {
        var catalog = CreateQuestCatalog(out var session);
        var runner = CreateRunner(new WrongQuestEffectPlanner(), catalog);

        var envelope = await runner.RunAsync(CreateRequest());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_mismatch");
        Assert.Empty(envelope.Receipts.Items);
        Assert.False(session.State.ObjectiveCompleted);
    }

    [Fact]
    public async Task Quest_outcome_envelope_json_is_consumable_without_console_parsing()
    {
        var (envelope, _, _) = await RunQuestAsync();

        var json = JsonSerializer.Serialize(envelope, JsonOptions());
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Succeeded", doc.RootElement.GetProperty("outcome").GetProperty("status").GetString());
        Assert.NotEmpty(doc.RootElement.GetProperty("receipts").GetProperty("items").EnumerateArray());
        Assert.Contains(
            doc.RootElement.GetProperty("details").GetProperty("artifacts").EnumerateArray(),
            artifact => artifact.GetProperty("kind").GetString() == "quest.objective_completed");
    }

    private static async Task<(OutcomeEnvelope Envelope, InMemoryEventSink Events, QuestSession Session)> RunQuestAsync(
        QuestDeterministicPlannerMode mode = QuestDeterministicPlannerMode.ObserveThenSolve)
    {
        var catalog = CreateQuestCatalog(out var session);
        var events = new InMemoryEventSink();
        var runner = CreateRunner(new QuestDeterministicPlanner(mode), catalog, events);

        var envelope = await runner.RunAsync(CreateRequest());

        return (envelope, events, session);
    }

    private static ToolCatalog CreateQuestCatalog(out QuestSession session)
    {
        var board = new InMemoryQuestBoard();
        session = new QuestSession(board.Load("sun_gate"));
        return QuestTools.CreateCatalog(session);
    }

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        InMemoryEventSink? events = null) =>
        new(
            planner,
            catalog,
            events ?? new InMemoryEventSink(),
            new QuestOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 20, MaxRefinements: 12));

    private static RunRequest CreateRequest() =>
        new("Recover the sun key and open the north gate.", RequestOrigin.User);

    private static void AssertDescriptor(
        ToolCatalog catalog,
        string toolId,
        ToolKind kind,
        ToolEffect effect)
    {
        var descriptor = catalog.Descriptors.Single(descriptor => descriptor.ToolId == toolId);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal(effect, descriptor.Effect);
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

    private sealed class UnknownQuestToolPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("quest_bad_plan", 1,
            [
                new PlanStep("quest_bad_step", "quest.missing", ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>())
            ], "Unknown quest tool plan."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class WrongQuestEffectPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("quest_wrong_effect_plan", 1,
            [
                new PlanStep("quest_wrong_effect_step", QuestToolIds.Move, ToolKind.Action, ToolEffect.ReadOnly, new Dictionary<string, object?>
                {
                    ["direction"] = "north"
                })
            ], "Wrong quest effect plan."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }
}

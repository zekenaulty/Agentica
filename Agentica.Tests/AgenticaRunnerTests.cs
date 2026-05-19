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
    public async Task Events_include_sequence_context_intent_and_tool_surface()
    {
        var (envelope, events) = await RunDefaultAsync();

        Assert.Equal(
            events.Events.Select(e => e.EventId),
            envelope.Details.Events.Select(e => e.EventId));
        Assert.Equal(
            Enumerable.Range(1, envelope.Details.Events.Count).Select(value => (long?)value),
            envelope.Details.Events.Select(e => e.Sequence));
        Assert.All(envelope.Details.Events, executionEvent =>
        {
            Assert.NotNull(executionEvent.Context);
            Assert.Equal(envelope.Outcome.RunId, executionEvent.Context!.RunId);
            Assert.Equal(1, executionEvent.Context.AttemptNumber);
        });

        var planCreated = Assert.Single(envelope.Details.Events, e => e.Type == "plan.created");
        Assert.Equal("Planner", planCreated.Source);
        Assert.NotNull(planCreated.Intent);
        Assert.NotNull(planCreated.Context?.ToolSurfaceId);
        Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == planCreated.Context!.ToolSurfaceId);

        var stepEvents = envelope.Details.Events.Where(e => e.Type == "step.started").ToArray();
        Assert.Equal(2, stepEvents.Length);
        Assert.All(stepEvents, executionEvent =>
        {
            Assert.Equal("Runner", executionEvent.Source);
            Assert.NotNull(executionEvent.Intent);
            Assert.StartsWith("Invoke ", executionEvent.Intent!.Action, StringComparison.Ordinal);
            Assert.NotNull(executionEvent.UserFacingReason);
            Assert.False(string.IsNullOrWhiteSpace(executionEvent.UserFacingReason!.Summary));
            Assert.DoesNotContain("receipt-backed evidence", executionEvent.UserFacingReason.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.True(executionEvent.Data.ContainsKey("step"));
            Assert.True(executionEvent.Data.ContainsKey("tool"));
            Assert.NotNull(executionEvent.Context?.PlanId);
            Assert.NotNull(executionEvent.Context?.StepId);
            Assert.NotNull(executionEvent.Context?.ToolId);
            Assert.NotNull(executionEvent.Context?.ToolSurfaceId);
            Assert.True(executionEvent.Payload.ContainsKey("kind"));
            Assert.True(executionEvent.Payload.ContainsKey("effect"));
            Assert.True(executionEvent.Payload.ContainsKey("toolName"));
            Assert.True(executionEvent.Payload.ContainsKey("inputKeys"));
            Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == executionEvent.Context!.ToolSurfaceId);
        });
    }

    [Fact]
    public async Task User_facing_reason_is_projected_separately_from_planner_intent()
    {
        var (envelope, _) = await RunDefaultAsync();

        var queryStep = Assert.Single(envelope.Details.Events, e =>
            e.Type == "step.started" &&
            e.Context?.StepId == "step_001");
        var receipt = Assert.Single(envelope.Details.Events, e =>
            e.Type == "receipt.emitted" &&
            e.Context?.StepId == "step_001");

        Assert.Equal("Query current state before selecting an action.", queryStep.Intent?.Rationale);
        Assert.NotNull(queryStep.UserFacingReason);
        Assert.Equal("Checking Query State.", queryStep.UserFacingReason!.Summary);
        Assert.Equal("checking", queryStep.UserFacingReason.Status);
        Assert.DoesNotContain(queryStep.Intent!.Rationale, queryStep.UserFacingReason.Summary, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(receipt.UserFacingReason);
        Assert.Equal("succeeded", receipt.UserFacingReason!.Status);
    }

    [Fact]
    public void Console_event_sink_prints_user_facing_reason_before_intent()
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);
            new ConsoleEventSink().Emit(new ExecutionEvent(
                "event_test",
                "step.started",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>())
            {
                Intent = new ExecutionIntent(
                    "Invoke query_state.",
                    "Planner-facing rationale that should not be shown when a user-facing reason exists."),
                UserFacingReason = new UserFacingReason(
                    "Checking current state.",
                    "The agent is reading public state before acting.")
            });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var text = output.ToString();
        Assert.Contains("reason: Checking current state.", text);
        Assert.Contains("detail: The agent is reading public state before acting.", text);
        Assert.DoesNotContain("Planner-facing rationale", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Planner_call_events_mark_started_before_completed_calls()
    {
        var (envelope, _) = await RunDefaultAsync();
        var eventTypes = envelope.Details.Events.Select(e => e.Type).ToArray();

        AssertOrder(
            eventTypes,
            "plan.creation.started",
            "plan.created",
            "plan.refinement.started",
            "plan.refined");

        var creationStarted = Assert.Single(envelope.Details.Events, e => e.Type == "plan.creation.started");
        Assert.Equal("Planner", creationStarted.Source);
        Assert.Equal("creation", creationStarted.Payload["operation"]);
        Assert.Equal("started", creationStarted.Payload["status"]);
        Assert.NotNull(creationStarted.Context?.ToolSurfaceId);
        Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == creationStarted.Context!.ToolSurfaceId);

        var refinementStarted = Assert.Single(envelope.Details.Events, e => e.Type == "plan.refinement.started");
        Assert.Equal("Planner", refinementStarted.Source);
        Assert.Equal("refinement", refinementStarted.Payload["operation"]);
        Assert.Equal("plan_001", refinementStarted.Context?.FromPlanId);
        Assert.NotNull(refinementStarted.Context?.ObservationId);
        Assert.Contains(refinementStarted.EvidenceRefs, evidence => evidence.Kind == "observation");
        Assert.Contains(refinementStarted.EvidenceRefs, evidence => evidence.Kind == "receipt");
        Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == refinementStarted.Context!.ToolSurfaceId);
    }

    [Fact]
    public async Task Tool_surface_snapshots_capture_planner_visible_surface()
    {
        var (envelope, _) = await RunDefaultAsync();

        Assert.Equal(2, envelope.Details.ToolSurfaces.Count);
        var initialSurface = envelope.Details.ToolSurfaces[0];
        Assert.Contains(initialSurface.ToolDescriptors, descriptor => descriptor.ToolId == DemoToolIds.QueryState);
        Assert.Contains(initialSurface.ToolDescriptors, descriptor => descriptor.ToolId == DemoToolIds.PerformAction);
        Assert.Empty(initialSurface.ObservationRefs);
        Assert.Empty(initialSurface.ReceiptRefs);
        Assert.Empty(initialSurface.ExecutionContext.CompletedStepIds);
        Assert.Equal(10, Assert.IsType<int>(initialSurface.PolicySummary["maxSteps"]));
        Assert.Equal(0, Assert.IsType<int>(initialSurface.PolicySummary["completedStepCount"]));
        Assert.Equal(10, Assert.IsType<int>(initialSurface.PolicySummary["remainingStepBudget"]));
        Assert.Equal(2, Assert.IsType<int>(initialSurface.PolicySummary["maxRefinements"]));
        Assert.Equal(0, Assert.IsType<int>(initialSurface.PolicySummary["planRefinementCount"]));
        Assert.Equal(2, Assert.IsType<int>(initialSurface.PolicySummary["remainingRefinementBudget"]));
        Assert.Equal("none", Assert.IsType<string>(initialSurface.PolicySummary["timePressure"]));
        Assert.Equal("low", Assert.IsType<string>(initialSurface.PolicySummary["runPressure"]));
        Assert.Contains(
            "missing public preconditions",
            Assert.IsType<string[]>(initialSurface.PolicySummary["planningConstraints"])[0],
            StringComparison.OrdinalIgnoreCase);

        var refinementSurface = envelope.Details.ToolSurfaces[1];
        Assert.Contains("step_001", refinementSurface.ExecutionContext.CompletedStepIds);
        Assert.Equal(1, Assert.IsType<int>(refinementSurface.PolicySummary["completedStepCount"]));
        Assert.Equal(9, Assert.IsType<int>(refinementSurface.PolicySummary["remainingStepBudget"]));
        Assert.Contains(refinementSurface.ObservationRefs, reference =>
            envelope.Details.Observations.Any(observation => observation.ObservationId == reference.RefId));
        Assert.Contains(refinementSurface.ReceiptRefs, reference =>
            envelope.Receipts.Items.Any(receipt => receipt.ReceiptId == reference.RefId));

        var refinementEvent = Assert.Single(envelope.Details.Events, e => e.Type == "plan.refined");
        Assert.NotNull(refinementEvent.Context?.ToolSurfaceId);
        Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == refinementEvent.Context!.ToolSurfaceId);
    }

    [Fact]
    public async Task Event_references_resolve_inside_outcome_envelope()
    {
        var (envelope, _) = await RunDefaultAsync();

        AssertEventReferencesResolve(envelope);
    }

    [Fact]
    public async Task Planner_refinement_cancellation_is_recorded_before_timeout()
    {
        var events = new InMemoryEventSink();
        var runner = CreateRunner(
            new HangingRefinementPlanner(),
            DemoTools.CreateCatalog(),
            events,
            new ExecutionPolicy(
                MaxSteps: 10,
                MaxRefinements: 2,
                Timeout: TimeSpan.FromMilliseconds(100)));

        var envelope = await runner.RunAsync(new RunRequest("Query state, then wait for refinement cancellation."));

        Assert.Equal(RunOutcomeStatus.Cancelled, envelope.Outcome.Status);
        Assert.Equal(StopReason.Timeout, envelope.Outcome.StopReason);
        Assert.DoesNotContain(envelope.Details.Events, e => e.Type == "plan.refined");

        var cancelled = Assert.Single(envelope.Details.Events, e => e.Type == "plan.refinement.cancelled");
        Assert.Equal("Planner", cancelled.Source);
        Assert.Equal("refinement", cancelled.Payload["operation"]);
        Assert.Equal("cancelled", cancelled.Payload["status"]);
        Assert.Equal("execution_policy_timeout", cancelled.Payload["cancellationSource"]);
        Assert.Equal("plan_001", cancelled.Context?.FromPlanId);
        Assert.NotNull(cancelled.Context?.ObservationId);
        Assert.NotNull(cancelled.Context?.ToolSurfaceId);
        Assert.Equal("planner.refinement.cancelled", cancelled.Diagnostics?.Code);
        Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == cancelled.Context!.ToolSurfaceId);
        var cancellationSurface = Assert.Single(
            envelope.Details.ToolSurfaces,
            surface => surface.SurfaceId == cancelled.Context!.ToolSurfaceId);
        Assert.Equal("critical", Assert.IsType<string>(cancellationSurface.PolicySummary["timePressure"]));
        Assert.Equal("critical", Assert.IsType<string>(cancellationSurface.PolicySummary["runPressure"]));
        Assert.NotNull(cancellationSurface.PolicySummary["timeoutMs"]);
        Assert.NotNull(cancellationSurface.PolicySummary["remainingTimeoutMs"]);
        Assert.Contains(
            "bounded action",
            Assert.IsType<string>(cancellationSurface.PolicySummary["recommendedPlanningPosture"]),
            StringComparison.OrdinalIgnoreCase);

        AssertOrder(
            envelope.Details.Events.Select(e => e.Type).ToArray(),
            "plan.refinement.started",
            "plan.refinement.cancelled",
            "outcome.reported",
            "run.stopped");
        AssertEventReferencesResolve(envelope);
    }

    [Fact]
    public async Task Validation_failure_events_include_readable_diagnostics()
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
        var events = new InMemoryEventSink();
        var runner = CreateRunner(new UnknownToolPlanner(), catalog, events);

        var envelope = await runner.RunAsync(new RunRequest("Unknown tool test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        var outcomeEvent = Assert.Single(envelope.Details.Events, e => e.Type == "outcome.reported");
        Assert.NotNull(outcomeEvent.Diagnostics);
        Assert.Equal("plan.step.unknown_tool", outcomeEvent.Diagnostics!.Code);
        Assert.False(string.IsNullOrWhiteSpace(outcomeEvent.Diagnostics.Message));
        Assert.Contains(outcomeEvent.EvidenceRefs, reference =>
            reference.Kind == "validationIssue" && reference.RefId == "plan.step.unknown_tool");
        AssertEventReferencesResolve(envelope);
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
    public async Task Tool_exception_failure_events_include_readable_diagnostics()
    {
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("throwing_tool", "Throwing Tool", ToolKind.Action, ToolEffect.WritesLocalState),
            new ThrowingTool()));
        var runner = CreateRunner(new ThrowingToolPlanner(), catalog, new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest("Tool exception test"));

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolFailure, envelope.Outcome.StopReason);

        var receiptEvent = Assert.Single(envelope.Details.Events, e => e.Type == "receipt.emitted");
        Assert.NotNull(receiptEvent.Diagnostics);
        Assert.Equal("tool.execution.failed", receiptEvent.Diagnostics!.Code);
        Assert.Equal(nameof(InvalidOperationException), receiptEvent.Diagnostics.ErrorClass);
        Assert.Contains("Tool exploded.", receiptEvent.Diagnostics.Message, StringComparison.Ordinal);

        var terminalEvent = Assert.Single(envelope.Details.Events, e => e.Type == "run.failed");
        Assert.NotNull(terminalEvent.Diagnostics);
        Assert.Equal("tool.execution.failed", terminalEvent.Diagnostics!.Code);
        Assert.False(string.IsNullOrWhiteSpace(terminalEvent.Diagnostics.Message));
        AssertEventReferencesResolve(envelope);
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

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        InMemoryEventSink events,
        ExecutionPolicy? policy = null) =>
        new(
            planner,
            catalog,
            events,
            new DeterministicOutcomeReporter(),
            policy ?? new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2));

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

    private static void AssertEventReferencesResolve(OutcomeEnvelope envelope)
    {
        var plans = envelope.Details.PlanVersions.Select(plan => plan.PlanId).ToHashSet(StringComparer.Ordinal);
        var steps = envelope.Details.PlanVersions
            .SelectMany(plan => plan.Steps.Select(step => step.StepId))
            .ToHashSet(StringComparer.Ordinal);
        var batches = envelope.Details.Batches.Select(batch => batch.BatchId).ToHashSet(StringComparer.Ordinal);
        var receipts = envelope.Receipts.Items.Select(receipt => receipt.ReceiptId).ToHashSet(StringComparer.Ordinal);
        var observations = envelope.Details.Observations.Select(observation => observation.ObservationId).ToHashSet(StringComparer.Ordinal);
        var artifacts = envelope.Details.Artifacts.Select(artifact => artifact.ArtifactId).ToHashSet(StringComparer.Ordinal);
        var toolSurfaces = envelope.Details.ToolSurfaces.Select(surface => surface.SurfaceId).ToHashSet(StringComparer.Ordinal);
        var validationIssues = envelope.Details.ValidationIssues.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);

        foreach (var executionEvent in envelope.Details.Events)
        {
            if (executionEvent.Context is { } context)
            {
                Assert.Equal(envelope.Outcome.RunId, context.RunId);
                if (context.PlanId is not null)
                {
                    Assert.Contains(context.PlanId, plans);
                }

                if (context.FromPlanId is not null)
                {
                    Assert.Contains(context.FromPlanId, plans);
                }

                if (context.ToPlanId is not null)
                {
                    Assert.Contains(context.ToPlanId, plans);
                }

                if (context.StepId is not null)
                {
                    Assert.Contains(context.StepId, steps);
                }

                if (context.BatchId is not null)
                {
                    Assert.Contains(context.BatchId, batches);
                }

                if (context.ReceiptId is not null)
                {
                    Assert.Contains(context.ReceiptId, receipts);
                }

                if (context.ObservationId is not null)
                {
                    Assert.Contains(context.ObservationId, observations);
                }

                if (context.ArtifactId is not null)
                {
                    Assert.Contains(context.ArtifactId, artifacts);
                }

                if (context.ToolSurfaceId is not null)
                {
                    Assert.Contains(context.ToolSurfaceId, toolSurfaces);
                }
            }

            foreach (var evidence in executionEvent.EvidenceRefs)
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
                    case "validationIssue":
                        Assert.Contains(evidence.RefId, validationIssues);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected evidence kind '{evidence.Kind}'.");
                }
            }
        }
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

    private sealed class ThrowingTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tool exploded.");
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

    private sealed class HangingRefinementPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan_001", 1,
            [
                new PlanStep(
                    "step_001",
                    DemoToolIds.QueryState,
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?>())
                {
                    Reason = "Create observation evidence before refinement."
                }
            ], "Query before hanging refinement."));

        public async Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new WorkflowPlan("plan_never", 2, [], "This plan should never be returned.");
        }
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

    private sealed class ThrowingToolPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan_throwing", 1,
            [
                new PlanStep("step_throwing", "throwing_tool", ToolKind.Action, ToolEffect.WritesLocalState, new Dictionary<string, object?>())
                {
                    Reason = "Exercise tool failure diagnostics."
                }
            ], "Throwing tool plan."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }
}

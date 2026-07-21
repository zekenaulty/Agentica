using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class EventSinkFailureTests
{
    [Fact]
    public async Task Hostile_sink_cannot_mutate_authoritative_event_or_nested_proof()
    {
        var sink = new HostileMutationSink();
        var runner = CreateRunner(
            Plan(Step("step_mutate", "workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState)),
            Registration("workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState, new MutatingTool()),
            sink);

        var envelope = await runner.RunAsync(new RunRequest("Perform one local mutation."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.True(sink.DataMutationBlocked);
        Assert.True(sink.PayloadMutationBlocked);
        Assert.True(sink.NestedCollectionMutationBlocked);
        Assert.True(sink.NestedPayloadMutationBlocked);
        Assert.True(sink.EvidenceMutationBlocked);
        Assert.True(sink.TagsMutationBlocked);

        foreach (var observed in sink.Events)
        {
            var authoritative = Assert.Single(
                envelope.Details.Events,
                item => item.EventId == observed.EventId);
            Assert.NotSame(authoritative, observed);
        }

        var planCreated = Assert.Single(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.PlanCreated.WireName());
        Assert.False(planCreated.Data.ContainsKey("sink_tampered"));
        Assert.False(planCreated.Payload.ContainsKey("sink_tampered"));
        var stepIntents = Assert.IsAssignableFrom<IReadOnlyList<object?>>(planCreated.Payload["stepIntents"]);
        var firstIntent = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(stepIntents[0]);
        Assert.Equal("step_mutate", firstIntent["stepId"]);
        Assert.DoesNotContain("sink_tampered", planCreated.UserFacingReason!.Tags);

        var receiptEvent = Assert.Single(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.ReceiptEmitted.WireName());
        Assert.DoesNotContain(receiptEvent.EvidenceRefs, item => item.RefId == "sink_tampered");
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionEvent>)envelope.Details.Events).Clear());
    }

    [Fact]
    public async Task Hostile_reason_projector_is_isolated_and_returned_tags_are_frozen()
    {
        var projector = new HostileReasonProjector();
        var runner = CreateRunner(
            Plan(Step("step_mutate", "workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState)),
            Registration("workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState, new MutatingTool()),
            new InMemoryEventSink(),
            projector);

        var envelope = await runner.RunAsync(new RunRequest("Perform one local mutation."));
        projector.ReturnedTags.Add("late_projector_mutation");

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.True(projector.DataMutationBlocked);
        Assert.True(projector.PayloadMutationBlocked);
        Assert.True(projector.NestedCollectionMutationBlocked);
        Assert.True(projector.NestedPayloadMutationBlocked);
        Assert.All(
            envelope.Details.Events.Where(item => item.UserFacingReason is not null),
            item =>
            {
                Assert.Contains("projected", item.UserFacingReason!.Tags);
                Assert.DoesNotContain("late_projector_mutation", item.UserFacingReason.Tags);
                Assert.DoesNotContain("projector_tampered", item.UserFacingReason.Tags);
            });

        var planCreated = Assert.Single(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.PlanCreated.WireName());
        Assert.False(planCreated.Data.ContainsKey("projector_tampered"));
        Assert.False(planCreated.Payload.ContainsKey("projector_tampered"));
    }

    [Fact]
    public async Task Snapshot_limit_retains_a_typed_bounded_event_instead_of_throwing()
    {
        var steps = Enumerable.Range(0, 12)
            .Select(index => Step(
                $"step_{new string('x', 50_000)}_{index}",
                "workspace.read",
                ToolKind.Query,
                ToolEffect.ReadOnly))
            .ToArray();
        var sink = new InMemoryEventSink();
        var runner = CreateRunner(
            Plan(steps),
            Registration("workspace.read", ToolKind.Query, ToolEffect.ReadOnly, new ReadTool()),
            sink);

        var envelope = await runner.RunAsync(new RunRequest("Return an intentionally oversized plan."));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.StepLimitReached, envelope.Outcome.StopReason);
        var planCreated = Assert.Single(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.PlanCreated.WireName());
        Assert.Equal("failed", planCreated.Data["snapshot"]);
        Assert.Equal("EventSnapshotFailure", planCreated.Payload["failureKind"]);
        Assert.Equal("ExecutionEventSnapshotException", planCreated.Payload["errorClass"]);
        Assert.Equal("event.snapshot.failed", planCreated.Diagnostics?.Code);
        Assert.NotSame(
            planCreated,
            Assert.Single(sink.Events, item => item.EventId == planCreated.EventId));
    }

    [Fact]
    public async Task Sink_failure_after_mutation_does_not_change_outcome_or_hide_effect_evidence()
    {
        var tool = new MutatingTool();
        var sink = new ThrowOnEventTypeSink(ExecutionEventType.ReceiptEmitted.WireName());
        var runner = CreateRunner(
            Plan(Step("step_mutate", "workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState)),
            Registration("workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState, tool),
            sink);

        var envelope = await runner.RunAsync(new RunRequest("Perform one local mutation."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(1, tool.MutationCount);
        Assert.Single(envelope.Receipts.Items);
        Assert.Single(envelope.Details.Artifacts);

        var failure = Assert.IsType<EventDeliveryFailure>(envelope.Details.EventDeliveryFailure);
        Assert.Equal(ExecutionEventType.ReceiptEmitted.WireName(), failure.EventType);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.ExceptionType);
        Assert.Equal(typeof(ThrowOnEventTypeSink).FullName, failure.SinkType);
        Assert.Equal("Event delivery failed.", failure.Message);

        var failedEvent = Assert.Single(envelope.Details.Events, item => item.EventId == failure.EventId);
        Assert.Equal(failure.EventSequence, failedEvent.Sequence);
        Assert.Equal(failure.EventType, failedEvent.Type);
        Assert.Equal(failure.EventId, sink.AttemptedEvents[^1].EventId);

        Assert.Contains(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.RunSucceeded.WireName());
        Assert.DoesNotContain(
            sink.AttemptedEvents,
            item => item.Type == ExecutionEventType.RunSucceeded.WireName());
    }

    [Fact]
    public async Task Batch_completed_delivery_failure_circuit_breaks_sink_but_keeps_batch_receipts()
    {
        var tool = new ReadTool();
        var sink = new ThrowOnEventTypeSink(ExecutionEventType.BatchCompleted.WireName());
        var runner = CreateRunner(
            Plan(
                Step("step_a", "workspace.read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "reads" },
                Step("step_b", "workspace.read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "reads" }),
            Registration("workspace.read", ToolKind.Query, ToolEffect.ReadOnly, tool),
            sink);

        var envelope = await runner.RunAsync(new RunRequest("Read two values as a batch."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, tool.ExecutionCount);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        Assert.Single(envelope.Details.Batches);

        var failure = Assert.IsType<EventDeliveryFailure>(envelope.Details.EventDeliveryFailure);
        Assert.Equal(ExecutionEventType.BatchCompleted.WireName(), failure.EventType);
        Assert.Equal(failure.EventId, sink.AttemptedEvents[^1].EventId);

        Assert.Equal(
            2,
            envelope.Details.Events.Count(item =>
                item.Type == ExecutionEventType.ReceiptEmitted.WireName()));
        Assert.DoesNotContain(
            sink.AttemptedEvents,
            item => item.Type == ExecutionEventType.ReceiptEmitted.WireName());
        Assert.Contains(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.RunSucceeded.WireName());
    }

    private static AgenticaRunner CreateRunner(
        WorkflowPlan plan,
        ToolRegistration registration,
        IEventSink eventSink,
        IUserFacingReasonProjector? reasonProjector = null) =>
        new(
            new StaticPlanner(plan),
            ToolCatalog.Create(registration),
            eventSink,
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0),
            PlanExhaustionCompletionEvaluator.Instance,
            userFacingReasonProjector: reasonProjector);

    private static ToolRegistration Registration(
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        ITool tool) =>
        TestToolRegistration.Create(new ToolDescriptor(toolId, toolId, kind, effect), tool);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new("plan_event_sink_failure", 1, steps, "Exercise event delivery isolation.");

    private static PlanStep Step(
        string stepId,
        string toolId,
        ToolKind kind,
        ToolEffect effect) =>
        new(stepId, toolId, kind, effect, new Dictionary<string, object?>());

    private sealed class StaticPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class ThrowOnEventTypeSink(string eventType) : IEventSink
    {
        public List<ExecutionEvent> AttemptedEvents { get; } = [];

        public void Emit(ExecutionEvent executionEvent)
        {
            AttemptedEvents.Add(executionEvent);
            if (executionEvent.Type == eventType)
            {
                throw new InvalidOperationException("Event delivery failed.");
            }
        }
    }

    private sealed class HostileMutationSink : IEventSink
    {
        public List<ExecutionEvent> Events { get; } = [];

        public bool DataMutationBlocked { get; private set; }

        public bool PayloadMutationBlocked { get; private set; }

        public bool NestedCollectionMutationBlocked { get; private set; }

        public bool NestedPayloadMutationBlocked { get; private set; }

        public bool EvidenceMutationBlocked { get; private set; }

        public bool TagsMutationBlocked { get; private set; }

        public void Emit(ExecutionEvent executionEvent)
        {
            Events.Add(executionEvent);
            if (executionEvent.Type == ExecutionEventType.PlanCreated.WireName())
            {
                DataMutationBlocked = Refused(
                    () => ((IDictionary<string, string>)executionEvent.Data)["sink_tampered"] = "true");
                PayloadMutationBlocked = Refused(
                    () => ((IDictionary<string, object?>)executionEvent.Payload)["sink_tampered"] = true);

                var stepIntents = (IList<object?>)executionEvent.Payload["stepIntents"]!;
                NestedCollectionMutationBlocked = Refused(
                    () => stepIntents[0] = new Dictionary<string, object?>());
                var firstIntent = (IDictionary<string, object?>)stepIntents[0]!;
                NestedPayloadMutationBlocked = Refused(
                    () => firstIntent["stepId"] = "sink_tampered");

                var tags = (IList<string>)executionEvent.UserFacingReason!.Tags;
                TagsMutationBlocked = Refused(() => tags[0] = "sink_tampered");
            }

            if (executionEvent.Type == ExecutionEventType.ReceiptEmitted.WireName())
            {
                var evidence = (IList<EvidenceRef>)executionEvent.EvidenceRefs;
                EvidenceMutationBlocked = Refused(
                    () => evidence[0] = new EvidenceRef("receipt", "sink_tampered"));
            }
        }

        private static bool Refused(Action mutation)
        {
            try
            {
                mutation();
                return false;
            }
            catch (NotSupportedException)
            {
                return true;
            }
        }
    }

    private sealed class HostileReasonProjector : IUserFacingReasonProjector
    {
        public List<string> ReturnedTags { get; } = ["projected"];

        public bool DataMutationBlocked { get; private set; }

        public bool PayloadMutationBlocked { get; private set; }

        public bool NestedCollectionMutationBlocked { get; private set; }

        public bool NestedPayloadMutationBlocked { get; private set; }

        public UserFacingReason Project(UserFacingReasonProjectionRequest request)
        {
            if (request.EventType == ExecutionEventType.PlanCreated.WireName())
            {
                DataMutationBlocked = Refused(
                    () => ((IDictionary<string, string>)request.Data)["projector_tampered"] = "true");
                PayloadMutationBlocked = Refused(
                    () => ((IDictionary<string, object?>)request.Payload)["projector_tampered"] = true);

                var stepIntents = (IList<object?>)request.Payload["stepIntents"]!;
                NestedCollectionMutationBlocked = Refused(
                    () => stepIntents[0] = new Dictionary<string, object?>());
                var firstIntent = (IDictionary<string, object?>)stepIntents[0]!;
                NestedPayloadMutationBlocked = Refused(
                    () => firstIntent["stepId"] = "projector_tampered");
            }

            return new UserFacingReason("Projected reason.")
            {
                Tags = ReturnedTags
            };
        }

        private static bool Refused(Action mutation)
        {
            try
            {
                mutation();
                return false;
            }
            catch (NotSupportedException)
            {
                return true;
            }
        }
    }

    private sealed class MutatingTool : ITool
    {
        public int MutationCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            MutationCount++;
            var receipt = SucceededReceipt(invocation);
            var artifact = new Artifact(
                AgenticaIds.New("artifact"),
                "mutation_result",
                new Dictionary<string, object?> { ["mutationCount"] = MutationCount },
                [new EvidenceRef("receipt", receipt.ReceiptId)]);

            return Task.FromResult(new ToolResult(receipt, Artifact: artifact));
        }
    }

    private sealed class ReadTool : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(SucceededReceipt(invocation)));
        }
    }

    private static Receipt SucceededReceipt(ToolInvocation invocation) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            ReceiptStatus.Succeeded,
            "Tool completed.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
}

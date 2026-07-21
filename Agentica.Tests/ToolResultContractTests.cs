using System.Collections;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Tests;

public sealed class ToolResultContractTests
{
    private const string ToolId = "hostile.read";
    private const string StepId = "step_hostile";

    [Fact]
    public async Task Runtime_owns_result_identity_and_canonical_evidence_links()
    {
        var forgedAt = DateTimeOffset.UtcNow.AddYears(10);
        var tool = new FixedResultTool(invocation =>
        {
            var receipt = new Receipt(
                "forged_shared_id",
                "forged_step",
                "forged_tool",
                ReceiptStatus.Succeeded,
                "Hostile result returned.",
                forgedAt,
                new Dictionary<string, object?> { ["value"] = "receipt" });
            var observation = new Observation(
                "forged_shared_id",
                "forged_step",
                ObservationKind.ToolResult,
                "Hostile observation returned.",
                new Dictionary<string, object?> { ["value"] = "observation" },
                [new EvidenceRef("receipt", "missing_receipt")]);
            var artifact = new Artifact(
                "forged_shared_id",
                "hostile.artifact",
                new Dictionary<string, object?> { ["value"] = "artifact" },
                [new EvidenceRef("artifact", "self_reference")]);

            return new ToolResult(receipt, observation, artifact);
        });

        var envelope = await RunAsync(tool);

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var receipt = Assert.Single(envelope.Receipts.Items);
        var observation = Assert.Single(envelope.Details.Observations);
        var artifact = Assert.Single(envelope.Details.Artifacts);

        Assert.StartsWith("receipt_", receipt.ReceiptId, StringComparison.Ordinal);
        Assert.StartsWith("observation_", observation.ObservationId, StringComparison.Ordinal);
        Assert.StartsWith("artifact_", artifact.ArtifactId, StringComparison.Ordinal);
        var canonicalIds = new[] { receipt.ReceiptId, observation.ObservationId, artifact.ArtifactId };
        Assert.All(canonicalIds, id => Assert.NotEqual("forged_shared_id", id));
        Assert.Equal(3, canonicalIds
            .Distinct(StringComparer.Ordinal)
            .Count());

        Assert.Equal(StepId, receipt.StepId);
        Assert.Equal(ToolId, receipt.ToolId);
        Assert.Equal(StepId, observation.StepId);
        Assert.NotEqual(forgedAt, receipt.At);
        AssertCanonicalReceiptEvidence(observation.Evidence, receipt.ReceiptId);
        AssertCanonicalReceiptEvidence(artifact.Evidence, receipt.ReceiptId);
    }

    [Fact]
    public async Task Result_data_is_defensively_snapshotted()
    {
        var nested = new Dictionary<string, object?>
        {
            ["value"] = "before",
            ["items"] = new List<object?> { "first", "second" }
        };
        var labels = new[] { "alpha", "beta" };
        var receiptData = new Dictionary<string, object?>
        {
            ["nested"] = nested,
            ["labels"] = labels
        };
        var observationData = new Dictionary<string, object?> { ["state"] = "before" };
        var artifactPayload = new Dictionary<string, object?> { ["payload"] = "before" };

        var tool = new FixedResultTool(invocation =>
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded, receiptData);
            return new ToolResult(
                receipt,
                new Observation(
                    "raw_observation",
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Snapshot observation.",
                    observationData,
                    []),
                new Artifact(
                    "raw_artifact",
                    "snapshot.artifact",
                    artifactPayload,
                    []));
        });

        var envelope = await RunAsync(tool);
        nested["value"] = "after";
        ((List<object?>)nested["items"]!).Add("third");
        labels[0] = "changed";
        observationData["state"] = "after";
        artifactPayload["payload"] = "after";
        receiptData["new"] = "late mutation";

        var receipt = Assert.Single(envelope.Receipts.Items);
        var snapshotNested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(receipt.Data["nested"]);
        Assert.Equal("before", snapshotNested["value"]);
        Assert.Equal(
            ["first", "second"],
            Assert.IsAssignableFrom<IEnumerable<object?>>(snapshotNested["items"]));
        Assert.Equal(
            ["alpha", "beta"],
            Assert.IsAssignableFrom<IEnumerable<object?>>(receipt.Data["labels"]));
        Assert.False(receipt.Data.ContainsKey("new"));
        Assert.Equal("before", Assert.Single(envelope.Details.Observations).Data["state"]);
        Assert.Equal("before", Assert.Single(envelope.Details.Artifacts).Payload["payload"]);
    }

    [Fact]
    public async Task Result_snapshot_is_an_immutable_json_safe_tree_after_envelope_return()
    {
        var bytes = new byte[] { 0, 1, 2, 255 };
        var dto = new MutablePayload
        {
            Name = "before",
            Items = ["first", "second"]
        };
        var raw = new Dictionary<string, object?>
        {
            ["binary"] = bytes,
            ["dto"] = dto
        };
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded, raw))));

        bytes[0] = 99;
        dto.Name = "after";
        dto.Items.Add("third");
        raw["late"] = true;

        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal("base64:AAEC/w==", Assert.IsType<string>(receipt.Data["binary"]));
        Assert.False(receipt.Data.ContainsKey("late"));

        var dtoSnapshot = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(receipt.Data["dto"]);
        Assert.Equal("before", dtoSnapshot["Name"]);
        Assert.Equal(
            ["first", "second"],
            Assert.IsAssignableFrom<IEnumerable<object?>>(dtoSnapshot["Items"]));

        var mutableRootView = Assert.IsAssignableFrom<IDictionary<string, object?>>(receipt.Data);
        Assert.True(mutableRootView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableRootView.Add("forged", true));
        var mutableDtoView = Assert.IsAssignableFrom<IDictionary<string, object?>>(dtoSnapshot);
        Assert.Throws<NotSupportedException>(() => mutableDtoView["Name"] = "forged");
        var mutableItemsView = Assert.IsAssignableFrom<IList<object?>>(dtoSnapshot["Items"]);
        Assert.True(mutableItemsView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableItemsView.Add("forged"));
    }

    [Fact]
    public async Task Deep_json_result_is_rejected_without_becoming_success()
    {
        var json = new string('[', 40) + "0" + new string(']', 40);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var element = document.RootElement.Clone();
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(Receipt(
                invocation,
                ReceiptStatus.Succeeded,
                new Dictionary<string, object?> { ["deep"] = element }))));

        AssertInvalidSnapshot(envelope);
    }

    [Fact]
    public async Task Oversized_json_string_is_rejected_without_echoing_payload()
    {
        const string marker = "DO_NOT_ECHO_HOSTILE_PAYLOAD";
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(marker + new string('x', 300_000)));
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(Receipt(
                invocation,
                ReceiptStatus.Succeeded,
                new Dictionary<string, object?> { ["huge"] = document }))));

        AssertInvalidSnapshot(envelope);
        Assert.DoesNotContain(marker, Assert.Single(envelope.Receipts.Items).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            marker,
            Assert.Single(
                    envelope.Details.Events,
                    item => item.Type == "receipt.emitted" &&
                        item.Diagnostics?.Code == "tool.result.snapshot.invalid")
                .Diagnostics!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_binary_result_is_rejected_and_never_exposes_a_mutable_array()
    {
        var bytes = new byte[300_000];
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(Receipt(
                invocation,
                ReceiptStatus.Succeeded,
                new Dictionary<string, object?> { ["binary"] = bytes }))));

        AssertInvalidSnapshot(envelope);
        Assert.DoesNotContain(envelope.Receipts.Items, receipt =>
            receipt.Data.Values.Any(value => value is byte[]));
    }

    [Fact]
    public async Task Aggregate_node_budget_is_shared_across_nested_collections()
    {
        var manyNodes = Enumerable.Range(0, 100)
            .Select(_ => (object?)Enumerable.Range(0, 200)
                .Select(value => (object?)value)
                .ToList())
            .ToList();
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(Receipt(
                invocation,
                ReceiptStatus.Succeeded,
                new Dictionary<string, object?> { ["manyNodes"] = manyNodes }))));

        AssertInvalidSnapshot(envelope);
    }

    [Fact]
    public async Task Aggregate_byte_budget_is_shared_across_the_entire_result()
    {
        var boundedString = new string('x', 240_000);
        var receiptData = new Dictionary<string, object?>
        {
            ["one"] = boundedString,
            ["two"] = boundedString
        };
        var observationData = new Dictionary<string, object?>
        {
            ["three"] = boundedString,
            ["four"] = boundedString
        };
        var artifactData = new Dictionary<string, object?>
        {
            ["five"] = boundedString
        };
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(
                Receipt(invocation, ReceiptStatus.Succeeded, receiptData),
                new Observation(
                    "raw_observation",
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Bounded summary.",
                    observationData,
                    []),
                new Artifact("raw_artifact", "bounded.artifact", artifactData, []))));

        AssertInvalidSnapshot(envelope);
    }

    [Fact]
    public async Task Oversized_unknown_dto_is_bounded_and_fails_closed()
    {
        const string marker = "UNKNOWN_DTO_SECRET";
        var dto = new MutablePayload
        {
            Name = marker + new string('x', 1_100_000),
            Items = []
        };
        var envelope = await RunAsync(new FixedResultTool(invocation =>
            new ToolResult(Receipt(
                invocation,
                ReceiptStatus.Succeeded,
                new Dictionary<string, object?> { ["dto"] = dto }))));

        AssertInvalidSnapshot(envelope);
        Assert.DoesNotContain(marker, Assert.Single(envelope.Receipts.Items).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_identity_is_restored_only_at_the_tool_invocation_boundary()
    {
        const string sourceObservationId = "source_observation_token";
        const string observeToolId = "identity.observe";
        const string consumeToolId = "identity.consume";
        var consumer = new IdentityConsumerTool(sourceObservationId);
        var planner = new IdentityRoundTripPlanner(observeToolId, consumeToolId);
        var observer = new FixedResultTool(invocation =>
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded);
            return new ToolResult(
                receipt,
                new Observation(
                    sourceObservationId,
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Source identity issued.",
                    new Dictionary<string, object?>
                    {
                        ["boundObservationId"] = sourceObservationId
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)]));
        });
        var catalog = ToolCatalog.Create(
            Registration(observeToolId, observer),
            Registration(consumeToolId, consumer));
        var runner = new AgenticaRunner(
            planner,
            catalog,
            new InMemoryEventSink(),
            new TestOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 3,
                MaxRefinements: 1,
                PlanningMode: PlanningMode.Stepwise,
                MaxBlockedRetries: 0),
            completionEvaluator: PlanExhaustionCompletionEvaluator.Instance);

        var envelope = await runner.RunAsync(new RunRequest("Round-trip a canonical identity."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var observation = Assert.Single(envelope.Details.Observations);
        Assert.NotEqual(sourceObservationId, observation.ObservationId);
        Assert.Equal(observation.ObservationId, observation.Data["boundObservationId"]);
        Assert.Equal(observation.ObservationId, planner.CanonicalObservationId);
        Assert.Equal(sourceObservationId, consumer.ReceivedObservationId);
    }

    [Fact]
    public async Task Null_result_becomes_failed_canonical_receipt()
    {
        var envelope = await RunAsync(new NullResultTool());

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolFailure, envelope.Outcome.StopReason);
        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal(StepId, receipt.StepId);
        Assert.Equal(ToolId, receipt.ToolId);
        Assert.StartsWith("receipt_", receipt.ReceiptId, StringComparison.Ordinal);
        Assert.Equal(true, receipt.Data["resultInvalid"]);
        Assert.Empty(envelope.Details.Observations);
        Assert.Empty(envelope.Details.Artifacts);
        var receiptEvent = Assert.Single(envelope.Details.Events, item => item.Type == "receipt.emitted");
        Assert.Equal("tool.result.required", receiptEvent.Diagnostics?.Code);
    }

    [Theory]
    [InlineData((int)ReceiptStatus.Accepted, RunOutcomeStatus.PartiallyComplete)]
    [InlineData((int)ReceiptStatus.Partial, RunOutcomeStatus.PartiallyComplete)]
    [InlineData(999, RunOutcomeStatus.Failed)]
    public async Task Nonterminal_or_undefined_status_cannot_succeed(
        int rawStatus,
        RunOutcomeStatus expectedStatus)
    {
        var tool = new FixedResultTool(invocation =>
            new ToolResult(Receipt(invocation, (ReceiptStatus)rawStatus)));

        var envelope = await RunAsync(tool);

        Assert.Equal(expectedStatus, envelope.Outcome.Status);
        Assert.NotEqual(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var receipt = Assert.Single(envelope.Receipts.Items);
        if (rawStatus == 999)
        {
            Assert.Equal(ReceiptStatus.Failed, receipt.Status);
            var receiptEvent = Assert.Single(envelope.Details.Events, item => item.Type == "receipt.emitted");
            Assert.Equal("tool.result.status.invalid", receiptEvent.Diagnostics?.Code);
        }
        else
        {
            Assert.Equal((ReceiptStatus)rawStatus, receipt.Status);
            Assert.Equal(StopReason.Partial, envelope.Outcome.StopReason);
        }
    }

    [Fact]
    public async Task Cyclic_result_data_is_rejected_without_escaping_or_succeeding()
    {
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;
        var tool = new FixedResultTool(invocation =>
            new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded, cyclic)));

        var envelope = await RunAsync(tool);

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal(true, receipt.Data["resultInvalid"]);
        var receiptEvent = Assert.Single(envelope.Details.Events, item => item.Type == "receipt.emitted");
        Assert.Equal("tool.result.snapshot.invalid", receiptEvent.Diagnostics?.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_child_record_invalidates_success(bool malformedArtifact)
    {
        var tool = new FixedResultTool(invocation =>
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded);
            return malformedArtifact
                ? new ToolResult(
                    receipt,
                    Artifact: new Artifact("raw_artifact", " ", new Dictionary<string, object?>(), []))
                : new ToolResult(
                    receipt,
                    new Observation(
                        "raw_observation",
                        invocation.StepId,
                        (ObservationKind)999,
                        "Invalid kind.",
                        new Dictionary<string, object?>(),
                        []));
        });

        var envelope = await RunAsync(tool);

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(ReceiptStatus.Failed, Assert.Single(envelope.Receipts.Items).Status);
        Assert.Empty(envelope.Details.Observations);
        Assert.Empty(envelope.Details.Artifacts);
    }

    private static void AssertInvalidSnapshot(OutcomeEnvelope envelope)
    {
        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolFailure, envelope.Outcome.StopReason);
        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal(true, receipt.Data["resultInvalid"]);
        var receiptEvent = Assert.Single(
            envelope.Details.Events,
            item => item.Type == "receipt.emitted" &&
                item.Diagnostics?.Code == "tool.result.snapshot.invalid");
        Assert.Equal("InvalidToolResult", receiptEvent.Diagnostics?.FailureKind);
    }

    private static async Task<OutcomeEnvelope> RunAsync(ITool tool)
    {
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(ToolId, "Hostile Read", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = new AgenticaRunner(
            new SingleStepPlanner(),
            catalog,
            new InMemoryEventSink(),
            new TestOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0),
            completionEvaluator: PlanExhaustionCompletionEvaluator.Instance);

        return await runner.RunAsync(new RunRequest("Exercise the hostile tool-result boundary."));
    }

    private static ToolRegistration Registration(string toolId, ITool tool) =>
        TestToolRegistration.Create(
            new ToolDescriptor(toolId, toolId, ToolKind.Query, ToolEffect.ReadOnly),
            tool);

    private static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        IReadOnlyDictionary<string, object?>? data = null) =>
        new(
            "raw_receipt",
            invocation.StepId,
            invocation.ToolId,
            status,
            $"Raw tool returned {status}.",
            DateTimeOffset.UtcNow,
            data ?? new Dictionary<string, object?>());

    private static void AssertCanonicalReceiptEvidence(
        IReadOnlyList<EvidenceRef> evidence,
        string receiptId)
    {
        var reference = Assert.Single(evidence);
        Assert.Equal("receipt", reference.Kind);
        Assert.Equal(receiptId, reference.RefId);
    }

    private sealed class FixedResultTool : ITool
    {
        private readonly Func<ToolInvocation, ToolResult> _resultFactory;

        public FixedResultTool(Func<ToolInvocation, ToolResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(_resultFactory(invocation));
    }

    private sealed class NullResultTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolResult>(null!);
    }

    private sealed class MutablePayload
    {
        public string Name { get; set; } = string.Empty;

        public List<string> Items { get; set; } = [];
    }

    private sealed class IdentityConsumerTool : ITool
    {
        private readonly string _expectedSourceObservationId;

        public IdentityConsumerTool(string expectedSourceObservationId)
        {
            _expectedSourceObservationId = expectedSourceObservationId;
        }

        public string? ReceivedObservationId { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ReceivedObservationId = Convert.ToString(invocation.Input["observationId"]);
            var status = string.Equals(
                ReceivedObservationId,
                _expectedSourceObservationId,
                StringComparison.Ordinal)
                ? ReceiptStatus.Succeeded
                : ReceiptStatus.Refused;
            return Task.FromResult(new ToolResult(Receipt(invocation, status)));
        }
    }

    private sealed class IdentityRoundTripPlanner : IWorkflowPlanner
    {
        private readonly string _observeToolId;
        private readonly string _consumeToolId;

        public IdentityRoundTripPlanner(string observeToolId, string consumeToolId)
        {
            _observeToolId = observeToolId;
            _consumeToolId = consumeToolId;
        }

        public string? CanonicalObservationId { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "plan_identity_observe",
                1,
                [new PlanStep("step_identity_observe", _observeToolId, ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>())],
                "Issue a source identity."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            CanonicalObservationId = observation.ObservationId;
            return Task.FromResult(new WorkflowPlan(
                "plan_identity_consume",
                2,
                [
                    new PlanStep(
                        "step_identity_consume",
                        _consumeToolId,
                        ToolKind.Query,
                        ToolEffect.ReadOnly,
                        new Dictionary<string, object?>
                        {
                            ["observationId"] = observation.ObservationId
                        })
                ],
                "Consume the canonical identity through the tool boundary."));
        }
    }

    private sealed class SingleStepPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "plan_hostile",
                1,
                [new PlanStep(StepId, ToolId, ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>())],
                "Exercise one hostile tool result."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Plan-only execution must not refine.");
    }

    private sealed class TestOutcomeReporter : IOutcomeReporter
    {
        public OutcomeReport BuildReport(
            AgenticaRun run,
            RunOutcomeStatus status,
            StopReason stopReason,
            IReadOnlyList<ValidationIssue> validationIssues,
            IReadOnlyList<string> blockers) =>
            new(
                AgenticaIds.New("report"),
                $"Run stopped with {status}.",
                [new ReportClaim("The runtime reported its terminal status.", [new EvidenceRef("stopReason", stopReason.ToString())])]);
    }
}

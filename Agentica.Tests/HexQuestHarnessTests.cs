using Agentica.Artifacts;
using Agentica.CLI.Scenarios.HexQuest;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class HexQuestHarnessTests
{
    [Fact]
    public void HexQuest_commit_refuses_data_byte_patch_without_checksum_update()
    {
        var session = new HexQuestSession(new HexQuestBoard().Load("xor_checksum_strength"));

        var result = session.Execute(Invocation(HexQuestToolIds.CommitPatch, ("patch", "0:A9>B7")));

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.False(session.State.Completed);
        Assert.Contains("checksum", result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HexQuest_commit_succeeds_only_through_encoded_patch_surface()
    {
        var session = new HexQuestSession(new HexQuestBoard().Load("xor_checksum_strength"));

        var result = session.Execute(Invocation(HexQuestToolIds.CommitPatch, ("patch", "0:A9>B7,4:E8>E6")));

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("hexquest.objective_completed", result.Artifact?.Kind);
        Assert.True(session.State.Completed);
        var decoded = HexQuestCodec.Decode(session.State.Encoded);
        Assert.Equal(18, decoded.Strength);
        Assert.Equal(9, decoded.Dexterity);
        Assert.Equal(250, decoded.Gold);
        Assert.True(HexQuestCodec.HasValidChecksum(session.State.Encoded));
    }

    [Fact]
    public async Task HexQuest_deterministic_runner_completes_intro_scenario()
    {
        var scenario = new HexQuestBoard().Load("xor_checksum_strength");
        var session = new HexQuestSession(scenario);
        var runner = new AgenticaRunner(
            new HexQuestDeterministicPlanner(),
            HexQuestTools.CreateCatalog(session),
            new InMemoryEventSink(),
            new HexQuestOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 6),
            EvidenceCompletionEvaluator.ForArtifactKind("hexquest.objective_completed"));

        var envelope = await runner.RunAsync(new RunRequest(scenario.Descriptor.Objective, RequestOrigin.User, session.PublicSnapshot()));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.Artifacts, artifact => artifact.Kind == "hexquest.objective_completed");
        Assert.Contains(envelope.Receipts.Items, receipt => receipt.ToolId == HexQuestToolIds.CommitPatch && receipt.Status == ReceiptStatus.Succeeded);
    }

    [Fact]
    public async Task HexQuest_deterministic_runner_completes_record_scope_scenario()
    {
        var scenario = new HexQuestBoard().Load("record_scope_conflict");
        var session = new HexQuestSession(scenario);
        var runner = new AgenticaRunner(
            new HexQuestDeterministicPlanner(scenario.Descriptor.ScenarioId),
            HexQuestTools.CreateCatalog(session),
            new InMemoryEventSink(),
            new HexQuestOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 6),
            EvidenceCompletionEvaluator.ForArtifactKind("hexquest.objective_completed"));

        var envelope = await runner.RunAsync(new RunRequest(scenario.Descriptor.Objective, RequestOrigin.User, session.PublicSnapshot()));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.Artifacts, artifact => artifact.Kind == "hexquest.objective_completed");
    }

    [Fact]
    public void HexQuest_record_scope_sandbox_diff_is_overbroad_but_commit_requires_bounded_authoritative_patch()
    {
        var session = new HexQuestSession(new HexQuestBoard().Load("record_scope_conflict"));

        var sandbox = session.Execute(Invocation(
            HexQuestToolIds.SandboxSetDecoded,
            ("entity", "B"),
            ("field", "Strength"),
            ("value", 18)));

        Assert.Equal(ReceiptStatus.Succeeded, sandbox.Receipt.Status);
        var diff = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(sandbox.Receipt.Data["diff"]);
        Assert.Contains(diff, item => Equals(item["offset"], 16));
        Assert.Contains(diff, item => Equals(item["offset"], 32));
        Assert.Contains(diff, item => Equals(item["offset"], 40));
        Assert.Contains(diff, item => Equals(item["offset"], 48));

        var overbroad = session.Execute(Invocation(
            HexQuestToolIds.CommitPatch,
            ("patch", "16:A9>B7,32:A9>B7,40:A9>B7,48:1C>12")));

        Assert.Equal(ReceiptStatus.Refused, overbroad.Receipt.Status);
        Assert.Contains("at most 2", overbroad.Receipt.Message, StringComparison.OrdinalIgnoreCase);

        var accepted = session.Execute(Invocation(
            HexQuestToolIds.CommitPatch,
            ("patch", "16:A9>B7,48:1C>12")));

        Assert.Equal(ReceiptStatus.Succeeded, accepted.Receipt.Status);
        Assert.True(session.State.Completed);
        var decoded = HexQuestCodec.Decode(new HexQuestBoard().Load("record_scope_conflict"), session.State.Encoded);
        Assert.Equal(18, decoded.Strength);
        Assert.Equal(12, decoded.Characters![0].Strength);
        Assert.Equal(12, decoded.Characters![2].Strength);
    }

    [Fact]
    public void HexQuest_record_scope_rejects_wrong_matching_record()
    {
        var session = new HexQuestSession(new HexQuestBoard().Load("record_scope_conflict"));

        var result = session.Execute(Invocation(HexQuestToolIds.CommitPatch, ("patch", "8:A9>B7")));

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.False(session.State.Completed);
    }

    [Fact]
    public void HexQuest_v2_hides_constraints_and_requires_contrastive_probe()
    {
        var scenario = new HexQuestBoard().Load("record_scope_conflict_v2");
        var session = new HexQuestSession(scenario);

        var decoded = session.Execute(Invocation(HexQuestToolIds.InspectDecoded));
        var goal = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(decoded.Receipt.Data["goal"]);

        Assert.Null(goal["forbiddenOffsets"]);
        Assert.Contains("bounded", Assert.IsType<string>(goal["patchConstraint"]), StringComparison.OrdinalIgnoreCase);

        var validate = session.Execute(Invocation(HexQuestToolIds.ValidatePatch, ("patch", "32:A9>B7,120:D6>D8")));

        Assert.Equal(ReceiptStatus.Succeeded, validate.Receipt.Status);
        Assert.False(Assert.IsType<bool>(validate.Receipt.Data["accepted"]));
        Assert.Contains("contrastive_probe_missing", Assert.IsAssignableFrom<IReadOnlyList<string>>(validate.Receipt.Data["failureCategories"]));
    }

    [Fact]
    public void HexQuest_v2_rejects_full_sandbox_diff_and_accepts_narrow_authoritative_patch()
    {
        var scenario = new HexQuestBoard().Load("record_scope_conflict_v2");
        var session = new HexQuestSession(scenario);

        session.Execute(Invocation(
            HexQuestToolIds.SandboxSetDecoded,
            ("entity", "A"),
            ("field", "Strength"),
            ("value", 18)));
        var targetSandbox = session.Execute(Invocation(
            HexQuestToolIds.SandboxSetDecoded,
            ("entity", "B"),
            ("field", "Strength"),
            ("value", 18)));
        var diff = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(targetSandbox.Receipt.Data["diff"]);

        Assert.True(diff.Count >= 8);
        Assert.Contains(diff, item => Equals(item["offset"], 32));
        Assert.Contains(diff, item => Equals(item["offset"], 120));
        Assert.Contains(diff, item => Equals(item["offset"], 121));

        var fullDiffPatch = string.Join(
            ',',
            diff.Select(item => $"{item["offset"]}:{item["old"]}>{item["new"]}"));
        var overbroad = session.Execute(Invocation(HexQuestToolIds.ValidatePatch, ("patch", fullDiffPatch)));

        Assert.False(Assert.IsType<bool>(overbroad.Receipt.Data["accepted"]));
        Assert.Contains("patch_budget_exceeded", Assert.IsAssignableFrom<IReadOnlyList<string>>(overbroad.Receipt.Data["failureCategories"]));

        var accepted = session.Execute(Invocation(HexQuestToolIds.CommitPatch, ("patch", "32:A9>B7,120:D6>D8")));

        Assert.Equal(ReceiptStatus.Succeeded, accepted.Receipt.Status);
        Assert.True(session.State.Completed);
        var after = HexQuestCodec.Decode(scenario, session.State.Encoded);
        var characterB = after.Characters!.Single(item => item.EntityId == "B");
        Assert.Equal(18, characterB.Strength);
        Assert.Equal(12, characterB.DisplayStrength);
        Assert.True(HexQuestCodec.HasValidChecksum(scenario, session.State.Encoded));
    }

    [Fact]
    public async Task HexQuest_deterministic_runner_completes_v2_scenario()
    {
        var scenario = new HexQuestBoard().Load("record_scope_conflict_v2");
        var session = new HexQuestSession(scenario);
        var events = new InMemoryEventSink();
        var runner = new AgenticaRunner(
            new HexQuestDeterministicPlanner(scenario.Descriptor.ScenarioId),
            HexQuestTools.CreateCatalog(session),
            events,
            new HexQuestOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 6),
            EvidenceCompletionEvaluator.ForArtifactKind("hexquest.objective_completed"));

        var envelope = await runner.RunAsync(new RunRequest(scenario.Descriptor.Objective, RequestOrigin.User, session.PublicSnapshot()));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.Artifacts, artifact => artifact.Kind == "hexquest.objective_completed");
        Assert.NotEmpty(envelope.Details.ToolSurfaces);
        var stepEvents = events.Events.Where(executionEvent => executionEvent.Type == "step.started").ToArray();
        Assert.NotEmpty(stepEvents);
        Assert.All(stepEvents, executionEvent =>
        {
            Assert.NotNull(executionEvent.Intent);
            Assert.NotNull(executionEvent.Context?.ToolSurfaceId);
            Assert.Contains(envelope.Details.ToolSurfaces, surface => surface.SurfaceId == executionEvent.Context!.ToolSurfaceId);
            Assert.DoesNotContain("forbiddenOffsets", executionEvent.Intent!.Rationale, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(
            envelope.Details.ToolSurfaces.Last().ToolDescriptors,
            descriptor => descriptor.ToolId == HexQuestToolIds.ValidatePatch);
    }

    private static ToolInvocation Invocation(
        string toolId,
        params (string Key, object? Value)[] input) =>
        new(
            "run_test",
            "step_test",
            toolId,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

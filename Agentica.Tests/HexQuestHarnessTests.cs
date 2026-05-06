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

    private static ToolInvocation Invocation(
        string toolId,
        params (string Key, object? Value)[] input) =>
        new(
            "run_test",
            "step_test",
            toolId,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

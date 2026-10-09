using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class ActionCustodyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentica-custody-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RestartRetainsOriginalRequestAndFencesSessionAcrossEpochs()
    {
        var store = new ActionCustodyStore(_root);
        var request = Request("original");
        var reservation = Reserve(store, request);
        var restarted = new ActionCustodyStore(_root);
        var pending = Assert.Single(restarted.ListUnresolved("host", "session"));

        Assert.Equal(reservation.RequestHash, pending.RequestHash);
        Assert.Equal("original", pending.Request.ActionId);
        Assert.Equal(request.Arguments.GetRawText(), pending.Request.Arguments.GetRawText());
        Assert.Equal(request.DeadlineAt, pending.Request.DeadlineAt);
        Assert.Equal("objective", pending.ObjectiveId);
        Assert.Single(restarted.ListUnresolvedForRun("run"));
        Assert.True(restarted.HasUnresolved("host", "session"));
        Assert.False(restarted.HasUnresolved("other-host", "session"));
        var nextEpoch = Request("new") with { SessionEpoch = "next-epoch", RunId = "next-run" };
        var error = Assert.Throws<HostProtocolException>(() => restarted.Reserve("host", "session", "next-epoch", "next-run", nextEpoch, Capability(), "objective"));
        Assert.Equal("custody.session_unresolved", error.Code);
        Assert.Single(restarted.ListUnresolved());
    }

    [Fact]
    public void TerminalReconciliationClearsFenceWithoutDispatchAndAllowsExactReplay()
    {
        var store = new ActionCustodyStore(_root);
        var original = Request("original");
        Reserve(store, original);
        var restarted = new ActionCustodyStore(_root);
        var result = Result(original);
        Assert.True(restarted.Resolve("host", "session", original.ActionId, result));
        Assert.False(restarted.Resolve("host", "session", original.ActionId, result));
        Assert.Empty(restarted.ListUnresolved());
        var retained = restarted.Get("host", "session", original.ActionId);
        Assert.Equal("applied", retained.Result!.Disposition);
        Assert.Equal(ProtocolValidation.Digest(result), retained.ResultHash);
        Assert.False(new ActionCustodyStore(_root).HasUnresolved("host", "session"));
        Assert.Throws<HostProtocolException>(() => restarted.Resolve("host", "session", original.ActionId, result with { Summary = "Changed result" }));
        Reserve(restarted, Request("next"));
        Assert.Equal("next", Assert.Single(restarted.ListUnresolved()).Request.ActionId);
    }

    [Fact]
    public void AmbiguousResultRemainsFencedUntilOriginalActionIsReconciled()
    {
        var store = new ActionCustodyStore(_root);
        var original = Request("original");
        Reserve(store, original);
        var unknown = Result(original) with { Disposition = "unresolved", AfterRevision = 7, Observation = null, Completion = null };
        Assert.True(store.Resolve("host", "session", original.ActionId, unknown));
        Assert.True(new ActionCustodyStore(_root).HasUnresolved("host", "session"));
        Assert.Equal("unresolved", Assert.Single(store.ListUnresolved()).Result!.Disposition);
        Assert.True(store.Resolve("host", "session", original.ActionId, Result(original)));
        Assert.False(store.HasUnresolved("host", "session"));
    }

    [Fact]
    public void ReserveIsIdempotentAndRejectsChangedArgumentsOrCapability()
    {
        var store = new ActionCustodyStore(_root);
        var request = Request("original");
        var first = Reserve(store, request);
        var replay = Reserve(store, request);
        Assert.Equal(first.ReservedAt, replay.ReservedAt);
        Assert.Single(store.ListUnresolved());
        Assert.Throws<HostProtocolException>(() => Reserve(store, request with { Arguments = HostProtocol.Element(new { direction = "changed" }) }));
        Assert.Throws<HostProtocolException>(() => store.Reserve("host", "session", "epoch", "run", request, Capability() with { Effect = ToolEffect.ReadOnly }, "objective"));
        Assert.Equal(first.RequestHash, store.Get("host", "session", "original").RequestHash);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("session")]
    [InlineData("action")]
    [InlineData("revision")]
    [InlineData("missing_observation")]
    [InlineData("observation_revision")]
    [InlineData("objective")]
    [InlineData("stale_current_revision")]
    public void InvalidResolutionCannotClearCustody(string mismatch)
    {
        var store = new ActionCustodyStore(_root);
        var request = Request("original");
        var entry = Reserve(store, request);
        var result = Result(request);
        result = mismatch switch
        {
            "epoch" => result with { SessionEpoch = "another" },
            "session" => result with { SessionId = "another" },
            "action" => result with { ActionId = "another" },
            "revision" => result with { BeforeRevision = 6 },
            "missing_observation" => result with { Observation = null },
            "observation_revision" => result with { Observation = result.Observation! with { Revision = 9 } },
            "objective" => result with { Completion = new("other-objective", "complete", "Not the admitted objective") },
            _ => result
        };
        if (mismatch == "stale_current_revision")
            Assert.Throws<HostProtocolException>(() => ActionCustodyStore.ValidateResolution(entry, result, minimumRevision: 9));
        else Assert.Throws<HostProtocolException>(() => store.Resolve("host", "session", request.ActionId, result));
        Assert.True(new ActionCustodyStore(_root).HasUnresolved("host", "session"));
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("conflict")]
    [InlineData("unavailable")]
    public void NonAppliedResultCannotClaimMutation(string disposition)
    {
        var store = new ActionCustodyStore(_root);
        var request = Request("original");
        Reserve(store, request);
        Assert.Throws<HostProtocolException>(() => store.Resolve("host", "session", request.ActionId, Result(request) with { Disposition = disposition, Completion = null }));
        Assert.True(store.HasUnresolved("host", "session"));
    }

    [Fact]
    public void ReadOnlyCapabilityCannotReportMutationAndEvidenceCannotBeReused()
    {
        var store = new ActionCustodyStore(_root);
        var original = Request("original");
        store.Reserve("host", "session", "epoch", "run", original, Capability() with { Effect = ToolEffect.ReadOnly }, "objective");
        Assert.Throws<HostProtocolException>(() => store.Resolve("host", "session", original.ActionId, Result(original)));
        var result = Result(original) with { AfterRevision = 7, Observation = Observation(7), Completion = null };
        Assert.True(store.Resolve("host", "session", original.ActionId, result));
        var next = Request("next");
        Reserve(store, next);
        Assert.Throws<HostProtocolException>(() => store.Resolve("host", "session", next.ActionId, Result(next) with { EvidenceId = result.EvidenceId }));
        Assert.True(store.HasUnresolved("host", "session"));
    }

    [Fact]
    public void PrevalidationRejectsCrossRunEvidenceReuseWithoutChangingCustody()
    {
        var store = new ActionCustodyStore(_root);
        var first = Request("first");
        Reserve(store, first);
        var completed = Result(first);
        store.Resolve("host", "session", first.ActionId, completed);
        var second = Request("second") with { RunId = "second-run" };
        store.Reserve("host", "session", "epoch", "second-run", second, Capability(), "objective");
        var path = Path.Combine(_root, ActionCustodyStore.LedgerFileName);
        var before = File.ReadAllText(path);
        var duplicateEvidence = Result(second) with { EvidenceId = completed.EvidenceId };
        var error = Assert.Throws<HostProtocolException>(() => store.PrevalidateResolution("host", "session", second.ActionId, duplicateEvidence));
        Assert.Equal("custody.evidence_reused", error.Code);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Null(store.Get("host", "session", second.ActionId).Result);

        var valid = Result(second);
        var entry = store.PrevalidateResolution("host", "session", second.ActionId, valid, minimumRevision: 8);
        Assert.Equal(second.ActionId, entry.Request.ActionId);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.True(store.HasUnresolved("host", "session"));
        Assert.True(store.Resolve("host", "session", second.ActionId, valid));
        Assert.Throws<HostProtocolException>(() => store.PrevalidateResolution("host", "session", second.ActionId, valid with { Summary = "Changed terminal" }));
    }

    [Fact]
    public void CorruptOrMissingLedgerFailsClosedAtStartupAndWhileRunning()
    {
        var store = new ActionCustodyStore(_root);
        Reserve(store, Request("original"));
        var path = Path.Combine(_root, ActionCustodyStore.LedgerFileName);
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("north", "south", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => new ActionCustodyStore(_root));
        Assert.Throws<InvalidDataException>(() => store.HasUnresolved("host", "session"));
        File.WriteAllText(path, original);
        File.Delete(path);
        Assert.Throws<InvalidDataException>(() => new ActionCustodyStore(_root));
        Assert.Throws<InvalidDataException>(() => store.ListUnresolved());
    }

    [Fact]
    public void CorruptResultHashFailsEvenIfOuterHashIsRecomputed()
    {
        var store = new ActionCustodyStore(_root);
        var request = Request("original");
        Reserve(store, request);
        store.Resolve("host", "session", request.ActionId, Result(request));
        var path = Path.Combine(_root, ActionCustodyStore.LedgerFileName);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node["payload"]!["entries"]![0]!["resultHash"] = "invalid";
        node["contentHash"] = ProtocolValidation.Digest(JsonSerializer.SerializeToElement(node["payload"], HostProtocol.Json));
        File.WriteAllText(path, node.ToJsonString(HostProtocol.Json));
        Assert.Throws<InvalidDataException>(() => new ActionCustodyStore(_root));
    }

    [Fact]
    public void CompletedRetentionNeverRemovesAnotherSessionsPendingOriginal()
    {
        var store = new ActionCustodyStore(_root);
        var pending = Request("pending") with { SessionId = "pending-session" };
        store.Reserve("host", "pending-session", "epoch", "run", pending, Capability(), "objective");
        for (var index = 0; index <= ActionCustodyStore.MaximumCompletedPerSession; index++)
        {
            var request = Request("completed-" + index);
            Reserve(store, request);
            store.Resolve("host", "session", request.ActionId, Result(request));
        }
        var restarted = new ActionCustodyStore(_root);
        Assert.Equal("pending", Assert.Single(restarted.ListUnresolved()).Request.ActionId);
        Assert.Equal(1, restarted.PrunedCompletedCount);
        Assert.Throws<HostProtocolException>(() => restarted.Get("host", "session", "completed-0"));
        Assert.NotNull(restarted.Get("host", "session", "completed-256").Result);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static ActionCustodyEntry Reserve(ActionCustodyStore store, HostActionRequest request) =>
        store.Reserve("host", "session", "epoch", "run", request, Capability(), "objective");

    private static HostActionRequest Request(string id) => new(id, "run", "runner", "step-" + id, "session", "epoch", "host.move",
        "sha256:manifest", HostProtocol.Element(new { direction = "north" }), 7, DateTimeOffset.UtcNow.AddMinutes(1));

    private static HostCapability Capability() => new("host.move", "Move", "Apply a bounded host action", ToolKind.Action,
        ToolEffect.WritesLocalState, ToolInputSchema.Create(new ToolInputField("direction", Required: true)));

    private static HostObservation Observation(long revision) => new("observation-" + revision, revision,
        DateTimeOffset.UnixEpoch.AddSeconds(revision), HostProtocol.Element(new { revision }));

    private static HostActionResult Result(HostActionRequest request) => new(request.ActionId, request.SessionId, request.SessionEpoch,
        "applied", 7, 8, "evidence-" + request.ActionId, "Host verified the original action", Observation(8),
        new("objective", "completion-" + request.ActionId, "Host verified the objective"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

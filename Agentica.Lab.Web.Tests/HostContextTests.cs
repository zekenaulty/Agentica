using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class HostContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentica-host-context-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReloadKeepsScopedKnowledgeAndRequiresFreshCurrentObservation()
    {
        var first = Observation("first", 1, Fact("item", "pending"));
        var initial = new HostContextStore(_root).Open(Request(first));
        var hash = initial.ReadEvidence("first").Reference!.ContentHash;

        var reloaded = new HostContextStore(_root).Open(Request(Observation("second", 2)));

        Assert.Equal("second", reloaded.CurrentObservation.ObservationId);
        Assert.Equal("pending", Assert.Single(reloaded.Snapshot().Facts).Value.GetString());
        Assert.Equal(hash, reloaded.ReadEvidence("first").Reference!.ContentHash);
        Assert.Equal("available", reloaded.ReadEvidence("first").Status);
        Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("perspective")]
    [InlineData("epoch")]
    [InlineData("scope")]
    [InlineData("host")]
    [InlineData("session")]
    public void IncompatibleIdentityCannotReadOtherContext(string field)
    {
        var store = new HostContextStore(_root);
        store.Open(Request(Observation("private-to-perspective", 1, Fact("known", "value"))));
        var different = Request(Observation("other", 0));
        different = field switch
        {
            "perspective" => different with { PerspectiveId = "another" },
            "epoch" => different with { SessionEpoch = "another" },
            "scope" => different with { ScopeId = "another" },
            "host" => different with { HostId = "another" },
            _ => different with { SessionId = "another" }
        };

        var session = store.Open(different);

        Assert.Empty(session.Snapshot().Facts);
        Assert.Equal("unknown", session.ReadEvidence("private-to-perspective").Status);
        Assert.Equal(2, Directory.GetFiles(_root, "*.json").Length);
    }

    [Fact]
    public void SourceMutationUnknownEvidenceAndRevisionRewindAreRejectedAtomically()
    {
        var store = new HostContextStore(_root);
        var session = store.Open(Request(Observation("original", 3, Fact("status", "pending"))));
        var before = File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.json")));
        Assert.Throws<InvalidOperationException>(() => session.Record(Observation("original", 3, Fact("status", "accepted"))));
        Assert.Throws<InvalidOperationException>(() => session.Record(Observation("past", 2)));
        Assert.Throws<InvalidOperationException>(() => session.Record(Observation("bad-reference", 4,
            Fact("status", "accepted") with { EvidenceObservationIds = ["missing"] })));
        Assert.Equal("original", session.CurrentObservation.ObservationId);
        Assert.Equal(before, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.json"))));
    }

    [Fact]
    public void ExplicitCorrectionRetainsContradictedSourceAndSupersession()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("before", 1, Fact("state", "open"))));
        var old = Assert.Single(session.Snapshot().Facts);
        session.Record(Observation("after", 2, Fact("state", "closed") with { Supersedes = old.Id }));

        var snapshot = session.Snapshot();
        var current = Assert.Single(snapshot.Facts, fact => fact.IsCurrent);
        var retired = Assert.Single(snapshot.Facts, fact => !fact.IsCurrent);
        Assert.Equal("observed", current.State);
        Assert.Equal("corrected", current.Change);
        Assert.Equal(old.Id, current.Supersedes);
        Assert.Equal("stale", retired.State);
        Assert.Equal("before", Assert.Single(retired.Evidence).ObservationId);
        Assert.Equal("after", Assert.Single(current.Evidence).ObservationId);
        Assert.Equal("available", session.ReadEvidence("before").Status);
    }

    [Fact]
    public async Task ModelHypothesisCannotPromoteItselfOrReplaceHostFacts()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1, Fact("state", "closed"))));
        var tool = session.CreateTools().Single(registration => registration.Descriptor.ToolId == "lab.hypothesis.record").Tool;
        var invocation = Hypothesis("one", "state", "open", "inferred");
        var recorded = await tool.ExecuteAsync(invocation, TestContext.Current.CancellationToken);
        var replayed = await tool.ExecuteAsync(invocation, TestContext.Current.CancellationToken);
        var promoted = await tool.ExecuteAsync(Hypothesis("two", "state", "open", "observed"), TestContext.Current.CancellationToken);
        var overwrite = Hypothesis("three", "different", "open", "inferred");
        var hostId = session.Snapshot().Facts.Single(fact => fact.Source == "host").Id;
        overwrite = overwrite with { Input = new Dictionary<string, object?>(overwrite.Input) { ["supersedes"] = hostId } };
        var replaced = await tool.ExecuteAsync(overwrite, TestContext.Current.CancellationToken);

        Assert.Equal(ReceiptStatus.Succeeded, recorded.Receipt.Status);
        Assert.Equal(ReceiptStatus.Succeeded, replayed.Receipt.Status);
        Assert.Equal(ReceiptStatus.Refused, promoted.Receipt.Status);
        Assert.Equal(ReceiptStatus.Refused, replaced.Receipt.Status);
        Assert.Equal("closed", session.Snapshot().Facts.Single(fact => fact.Source == "host").Value.GetString());
        Assert.Equal("inferred", session.Snapshot().Facts.Single(fact => fact.Source == "model").State);
    }

    [Fact]
    public async Task CounterEvidenceCanRefuteHypothesisWithoutRewritingObservation()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1)));
        var tool = session.CreateTools().Single(registration => registration.Descriptor.ToolId == "lab.hypothesis.record").Tool;
        await tool.ExecuteAsync(Hypothesis("first", "possible", "open", "inferred"), TestContext.Current.CancellationToken);
        var old = Assert.Single(session.Snapshot().Facts);
        session.Record(Observation("counter", 2));
        var correction = Hypothesis("second", "possible", "closed", "refuted") with
        {
            Input = new Dictionary<string, object?>
            {
                ["key"] = "possible",
                ["summary"] = "Counter evidence refutes the hypothesis.",
                ["value"] = "closed",
                ["state"] = "refuted",
                ["evidenceObservationIds"] = new[] { "host", "counter" },
                ["supersedes"] = old.Id
            }
        };
        var result = await tool.ExecuteAsync(correction, TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        var current = Assert.Single(session.Snapshot().Facts, fact => fact.IsCurrent);
        Assert.Equal("refuted", current.State);
        Assert.Equal(2, current.Evidence.Count);
        Assert.Equal(old.Id, current.Supersedes);
        Assert.Equal("host", session.ReadEvidence("host").Observation!.ObservationId);
    }

    [Fact]
    public async Task ExactEvidenceReadChecksHashAndRefusesForeignSources()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1)));
        var tool = session.CreateTools().Single(registration => registration.Descriptor.ToolId == "lab.evidence.read").Tool;
        var source = session.ReadEvidence("host");
        var valid = await tool.ExecuteAsync(new("run", "read", "lab.evidence.read",
            new Dictionary<string, object?> { ["observationId"] = "host", ["contentHash"] = source.Reference!.ContentHash }), TestContext.Current.CancellationToken);
        var mismatch = await tool.ExecuteAsync(new("run", "mismatch", "lab.evidence.read",
            new Dictionary<string, object?> { ["observationId"] = "host", ["contentHash"] = "wrong" }), TestContext.Current.CancellationToken);
        var missing = await tool.ExecuteAsync(new("run", "missing", "lab.evidence.read",
            new Dictionary<string, object?> { ["observationId"] = "foreign" }), TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Succeeded, valid.Receipt.Status);
        Assert.Equal(ReceiptStatus.Refused, mismatch.Receipt.Status);
        Assert.Equal(ReceiptStatus.Unavailable, missing.Receipt.Status);
        Assert.Equal("host", Assert.Single(valid.Observation!.Evidence, reference => reference.Kind == "host_observation").RefId);
    }

    [Fact]
    public void CompactProjectionRetainsCurrentAndKnowledgeEvidenceReferences()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("observed", 1, Fact("known", "value"))));
        session.Record(Observation("current", 2));
        var request = new PlanningFrameProjectionRequest("run", 1, new RunRequest("objective"), PlanningExecutionContext.Empty, [], [], [], null);
        var frame = Assert.Single(session.Project(request));
        var compact = JsonSerializer.Serialize(frame.CompactPayload, HostProtocol.Json);
        var full = JsonSerializer.Serialize(frame.Payload, HostProtocol.Json);
        Assert.Contains("observed", compact, StringComparison.Ordinal);
        Assert.Contains("current", compact, StringComparison.Ordinal);
        Assert.Contains(session.ReadEvidence("observed").Reference!.ContentHash, compact, StringComparison.Ordinal);
        Assert.Contains("lab.evidence.read", compact, StringComparison.Ordinal);
        Assert.True(compact.Length < full.Length);
        Assert.Contains(frame.EvidenceRefs, reference => reference.RefId == "observed");
        Assert.Contains(frame.EvidenceRefs, reference => reference.RefId == "current");
        Assert.Equal(frame.FrameId, Assert.Single(session.Project(request)).FrameId);
    }

    [Fact]
    public void RetentionPreservesSourceHashAndExplicitlyMarksMissingExactEvidence()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("first", 0, Fact("durable-knowledge", "value"))));
        var hash = session.ReadEvidence("first").Reference!.ContentHash;
        for (var index = 1; index <= HostContextSession.MaximumObservations; index++) session.Record(Observation("next-" + index, index));
        var snapshot = session.Snapshot();
        Assert.Equal(HostContextSession.MaximumObservations, snapshot.RetainedObservationCount);
        var missing = session.ReadEvidence("first");
        Assert.Equal("not_retained", missing.Status);
        Assert.Null(missing.Observation);
        Assert.Equal(hash, missing.Reference!.ContentHash);
        Assert.False(snapshot.Evidence.Single(item => item.Reference.ObservationId == "first").Available);
        Assert.Equal(1, snapshot.PrunedObservationCount);
        Assert.Single(snapshot.Facts);
    }

    [Fact]
    public void CorruptPersistedContentIsRejectedInsteadOfSilentlyReset()
    {
        new HostContextStore(_root).Open(Request(Observation("first", 1, Fact("fact", "before"))));
        var path = Assert.Single(Directory.GetFiles(_root, "*.json"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("before", "after", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => new HostContextStore(_root).Open(Request(Observation("second", 2))));
    }

    [Fact]
    public void RetentionAndProjectionAccountForBoundedFactOmissions()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("initial", 0)));
        for (var revision = 1; revision <= 5; revision++)
            session.Record(Observation("batch-" + revision, revision,
                Enumerable.Range(0, 64).Select(index => Fact($"{revision}-{index}", "known")).ToArray()));
        Assert.Equal(HostContextSession.MaximumFacts, session.Snapshot().Facts.Count);
        var frame = Assert.Single(session.Project(new("run", 1, new RunRequest("objective"), PlanningExecutionContext.Empty, [], [], [], null)));
        Assert.Equal(HostContextSession.MaximumFacts - 48, frame.Payload["omittedCurrentFactCount"]);
        Assert.Equal(HostContextSession.MaximumFacts - 12, frame.CompactPayload!["omittedCurrentFactCount"]);
        var reloaded = new HostContextStore(_root).Open(Request(Observation("fresh", 6)));
        Assert.Equal(HostContextSession.MaximumFacts, reloaded.Snapshot().Facts.Count);
        Assert.Equal(64, reloaded.Snapshot().PrunedFactCount);
    }

    [Fact]
    public void SnapshotCollectionsCannotMutateSessionState()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("first", 1, Fact("fact", "value"))));
        var snapshot = session.Snapshot();
        ((HostKnowledgeEntry[])snapshot.Facts)[0] = snapshot.Facts[0] with { Summary = "changed" };
        Assert.NotEqual("changed", Assert.Single(session.Snapshot().Facts).Summary);
    }

    [Fact]
    public void KnowledgeQueryPagesOmittedFactsAndBindsCursorToScopeAndSnapshot()
    {
        var store = new HostContextStore(_root);
        var session = store.Open(Request(Observation("host", 1,
            Enumerable.Range(0, 64).Select(index => Fact($"known/{index:D2}", "value")).ToArray())));
        var first = session.QueryKnowledge(keyPrefix: "known/", state: "observed", limit: 10);
        Assert.Equal(64, first.TotalMatched);
        Assert.Equal(10, first.Entries.Count);
        Assert.Equal(54, first.RemainingCount);
        Assert.NotNull(first.NextCursor);
        Assert.All(first.Entries, item => Assert.True(Assert.Single(item.Evidence).Available));
        var second = session.QueryKnowledge(keyPrefix: "known/", state: "observed", limit: 10, cursor: first.NextCursor);
        Assert.Equal("known/10", second.Entries[0].Fact.Key);
        Assert.Equal("known/63", Assert.Single(session.QueryKnowledge(key: "known/63").Entries).Fact.Key);
        Assert.Throws<InvalidOperationException>(() => session.QueryKnowledge(keyPrefix: "other/", state: "observed", cursor: first.NextCursor));
        var other = store.Open(Request(Observation("host", 1)) with { PerspectiveId = "other" });
        Assert.Throws<InvalidOperationException>(() => other.QueryKnowledge(keyPrefix: "known/", state: "observed", cursor: first.NextCursor));
        session.Record(Observation("changed", 2, Fact("known/00", "corrected")));
        Assert.Throws<InvalidOperationException>(() => session.QueryKnowledge(keyPrefix: "known/", state: "observed", cursor: first.NextCursor));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.QueryKnowledge(limit: 33));
    }

    [Fact]
    public async Task KnowledgeQueryToolIsBoundedAndAvoidsDuplicatingPayloadInReceipt()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1,
            Enumerable.Range(0, 6).Select(index => Fact($"large/{index}", new string('v', 7_000))).ToArray())));
        var tool = session.CreateTools().Single(registration => registration.Descriptor.ToolId == "lab.knowledge.query").Tool;
        var result = await tool.ExecuteAsync(new("run", "query", "lab.knowledge.query",
            new Dictionary<string, object?> { ["keyPrefix"] = "large/", ["limit"] = 32 }), TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        var payload = (JsonElement)result.Observation!.Data["result"]!;
        Assert.True(payload.GetProperty("entries").GetArrayLength() < 6);
        Assert.NotEqual(JsonValueKind.Null, payload.GetProperty("nextCursor").ValueKind);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(payload, HostProtocol.Json).Length < 33_500);
        Assert.False(result.Receipt.Data.ContainsKey("result"));
        Assert.True(result.Receipt.Data.ContainsKey("resultHash"));
    }

    [Fact]
    public void ProjectionAppliesByteBoundsAsWellAsEntryCounts()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("initial", 0)));
        for (var revision = 1; revision <= 4; revision++)
            session.Record(Observation("source-" + revision, revision,
                Enumerable.Range(0, 4).Select(index => Fact($"{revision}-{index}", new string('v', 7_000)) with
                { Summary = new string('\u4e00', 1_000) }).ToArray()));
        var frame = Assert.Single(session.Project(new("run", 1, new RunRequest("objective"), PlanningExecutionContext.Empty, [], [], [], null)));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(frame.Payload, HostProtocol.Json).Length <= 81_920);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(frame.CompactPayload, HostProtocol.Json).Length <= 16_384);
        Assert.True((int)frame.CompactPayload!["omittedCurrentFactCount"]! > 12);
        Assert.NotEmpty(session.QueryKnowledge(keyPrefix: "1-").Entries);
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("refuted")]
    [InlineData("inconclusive")]
    public async Task ThoughtExerciseBindsPriorExpectationToOriginalResultAcrossCompactionAndLaterRun(string assessment)
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1, Fact("item.status", "pending"))));
        var record = session.CreateTools().Single(tool => tool.Descriptor.ToolId == "lab.hypothesis.record").Tool;
        var planned = await record.ExecuteAsync(ThoughtInvocation("plan"), TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Succeeded, planned.Receipt.Status);
        var thought = Assert.Single(session.Snapshot().ThoughtTests);
        var action = ThoughtAction();
        session.RecordActionDispatch(action);
        session.RecordActionDispatch(action); // exact pre-send replay must not bind a new prediction
        var actualRevision = assessment == "refuted" ? 1 : 2;
        var observation = Observation("actual", actualRevision, Fact("item.status", assessment == "supported" ? "accepted" : "pending")) with
        { Data = HostProtocol.Element(new { status = assessment == "supported" ? "accepted" : "pending", blocked = assessment == "refuted" }) };
        var result = new HostActionResult(action.ActionId, action.SessionId, action.SessionEpoch,
            assessment == "refuted" ? "refused" : "applied", 1, actualRevision, "actual-host-receipt", "Authoritative host result", observation);
        session.RecordActionResult(action, result);
        session.RecordActionResult(action, result);
        var assess = session.CreateTools().Single(tool => tool.Descriptor.ToolId == "lab.hypothesis.assess").Tool;
        var invocation = AssessmentInvocation(thought.HypothesisId, assessment);
        var invented = invocation with { Input = new Dictionary<string, object?>(invocation.Input) { ["hostEvidenceId"] = "invented" } };
        var mismatched = invocation with { Input = new Dictionary<string, object?>(invocation.Input) { ["observationId"] = "host" } };
        Assert.Equal(ReceiptStatus.Refused, (await assess.ExecuteAsync(invented, TestContext.Current.CancellationToken)).Receipt.Status);
        Assert.Equal(ReceiptStatus.Refused, (await assess.ExecuteAsync(mismatched, TestContext.Current.CancellationToken)).Receipt.Status);
        var qualified = await assess.ExecuteAsync(invocation, TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Succeeded, qualified.Receipt.Status);
        Assert.Null(qualified.Artifact); // An assessment does not supply a host completion artifact.
        var snapshot = session.Snapshot();
        var tested = Assert.Single(snapshot.ThoughtTests);
        var retainedAction = Assert.Single(snapshot.ActionEvidence);
        Assert.True(tested.PlannedAt <= retainedAction.IntentRecordedAt);
        Assert.Equal(action.ActionId, tested.ActionId);
        Assert.Equal("The item becomes accepted.", tested.ExpectedResult);
        Assert.Equal("The host refuses and the item remains pending.", tested.Falsifier);
        Assert.Equal(retainedAction.Result!.ResultHash, tested.Assessment!.ResultHash);
        Assert.Equal(ProtocolValidation.Digest(result), tested.Assessment.ResultHash);
        Assert.Equal(ProtocolValidation.Digest(action), retainedAction.RequestHash);
        Assert.Equal("actual-host-receipt", tested.Assessment.HostEvidenceId);
        Assert.Equal(assessment, tested.Assessment.Assessment);
        var model = Assert.Single(snapshot.Facts, fact => fact.Source == "model" && fact.IsCurrent);
        Assert.Equal(assessment == "inconclusive" ? "inferred" : assessment, model.State);
        Assert.Equal(thought.HypothesisId, model.Supersedes);
        Assert.Equal("observed", Assert.Single(snapshot.Facts, fact => fact.Source == "host" && fact.IsCurrent).State);

        var later = new HostContextStore(_root).Open(Request(Observation("later-run-current", 3)) with { ObjectiveId = "later-bounded-run" });
        var frame = Assert.Single(later.Project(new("later-run", 1, new RunRequest("Revisit learned evidence"), PlanningExecutionContext.Empty, [], [], [], null)));
        var compact = JsonSerializer.Serialize(frame.CompactPayload, HostProtocol.Json);
        Assert.Contains("actual-host-receipt", compact, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"model\"", compact, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"host\"", compact, StringComparison.Ordinal);
        var queried = Assert.Single(later.QueryKnowledge(key: "prediction/accept").Entries);
        Assert.Equal(assessment, queried.ThoughtTest!.Assessment!.Assessment);
        var exact = later.ReadEvidence(queried.ThoughtTest.Assessment.ObservationId);
        Assert.Equal("available", exact.Status);
        Assert.Equal(queried.ThoughtTest.Assessment.ObservationHash, exact.Reference!.ContentHash);
        Assert.Equal("later-run-current", later.CurrentObservation.ObservationId);
    }

    [Fact]
    public async Task PostActionPredictionAndUnresolvedResultCannotBeAssessed()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1)));
        var action = ThoughtAction();
        session.RecordActionDispatch(action);
        var tools = session.CreateTools();
        var record = tools.Single(tool => tool.Descriptor.ToolId == "lab.hypothesis.record").Tool;
        await record.ExecuteAsync(ThoughtInvocation("too-late"), TestContext.Current.CancellationToken);
        var late = Assert.Single(session.Snapshot().ThoughtTests);
        session.RecordActionResult(action, new(action.ActionId, action.SessionId, action.SessionEpoch, "applied", 1, 2,
            "actual-host-receipt", "Applied", Observation("actual", 2)));
        var assess = tools.Single(tool => tool.Descriptor.ToolId == "lab.hypothesis.assess").Tool;
        var refused = await assess.ExecuteAsync(AssessmentInvocation(late.HypothesisId, "supported"), TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Refused, refused.Receipt.Status);
        Assert.Null(Assert.Single(session.Snapshot().ThoughtTests).Assessment);

        var next = ThoughtAction() with { ActionId = "next-action", ExpectedRevision = 2 };
        session.RecordActionDispatch(next); // The same prediction can bind only a future action.
        session.RecordActionResult(next, new(next.ActionId, next.SessionId, next.SessionEpoch, "unresolved", 2, 2,
            "unknown-host-receipt", "Result is unknown", Observation("unknown", 2)));
        var unknownAssessment = AssessmentInvocation(late.HypothesisId, "inconclusive") with
        {
            Input = new Dictionary<string, object?>
            {
                ["hypothesisId"] = late.HypothesisId,
                ["actionId"] = "next-action",
                ["hostEvidenceId"] = "unknown-host-receipt",
                ["observationId"] = "unknown",
                ["assessment"] = "inconclusive",
                ["summary"] = "Unknown delivery is not test evidence."
            }
        };
        Assert.Equal(ReceiptStatus.Refused, (await assess.ExecuteAsync(unknownAssessment, TestContext.Current.CancellationToken)).Receipt.Status);
    }

    [Fact]
    public async Task PredictionRequiresFalsifierAndSupportedCannotBypassResultAssessment()
    {
        var session = new HostContextStore(_root).Open(Request(Observation("host", 1)));
        var record = session.CreateTools().Single(tool => tool.Descriptor.ToolId == "lab.hypothesis.record").Tool;
        var incomplete = ThoughtInvocation("missing-falsifier");
        var input = new Dictionary<string, object?>(incomplete.Input);
        input.Remove("falsifier");
        Assert.Equal(ReceiptStatus.Refused, (await record.ExecuteAsync(incomplete with { Input = input }, TestContext.Current.CancellationToken)).Receipt.Status);
        var unsupportedPromotion = Hypothesis("promotion", "prediction/accept", "accepted", "supported");
        Assert.Equal(ReceiptStatus.Refused, (await record.ExecuteAsync(unsupportedPromotion, TestContext.Current.CancellationToken)).Receipt.Status);
        Assert.Empty(session.Snapshot().ThoughtTests);
        Assert.Empty(session.Snapshot().Facts);
    }

    [Fact]
    public void LegacyContextWithoutThoughtExtensionsMigratesWithoutLosingEvidence()
    {
        new HostContextStore(_root).Open(Request(Observation("legacy", 1, Fact("legacy-key", "retained"))));
        var path = Assert.Single(Directory.GetFiles(_root, "*.json"));
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        document["version"] = 1;
        var payload = document["payload"]!.AsObject();
        payload.Remove("actions");
        payload.Remove("thoughtTests");
        document["contentHash"] = ProtocolValidation.Digest(JsonSerializer.SerializeToElement(payload, HostProtocol.Json))[7..];
        File.WriteAllText(path, document.ToJsonString(HostProtocol.Json));

        var migrated = new HostContextStore(_root).Open(Request(Observation("fresh", 2)));
        Assert.Equal("retained", Assert.Single(migrated.Snapshot().Facts).Value.GetString());
        Assert.Equal("available", migrated.ReadEvidence("legacy").Status);
        Assert.Empty(migrated.Snapshot().ThoughtTests);
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, saved.RootElement.GetProperty("version").GetInt32());
    }

    private static ToolInvocation ThoughtInvocation(string step) => Hypothesis(step, "prediction/accept", "acceptance predicted", "inferred") with
    {
        Input = new Dictionary<string, object?>
        {
            ["key"] = "prediction/accept",
            ["summary"] = "An acceptance prediction",
            ["value"] = "acceptance predicted",
            ["evidenceObservationIds"] = new[] { "host" },
            ["expectedResult"] = "The item becomes accepted.",
            ["falsifier"] = "The host refuses and the item remains pending.",
            ["capabilityId"] = "host.accept"
        }
    };

    private static HostActionRequest ThoughtAction() => new("tested-action", "lab-run", "run", "action-step", "session", "epoch", "host.accept",
        "manifest", HostProtocol.Element(new { itemId = "sample" }), 1, DateTimeOffset.UtcNow.AddMinutes(1));

    private static ToolInvocation AssessmentInvocation(string hypothesisId, string assessment) => new("run", "assess", "lab.hypothesis.assess",
        new Dictionary<string, object?>
        {
            ["hypothesisId"] = hypothesisId,
            ["actionId"] = "tested-action",
            ["hostEvidenceId"] = "actual-host-receipt",
            ["observationId"] = "actual",
            ["assessment"] = assessment,
            ["summary"] = "The model compared the expected result and falsifier to the bound host receipt."
        });

    private static HostRunRequest Request(HostObservation observation) => new(
        1, "host", "session", "epoch", "scope", "perspective", "objective-id", "Do bounded work", observation, []);

    private static HostObservation Observation(string id, long revision, params KnowledgeFact[] facts) => new(
        id, revision, DateTimeOffset.UnixEpoch.AddSeconds(revision + 1), HostProtocol.Element(new { description = new string('x', 512), revision }), facts);

    private static KnowledgeFact Fact(string key, string value) => new(key, "An observed property", HostProtocol.Element(value));

    private static ToolInvocation Hypothesis(string stepId, string key, string value, string state) => new(
        "run", stepId, "lab.hypothesis.record", new Dictionary<string, object?>
        {
            ["key"] = key,
            ["summary"] = "A tentative model interpretation",
            ["value"] = value,
            ["state"] = state,
            ["evidenceObservationIds"] = new[] { "host" }
        });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

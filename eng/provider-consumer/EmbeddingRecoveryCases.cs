using Agentica.Memory;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Agentica.ProviderConsumer;

internal sealed class DurableEmbeddingJournal(string path, RetainedEmbeddingAdmission? retainedAdmission = null) : IEmbeddingAttemptJournal
{
    public Dictionary<string, AttemptEvidence> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<Dictionary<string, AttemptEvidence>>(File.ReadAllBytes(path))! : new();
    public void Reserve(AttemptEvidence evidence)
    {
        retainedAdmission?.Validate(evidence);
        var state = Read();
        if (!state.TryAdd(evidence.AttemptId, evidence)) throw new InvalidOperationException("embedding_duplicate_attempt");
        Save(state);
    }
    public void Update(string id, Func<AttemptEvidence, AttemptEvidence> update)
    {
        var state = Read();
        state[id] = update(state[id]);
        Save(state);
    }
    private void Save(Dictionary<string, AttemptEvidence> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(JsonSerializer.SerializeToUtf8Bytes(state));
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed class RetainedEmbeddingAdmission(AttemptEvidence retained,
    Func<string> requiredProfile, Func<IReadOnlyList<SourceReference>> liveSources, Func<bool> eligible)
{
    public void Validate(AttemptEvidence proposed)
    {
        var profile = requiredProfile();
        var sources = liveSources();
        if (!eligible() || retained.ProfileId != profile || proposed.ProfileId != profile ||
            !retained.Sources.SequenceEqual(sources) || !proposed.Sources.SequenceEqual(sources) ||
            retained.ScopeId != proposed.ScopeId || retained.Purpose != proposed.Purpose)
            throw new InvalidOperationException("retained_embedding_profile_or_source_ineligible");
    }
}

internal static class EmbeddingRecoveryCases
{
    public static async Task Run()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "host-proof", Guid.NewGuid().ToString("N"), "embeddings.json");
        var journal = new DurableEmbeddingJournal(path);
        var bundle = EpisodeState.DefaultBundle("embedding-bundle");
        var inputs = new SourceInput[] { new("user-message", "revision-1", "synthetic user source"), new("agent-message", "revision-7", "synthetic agent source") };
        using var physical = new EmbeddingTransport();
        using (var memory = new EmbeddingAttempt("memory-encode", "memory.encode", "alpha", bundle.Memory,
            MemoryEmbeddingPurpose.Document, inputs, journal, physical))
        {
            await memory.ExecuteAsync();
            Check.Equal(1, physical.Calls);
        }
        var reopened = new DurableEmbeddingJournal(path);
        var original = reopened.Read()["memory-encode"];
        Check.Equal(AcceptanceState.Accepted, original.Acceptance);
        Check.Equal("revision-7", original.Sources[1].Revision);
        Check.Equal(bundle.Memory.ProfileId, original.ProfileId);
        using var duplicateWire = new EmbeddingTransport();
        using (var duplicate = new EmbeddingAttempt("memory-encode", "memory.encode", "alpha", bundle.Memory,
            MemoryEmbeddingPurpose.Document, inputs, reopened, duplicateWire))
            await Check.Throws<InvalidOperationException>(() => duplicate.ExecuteAsync());
        Check.Equal(0, duplicateWire.Calls);
        Check.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(reopened.Read()["memory-encode"]));
        using var retainedWire = new EmbeddingTransport();
        using (var retained = new EmbeddingAttempt("retained-memory", "memory.encode", "alpha", bundle.Memory,
            MemoryEmbeddingPurpose.Document, inputs, reopened, retainedWire))
        {
            await Check.Throws<ArgumentException>(() => retained.ExecuteAsync(bundle.Conversation,
                MemoryEmbeddingPurpose.Document, inputs.Select(input => input.Text).ToArray()));
            await Check.Throws<ArgumentException>(() => retained.ExecuteAsync(bundle.Memory,
                MemoryEmbeddingPurpose.Query, inputs.Select(input => input.Text).ToArray()));
        }
        Check.Equal(0, retainedWire.Calls);
        var changedMemory = new MemoryEmbeddingProfile("openai", "text-embedding-3-small", "memory.configuration.v2", 3,
            "memory.document.v2", "memory.query.v2");
        var currentProfile = bundle.Memory.ProfileId;
        IReadOnlyList<SourceReference> currentSources = original.Sources;
        var currentEligibility = true;
        var admission = new RetainedEmbeddingAdmission(original, () => currentProfile, () => currentSources, () => currentEligibility);
        var guardedJournal = new DurableEmbeddingJournal(path, admission);
        foreach (var reason in new[] { "changed-profile", "stale-revision", "ineligible" })
        {
            currentProfile = bundle.Memory.ProfileId;
            currentSources = original.Sources;
            currentEligibility = true;
            using var deniedWire = new EmbeddingTransport();
            using var denied = new EmbeddingAttempt("denied-" + reason, "memory.encode", "alpha", bundle.Memory,
                MemoryEmbeddingPurpose.Document, inputs, guardedJournal, deniedWire);
            // Mutate live policy after capturing the request. Required reservation rechecks it
            // at the real EmbeddingAttempt transport boundary, before any physical POST.
            if (reason == "changed-profile") currentProfile = changedMemory.ProfileId;
            if (reason == "stale-revision") currentSources = [new("user-message", "revision-2"), new("agent-message", "revision-7")];
            if (reason == "ineligible") currentEligibility = false;
            await Check.Throws<InvalidOperationException>(() => denied.ExecuteAsync());
            Check.Equal(0, deniedWire.Calls);
            Check.That(!guardedJournal.Read().ContainsKey("denied-" + reason), "rejected retained source receives no reservation");
            Check.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(guardedJournal.Read()["memory-encode"]));
        }
        currentProfile = bundle.Memory.ProfileId;
        currentSources = original.Sources;
        currentEligibility = true;
        using var eligibleWire = new EmbeddingTransport();
        using (var eligibleAttempt = new EmbeddingAttempt("retained-source-admitted", "memory.encode", "alpha", bundle.Memory,
            MemoryEmbeddingPurpose.Document, inputs, guardedJournal, eligibleWire))
        {
            await eligibleAttempt.ExecuteAsync();
            Check.Equal(1, eligibleWire.Calls);
        }
        using var queryWire = new EmbeddingTransport(singleInput: true);
        using (var query = new EmbeddingAttempt("conversation-query", "conversation.query", "alpha", bundle.Conversation,
            MemoryEmbeddingPurpose.Query, [new("query", "query-revision-1", "synthetic query")], reopened, queryWire))
        {
            var result = await query.ExecuteAsync();
            Check.Equal(bundle.Conversation.ProfileId, result.ProfileId);
            Check.Equal(1, queryWire.Calls);
        }
        Check.Equal(3, reopened.Read().Count);
        Check.Equal(MemoryEmbeddingPurpose.Query, reopened.Read()["conversation-query"].Purpose);
        Check.Equal(1L, reopened.Read()["memory-encode"].InputTokens);
    }

    private sealed class EmbeddingTransport(bool singleInput = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var data = singleInput
                ? new[] { new { @object = "embedding", index = 0, embedding = new[] { 1f, 0f, 0f } } }
                : [new { @object = "embedding", index = 1, embedding = new[] { 0f, 1f, 0f } }, new { @object = "embedding", index = 0, embedding = new[] { 1f, 0f, 0f } }];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { @object = "list", model = "text-embedding-3-small", data, usage = new { prompt_tokens = 1 } }), Encoding.UTF8, "application/json") };
            response.Headers.Add("x-request-id", "durable-embedding-request");
            return Task.FromResult(response);
        }
    }
}

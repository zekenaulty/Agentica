using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.ProviderConsumer;

internal sealed record CapturedRoute(string Operation, string Phase, string Provider, string Model, string Fingerprint);
internal sealed record CapturedBundle(string Id, CapturedRoute Foreground, CapturedRoute Evaluation,
    CapturedRoute Document, MemoryEmbeddingProfile Memory, MemoryEmbeddingProfile Conversation);
internal enum SemanticAuthor { User, Assistant, Host }
internal sealed record HostSource(string Id, string Revision, SemanticAuthor Author, string Text);
internal sealed record HostEvidence(string Id, string Revision, SemanticAuthor Author,
    LlmMessageRole ProviderRole, string Content, string SourceAttemptId);
internal sealed record HostAttempt(LlmPreparedRequestDescriptor Descriptor, decimal Reserved,
    LlmAttemptEvidence? Evidence = null, string Acceptance = "pending", string? Failure = null,
    decimal? ObservedFixtureCost = null);
internal sealed record ReplacementDecision(string OriginalId, string ReplacementId, string Reason);
internal sealed record HostToolReceipt(string Id, string ItemId, string Scope, int Before, int After);
internal sealed record HostPolicy(bool Authorized = true, bool Eligible = true, bool Revoked = false);
internal sealed class EpisodeState
{
    public string Objective { get; set; } = "Accept both eligible alpha items, once each.";
    public string Scope { get; set; } = "alpha";
    public string EpisodeId { get; set; } = Guid.NewGuid().ToString("N");
    public CapturedBundle Bundle { get; set; } = DefaultBundle("bundle-v1");
    public CapturedBundle CurrentSettings { get; set; } = DefaultBundle("bundle-v1");
    public HostPolicy Policy { get; set; } = new();
    public Dictionary<string, string> Items { get; set; } = new() { ["item-a"] = "pending", ["item-b"] = "pending" };
    public List<string> Obligations { get; set; } = ["item-a", "item-b"];
    public List<HostSource> Sources { get; set; } =
        [new("participant-message", "revision-1", SemanticAuthor.User, "Synthetic participant asks to accept eligible items."),
         new("agent-message", "revision-1", SemanticAuthor.Assistant, "Synthetic prior agent claim; host state verifies completion.")];
    public List<HostEvidence> AcceptedTranscript { get; set; } = [];
    public Dictionary<string, HostAttempt> Attempts { get; set; } = new();
    public List<ReplacementDecision> Replacements { get; set; } = [];
    public List<HostToolReceipt> Tools { get; set; } = [];
    public List<string> Transitions { get; set; } = [];
    public Dictionary<string, int> PhysicalSends { get; set; } = new();
    public int Version { get; set; }
    public int MaximumAttempts { get; set; } = 8;
    public decimal MaximumReservation { get; set; } = 0.08m;
    public bool Completed { get; set; }
    public bool FailJournal { get; set; }
    public int? ReopenedProcessId { get; set; }
    public string? NativeEnvelope { get; set; }
    public string? NativeStatus { get; set; }
    public string? FinalEvidenceId { get; set; }
    public int StreamActivities { get; set; }
    public int StreamDeltas { get; set; }
    public int NativeReplaySends { get; set; }
    public int ReconstructedEvidenceSends { get; set; }

    public static CapturedBundle DefaultBundle(string id) => new(id,
        new("foreground.plan", "decide", "openai", "fixture-gpt", "route:foreground:v1"),
        new("memory.evaluate", "evaluate", "openai", "fixture-evaluation", "route:evaluation:v1"),
        new("document.answer", "read", "openai", "fixture-document", "route:document:v1"),
        new("openai", "text-embedding-3-small", "memory.configuration.v1", 3, "memory.document.v1", "memory.query.v1"),
        new("openai", "text-embedding-3-small", "conversation.configuration.v1", 3, "conversation.document.v1", "conversation.query.v1"));
}

// Single-owner synthetic durable host. Atomic replacement and Flush(true) exercise fresh-process
// reopen, not a production concurrent database or a new package persistence abstraction.
internal sealed class EpisodeStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public EpisodeStore(string path) => Path = System.IO.Path.GetFullPath(path);
    public string Path { get; }
    public EpisodeState Read() => JsonSerializer.Deserialize<EpisodeState>(File.ReadAllBytes(Path), Json)!;
    public void Save(EpisodeState state)
    {
        if (state.FailJournal) throw new IOException("fixture_journal_write_failed");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".pending";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path, overwrite: true);
    }

    public void ProtectNative(EpisodeState state, byte[] bytes)
    {
        var keyPath = Path + ".key";
        if (!File.Exists(keyPath)) File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        var key = File.ReadAllBytes(keyPath);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[16];
        try
        {
            using var cipher = new AesGcm(key, tag.Length);
            cipher.Encrypt(nonce, bytes, ciphertext, tag, Associated(state));
            state.NativeEnvelope = Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(bytes); }
    }

    public byte[] UnprotectNative(EpisodeState state)
    {
        var bytes = Convert.FromBase64String(state.NativeEnvelope!);
        var key = File.ReadAllBytes(Path + ".key");
        var plaintext = new byte[bytes.Length - 28];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Associated(state));
            return plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static byte[] Associated(EpisodeState state) => Encoding.UTF8.GetBytes(
        state.EpisodeId + "|" + state.Scope + "|" + state.Bundle.Id + "|native.v1");
}

internal sealed class DurableAdmission(EpisodeStore store, EpisodeState state, CapturedRoute route,
    string? replacementOf = null) : ILlmCallAdmission
{
    public bool Reserved { get; private set; }
    public ValueTask AdmitAsync(LlmPreparedRequestDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state.Completed) throw new InvalidOperationException("objective_already_accepted");
        if (!state.Policy.Authorized || !state.Policy.Eligible || state.Policy.Revoked)
            throw new InvalidOperationException("live_policy_denied");
        var refs = descriptor.References;
        if (descriptor.HostScopeId != state.Scope || refs.ExecutionBundleReference != state.Bundle.Id ||
            refs.RouteFingerprint != route.Fingerprint || refs.OperationId != route.Operation || refs.PhaseId != route.Phase ||
            descriptor.ProviderName != route.Provider || descriptor.ModelId != route.Model)
            throw new InvalidOperationException("captured_scope_route_bundle_mismatch");
        if (route.Provider != "openai") throw new InvalidOperationException("transition_permits_zero_google_model_calls");
        if (state.Attempts.ContainsKey(refs.AttemptId)) throw new InvalidOperationException("duplicate_attempt");
        var unresolved = state.Attempts.Where(pair => NeedsReconciliation(state, pair.Key)).Select(pair => pair.Key).ToArray();
        if (unresolved.Length > 0 && (replacementOf is null || !unresolved.Contains(replacementOf, StringComparer.Ordinal)) ||
            unresolved.Any(id => !state.Replacements.Any(decision => decision.OriginalId == id && decision.ReplacementId == refs.AttemptId)) ||
            replacementOf is not null && !state.Replacements.Any(decision => decision.OriginalId == replacementOf && decision.ReplacementId == refs.AttemptId))
            throw new InvalidOperationException("original_attempt_requires_explicit_reconciliation");
        if (state.Attempts.Count >= state.MaximumAttempts || state.Attempts.Values.Sum(attempt => attempt.Reserved) + 0.01m > state.MaximumReservation)
            throw new InvalidOperationException("cumulative_budget_exhausted");
        // Commit before returning admission. An I/O exception prevents the package's physical send.
        state.Attempts.Add(refs.AttemptId, new(descriptor, 0.01m));
        try { store.Save(state); }
        catch { state.Attempts.Remove(refs.AttemptId); throw; }
        Reserved = true;
        return ValueTask.CompletedTask;
    }

    // Every paid, unaccepted outcome needs host reconciliation, including known failure,
    // rejected host parsing and a delivered response lost before acceptance. A returned flag
    // alone cannot restore its proposal/native bytes or authorize another paid inference.
    public static bool NeedsReconciliation(EpisodeState state, string attemptId) =>
        state.Attempts[attemptId].Acceptance is not ("accepted" or "host-validated-fixture") &&
        !state.Replacements.Any(decision => decision.OriginalId == attemptId &&
            state.Attempts.TryGetValue(decision.ReplacementId, out var successor) && successor.Acceptance == "accepted");
}

internal sealed class RecoveryHost(EpisodeStore store, EpisodeState state)
{
    public const string Instructions = "Synthetic host policy: propose only bound accept-item actions; completion is checked by the host.";
    public static OpenAiResponsesClientOptions Options => new("synthetic-offline-fixture-key", Endpoint: new("https://fixture.invalid/v1/responses"));
    public EpisodeState State => state;

    public LlmRequest Request(CapturedRoute route, LlmNativeContinuation? native = null, string? schema = null)
    {
        var context = JsonSerializer.Serialize(new
        {
            objective = state.Objective, scope = state.Scope, version = state.Version,
            bound = "accept-item", pending = state.Items.Where(item => item.Value == "pending").Select(item => item.Key).ToArray(),
            accepted = state.Items.Where(item => item.Value == "accepted").Select(item => item.Key).ToArray(),
            source_references = state.Sources.Select(source => new { source.Id, source.Revision, author = source.Author.ToString() })
        });
        var messages = new List<LlmMessage> { new(LlmMessageRole.System, Instructions) };
        if (native is null)
        {
            // This host projection is evidence supplied to the model. Its original semantic
            // authors remain data; the provider input envelope itself is not a participant quote.
            // OpenAI's ordinary message seam intentionally rejects synthetic assistant replay.
            messages.Add(new(LlmMessageRole.User, JsonSerializer.Serialize(new
            {
                kind = "host-accepted-evidence-projection", sourceCut = BridgeContractFixtures.SourceCut,
                sources = state.Sources.Select(source => new { source.Id, source.Revision, author = source.Author.ToString(), source.Text }),
                accepted = state.AcceptedTranscript.Select(message => new
                { message.Id, message.Revision, author = message.Author.ToString(), provider_role = message.ProviderRole.ToString(), message.Content, message.SourceAttemptId })
            })));
        }
        messages.Add(new(LlmMessageRole.User, context));
        return new(route.Model, messages,
            new(MaxOutputTokens: 256, Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, includeThoughts: true)),
            new(JsonSchema: schema ?? BridgeContractFixtures.Foreground), NativeContinuation: native);
    }

    public async Task<LlmResponse> Call(string attemptId, FixtureBehavior behavior = FixtureBehavior.ModelDecision,
        CapturedRoute? selectedRoute = null, LlmNativeContinuation? native = null, string? replacementOf = null,
        CancellationTokenSource? caller = null, LlmRequest? suppliedRequest = null, LlmAttemptReferences? suppliedReferences = null)
    {
        var route = selectedRoute ?? state.Bundle.Foreground;
        var request = suppliedRequest ?? Request(route, native);
        var report = LlmConfigurationReports.Describe(request, Options,
            new(HostRouteReference: route.Fingerprint, HostExecutionBundleReference: state.Bundle.Id));
        Check.That(report.AdapterAcceptsRequest, "exact requested controls accepted:" + string.Join(",", report.Issues.Select(issue => issue.Code)));
        using var wire = new RecoveryTransport(store, state, attemptId, behavior, caller, request);
        using var http = new HttpClient(wire);
        using var prepared = LlmCallPreparation.Prepare(request, Options,
            suppliedReferences ?? new(attemptId, route.Operation, route.Phase, route.Fingerprint, state.Bundle.Id), state.Scope, http);
        var admission = new DurableAdmission(store, state, route, replacementOf);
        LlmResponse? response = null;
        try
        {
            await foreach (var item in prepared.StreamAsync(admission, caller?.Token ?? default))
            {
                if (item.Kind == LlmStreamEventKind.Activity) state.StreamActivities++;
                if (item.Kind == LlmStreamEventKind.TextDelta) state.StreamDeltas++;
                if (item.Kind == LlmStreamEventKind.Completed) response = item.Response;
            }
            Check.That(response is not null, "terminal response required");
            return response!;
        }
        finally
        {
            // Only the successful reserver owns this record: failed duplicate admission cannot
            // replace the original attempt's evidence, known usage or acceptance.
            if (admission.Reserved)
            {
                var evidence = prepared.Capture.Latest;
                var unknown = evidence?.DispatchCertainty == LlmDispatchCertainty.MayHaveDispatched;
                state.Attempts[attemptId] = state.Attempts[attemptId] with
                {
                    Evidence = evidence, Acceptance = response is not null ? "returned" : unknown ? "unknown" : "failed",
                    Failure = evidence?.ReasonCode, ObservedFixtureCost = FixtureCost(evidence?.Usage)
                };
                store.Save(state);
            }
        }
    }

    public void Accept(string attemptId, LlmResponse response)
    {
        if (!state.Attempts.TryGetValue(attemptId, out var attempt) ||
            response.AttemptEvidence?.References.AttemptId != attemptId)
            throw new InvalidDataException("response_attempt_mismatch");
        if (attempt.Acceptance == "accepted") return; // Repeated delivery is idempotent.
        if (attempt.Acceptance != "returned") throw new InvalidDataException("response_not_available_for_acceptance");
        BridgeContractFixtures.Validate(response.Text, BridgeContractFixtures.Foreground);
        using var parsed = JsonDocument.Parse(response.Text);
        var value = parsed.RootElement;
        if (value.GetProperty("decision").GetString() == "reply")
        {
            if (state.Obligations.Count != 0 || state.Items.Values.Any(item => item != "accepted") ||
                state.Tools.Count != 2 || state.Tools.Select(receipt => receipt.ItemId).Distinct().Count() != 2)
                throw new InvalidDataException("host_objective_not_fulfilled");
            state.Completed = true;
            state.FinalEvidenceId = attemptId;
        }
        else
        {
            var action = value.GetProperty("action");
            var input = action.GetProperty("input");
            var id = input.GetProperty("id").GetString()!;
            if (action.GetProperty("capability_ref").GetString() != "accept-item" || input.GetProperty("scope").GetString() != state.Scope ||
                input.GetProperty("version").GetInt32() != state.Version || !state.Policy.Authorized || !state.Policy.Eligible || state.Policy.Revoked ||
                !state.Items.TryGetValue(id, out var status) || status != "pending")
                throw new InvalidDataException("bound_tool_scope_version_policy");
            var before = state.Version;
            state.Items[id] = "accepted";
            state.Obligations.Remove(id);
            state.Tools.Add(new("effect-" + id, id, state.Scope, before, ++state.Version));
        }
        state.AcceptedTranscript.Add(new("proposal-" + attemptId, "delivered-v1", SemanticAuthor.Assistant,
            LlmMessageRole.Assistant, response.Text, attemptId));
        state.AcceptedTranscript.Add(new("host-observation-" + attemptId, "host-state-v" + state.Version,
            SemanticAuthor.Host, LlmMessageRole.User,
            JsonSerializer.Serialize(new { host_result = "verified", version = state.Version, remaining = state.Obligations.Count }), attemptId));
        state.Attempts[attemptId] = state.Attempts[attemptId] with { Acceptance = "accepted" };
        if (response.NativeContinuation is not null)
        {
            using var binding = LlmNativeContinuationBinding.FromRequest(LlmConfigurationReports.Describe(Request(state.Bundle.Foreground), Options), Request(state.Bundle.Foreground));
            var exported = LlmNativeContinuationCodec.ExportSensitive(response.NativeContinuation, binding);
            using var payload = exported.SensitivePayload;
            Check.That(exported.Succeeded, "export actual provider native capsule");
            store.ProtectNative(state, payload!.CopySensitiveBytes());
        }
        store.Save(state);
    }

    public LlmNativeContinuation? ReopenNative(string mode)
    {
        var request = Request(state.Bundle.Foreground);
        var report = LlmConfigurationReports.Describe(request, Options);
        using var binding = mode == "incompatible"
            ? new LlmNativeContinuationBinding(report.ProviderName, report.ApiMode, "different-model", report.ConfigurationIdentity, Instructions)
            : LlmNativeContinuationBinding.FromRequest(report, request);
        var bytes = mode == "unavailable" ? [] : mode == "invalid" ? Encoding.UTF8.GetBytes("{invalid") : store.UnprotectNative(state);
        try
        {
            var imported = LlmNativeContinuationCodec.ImportSensitive(bytes, binding);
            state.NativeStatus = imported.Succeeded ? "matching-native" : "accepted-evidence-reconstruction:" + imported.Rejection;
            if (!imported.Succeeded) state.Transitions.Add(state.NativeStatus);
            store.Save(state);
            return imported.Continuation;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Synthetic rates only: decimal exactness and no double counting, not current provider pricing.
    public static decimal? FixtureCost(LlmUsage? usage) => usage?.PromptTokens is >= 0 && usage.GeneratedOutputTokens is >= 0
        ? usage.PromptTokens.Value * 0.000001m + usage.GeneratedOutputTokens.Value * 0.000002m : null;
}

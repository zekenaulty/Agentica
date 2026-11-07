using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agentica.ProviderConsumer;

internal static class RecoveryCases
{
    private static readonly string RunRoot = Path.Combine(AppContext.BaseDirectory, "host-proof", Guid.NewGuid().ToString("N"));
    public static async Task RunAll()
    {
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("fresh_process_matching_native_complete_loop", () => CompleteLoop("matching")),
            ("fresh_process_unavailable_native_reconstructs_accepted_evidence", () => CompleteLoop("unavailable")),
            ("fresh_process_invalid_native_reconstructs_accepted_evidence", () => CompleteLoop("invalid")),
            ("fresh_process_incompatible_native_reconstructs_accepted_evidence", () => CompleteLoop("incompatible")),
            ("journal_failure_prevents_post", JournalFailure),
            ("lost_response_reopen_requires_explicit_replacement_and_preserves_budget", LostResponse),
            ("known_terminal_failure_retains_usage_without_retry", TerminalFailure),
            ("cancel_raced_with_success_retains_provider_usage", CancellationRace),
            ("host_schema_failure_after_usage_does_not_apply_effect", InvalidHostSchema),
            ("missing_usage_remains_unknown", MissingUsage),
            ("captured_route_and_independent_embedding_profiles_survive_settings_edit", RouteProfiles),
            ("retained_route_live_authority_eligibility_revocation", LivePolicy),
            ("wrong_scope_bundle_operation_route_and_duplicate_attempt", WrongBinding),
            ("bridge_shaped_strict_schema_and_semantic_authors", BridgeShapes),
            ("shared_search_sources_citations_and_private_replay", Search),
            ("authenticated_native_envelope_rejects_scope_and_ciphertext_changes", NativeAuthentication),
            ("host_completion_and_tool_validation_reject_model_claims", HostAcceptance),
            ("durable_embedding_profile_source_revision_and_reopen", EmbeddingRecoveryCases.Run),
            ("retained_google_unknown_uses_explicit_openai_successor_only", ProviderTransition),
            ("fresh_process_returned_before_accept_parks_until_explicit_successor", ReturnedBeforeAccept)
        };
        foreach (var item in cases)
        {
            await item.Run();
            Console.WriteLine(JsonSerializer.Serialize(new { scenario = item.Name, result = "PASS" }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            boundary = "P08 offline packed provider-only durable host", result = "PASS", scenarios = cases.Length,
            bridgeSourceCut = BridgeContractFixtures.SourceCut, physicalTransport = "terminal counted offline handler; no retry/redirect layer",
            paidCalls = 0, installedBridge = "NOT-RUN", modelQuality = "NOT-RUN", concurrentDatabase = "NOT-RUN"
        }));
    }

    public static async Task ResumeProcess(string path, string nativeMode)
    {
        var store = new EpisodeStore(path);
        var state = store.Read();
        state.ReopenedProcessId = Environment.ProcessId;
        if (state.Completed) { store.Save(state); Console.WriteLine("host-already-accepted"); return; }
        if (state.Attempts.Keys.Any(id => DurableAdmission.NeedsReconciliation(state, id)))
        {
            store.Save(state);
            Console.WriteLine("host-unaccepted-paid-outcome-preserved");
            return;
        }
        var host = new RecoveryHost(store, state);
        using var reopened = host.ReopenNative(nativeMode);
        var native = reopened;
        for (var iteration = 0; iteration < 4 && !state.Completed; iteration++)
        {
            var id = "reopened-" + state.Attempts.Count;
            var response = await host.Call(id, native: native);
            try
            {
                host.Accept(id, response);
                if (!ReferenceEquals(native, reopened)) native?.Dispose();
                native = response.NativeContinuation;
            }
            catch { response.NativeContinuation?.Dispose(); throw; }
        }
        if (!ReferenceEquals(native, reopened)) native?.Dispose();
        Check.That(state.Completed, "host full loop completion");
        Console.WriteLine("host-objective-accepted");
    }

    public static async Task ReturnBeforeAcceptProcess(string path)
    {
        var store = new EpisodeStore(path);
        var state = store.Read();
        state.ReopenedProcessId = Environment.ProcessId;
        var host = new RecoveryHost(store, state);
        using var native = host.ReopenNative("matching");
        var response = await host.Call("delivered-before-crash", native: native);
        response.NativeContinuation?.Dispose();
        // Exit this process with a durable delivered-but-unaccepted outcome. Its proposal
        // and capsule are intentionally unavailable after this simulated crash window.
        Console.WriteLine("host-returned-without-accepting");
    }

    private static async Task CompleteLoop(string mode)
    {
        var (store, state, host) = New(mode);
        var first = await host.Call("original-0");
        using (first.NativeContinuation)
        {
            host.Accept("original-0", first);
            host.Accept("original-0", first);
        }
        Check.Equal(1, state.Version);
        Check.Equal(1, state.Obligations.Count);
        // User settings edit is not a grant to silently reroute already admitted nested work.
        state.CurrentSettings = ChangedSettings(state);
        state.Transitions.Add("new-settings-published; captured-work-retains-bundle-v1");
        store.Save(state);
        var child = await Child(store.Path, mode);
        Check.That(child.Contains("host-objective-accepted", StringComparison.Ordinal), "fresh process completed");
        var reopened = store.Read();
        Check.That(reopened.ReopenedProcessId != Environment.ProcessId, "actual separate OS process");
        Check.That(reopened.Completed && reopened.FinalEvidenceId is not null, "authoritative accepted completion");
        Check.Equal(3, reopened.Attempts.Count);
        Check.Equal(3, reopened.PhysicalSends.Values.Sum());
        Check.Equal(2, reopened.Tools.Count);
        Check.Equal(0, reopened.Obligations.Count);
        Check.Equal(0.03m, reopened.Attempts.Values.Sum(attempt => attempt.Reserved));
        Check.That(reopened.Attempts.Values.All(attempt => attempt.Descriptor.References.ExecutionBundleReference == "bundle-v1" &&
            attempt.Descriptor.ModelId == "fixture-gpt" && attempt.Descriptor.References.RouteFingerprint == "route:foreground:v1"), "captured route on reopened calls");
        Check.Equal(mode == "matching" ? "matching-native" : "accepted-evidence-reconstruction:" + (mode switch
        { "unavailable" => "Unavailable", "invalid" => "MalformedPayload", _ => "ModelMismatch" }), reopened.NativeStatus);
        Check.That(reopened.StreamActivities >= 3 && reopened.StreamDeltas == 3, "actual streaming iterations");
        Check.Equal(mode == "matching" ? 2 : 1, reopened.NativeReplaySends);
        Check.Equal(mode == "matching" ? 0 : 1, reopened.ReconstructedEvidenceSends);
        var hostObservations = reopened.AcceptedTranscript.Where(item => item.Id.StartsWith("host-observation-", StringComparison.Ordinal)).ToArray();
        Check.Equal(3, hostObservations.Length);
        Check.That(hostObservations.All(item => item.Author == SemanticAuthor.Host && item.ProviderRole == LlmMessageRole.User &&
                item.Revision.StartsWith("host-state-v", StringComparison.Ordinal) && item.SourceAttemptId.Length > 0),
            "durable Host authorship independent from provider role");
        foreach (var attempt in reopened.Attempts.Values)
        {
            Check.Equal(LlmOutputTokenSemantics.IncludesThinking, attempt.Evidence!.Usage!.OutputTokenSemantics);
            Check.Equal(4, attempt.Evidence.Usage.NonThinkingOutputTokens);
            Check.Equal(7, attempt.Evidence.Usage.GeneratedOutputTokens);
            Check.Equal(0.000025m, attempt.ObservedFixtureCost);
        }
        var before = File.ReadAllBytes(store.Path);
        await Child(store.Path, mode); // Repeated delivery/reopen never schedules another paid attempt/effect.
        var repeated = store.Read();
        Check.Equal(3, repeated.PhysicalSends.Values.Sum());
        Check.Equal(2, repeated.Tools.Count);
        Check.Equal(reopened.FinalEvidenceId, repeated.FinalEvidenceId);
        Check.That(!JsonSerializer.Serialize(repeated).Contains("synthetic-private-native-", StringComparison.Ordinal), "private native bytes absent from journal");
        Check.That(before.Length > 0, "persisted host objective evidence budget and obligations");
    }

    private static async Task JournalFailure()
    {
        var (store, state, host) = New("journal-failure");
        state.FailJournal = true;
        await Check.Throws<LlmClientException>(() => host.Call("blocked"));
        Check.Equal(0, state.PhysicalSends.Count);
        Check.Equal(0, store.Read().Attempts.Count);
    }

    private static async Task LostResponse()
    {
        var (store, state, host) = New("lost-response");
        await Check.Throws<LlmClientException>(() => host.Call("lost", FixtureBehavior.LostResponse));
        var original = store.Read().Attempts["lost"];
        Check.Equal("unknown", original.Acceptance);
        Check.Equal(LlmDispatchCertainty.MayHaveDispatched, original.Evidence!.DispatchCertainty);
        Check.Equal<decimal?>(null, original.ObservedFixtureCost);
        await Child(store.Path, "matching");
        var reopened = store.Read();
        Check.Equal(original, reopened.Attempts["lost"]);
        Check.Equal(1, reopened.PhysicalSends.Values.Sum());
        var resumed = new RecoveryHost(store, reopened);
        await Check.Throws<LlmClientException>(() => resumed.Call("automatic-retry"));
        Check.Equal(1, reopened.Attempts.Count);
        reopened.Replacements.Add(new("lost", "replacement", "host acknowledges original unknown; accepts bounded duplicate inference risk"));
        reopened.Transitions.Add("openai-successor-for-original-unknown; original reservation retained");
        store.Save(reopened);
        var replacement = await resumed.Call("replacement", replacementOf: "lost");
        using (replacement.NativeContinuation) resumed.Accept("replacement", replacement);
        Check.Equal(original, reopened.Attempts["lost"]);
        Check.Equal(0.02m, reopened.Attempts.Values.Sum(attempt => attempt.Reserved));
        Check.That(reopened.Attempts["replacement"].Descriptor.References.AttemptId != original.Descriptor.References.AttemptId, "replacement identity distinct");
        Check.Equal(2, reopened.PhysicalSends.Values.Sum());
        reopened.MaximumAttempts = 2;
        reopened.Replacements.Add(new("lost", "over-cumulative-cap", "explicit bounded replacement requested but cumulative cap denies"));
        store.Save(reopened);
        await Check.Throws<LlmClientException>(() => resumed.Call("over-cumulative-cap", replacementOf: "lost"));
        Check.Equal(2, reopened.PhysicalSends.Values.Sum());
        reopened.MaximumAttempts = 8;
        store.Save(reopened);
        // Accepted successor evidence permits continuing the objective, while original
        // uncertainty/reservation remain present. The retry owner is still this host.
        for (var index = 0; index < 2; index++)
        {
            var id = "recovered-continue-" + index;
            var response = await resumed.Call(id);
            using (response.NativeContinuation) resumed.Accept(id, response);
        }
        Check.That(reopened.Completed, "unknown replacement continues through accepted objective");
        Check.Equal(0.04m, reopened.Attempts.Values.Sum(attempt => attempt.Reserved));
        Check.Equal(original, reopened.Attempts["lost"]);
    }

    private static async Task TerminalFailure()
    {
        var (store, state, host) = New("terminal-failure");
        await Check.Throws<LlmClientException>(() => host.Call("refused", FixtureBehavior.Refusal));
        var evidence = store.Read().Attempts["refused"].Evidence!;
        Check.Equal("completed", evidence.ProviderStatus);
        Check.Equal(11, evidence.Usage!.PromptTokens);
        Check.That(evidence.Outcome != LlmAttemptOutcome.Succeeded, "refusal not accepted delivery");
        Check.Equal(1, state.PhysicalSends.Values.Sum());
        Check.Equal(0, state.Tools.Count);
        Check.That((await Child(store.Path, "matching")).Contains("host-unaccepted-paid-outcome-preserved", StringComparison.Ordinal), "refusal reopen parks");
        await Check.Throws<LlmClientException>(() => host.Call("implicit-refusal-retry"));
        state.Replacements.Add(new("refused", "http-failed", "host explicitly authorizes a distinct bounded successor after retained refusal"));
        store.Save(state);
        await Check.Throws<LlmClientException>(() => host.Call("http-failed", FixtureBehavior.HttpFailure, replacementOf: "refused"));
        Check.Equal(429, state.Attempts["http-failed"].Evidence!.HttpStatusCode);
        Check.Equal<LlmUsage?>(null, state.Attempts["http-failed"].Evidence!.Usage); // Error body was not parsed as usage.
        Check.Equal(2, state.PhysicalSends.Values.Sum());
        Check.That((await Child(store.Path, "matching")).Contains("host-unaccepted-paid-outcome-preserved", StringComparison.Ordinal), "known HTTP failure reopen parks");
        // Reconcile each retained failed outcome explicitly; one successor may account for
        // both without deleting their independent reservations or observed evidence.
        state.Replacements.Add(new("refused", "after-known-failures", "host retains refusal and authorizes a new bounded successor"));
        state.Replacements.Add(new("http-failed", "after-known-failures", "host retains HTTP failure and authorizes the same new successor"));
        store.Save(state);
        var recovered = await host.Call("after-known-failures", replacementOf: "http-failed");
        using (recovered.NativeContinuation) host.Accept("after-known-failures", recovered);
        Check.Equal(3, state.Attempts.Count);
        Check.Equal(0.03m, state.Attempts.Values.Sum(attempt => attempt.Reserved));
        Check.Equal("failed", state.Attempts["refused"].Acceptance);
        Check.Equal("failed", state.Attempts["http-failed"].Acceptance);
    }

    private static async Task CancellationRace()
    {
        var (store, state, host) = New("cancellation-race");
        using var caller = new CancellationTokenSource();
        await Check.Throws<OperationCanceledException>(() => host.Call("cancelled", FixtureBehavior.CancelAfterSuccess, caller: caller));
        var attempt = store.Read().Attempts["cancelled"];
        Check.Equal("completed", attempt.Evidence!.ProviderStatus);
        Check.Equal(LlmAttemptOutcome.Cancelled, attempt.Evidence.Outcome);
        Check.Equal(11, attempt.Evidence.Usage!.PromptTokens);
        Check.Equal(0.000025m, attempt.ObservedFixtureCost);
        Check.Equal(1, state.PhysicalSends.Values.Sum());
        Check.Equal(0, state.Tools.Count);
        Check.That((await Child(store.Path, "matching")).Contains("host-unaccepted-paid-outcome-preserved", StringComparison.Ordinal), "cancelled delivery reopen parks");
    }

    private static async Task InvalidHostSchema()
    {
        var (store, state, host) = New("host-schema");
        var response = await host.Call("invalid-host-json", FixtureBehavior.InvalidHostJson);
        using (response.NativeContinuation)
        {
            Check.ThrowsSync<InvalidDataException>(() => host.Accept("invalid-host-json", response));
            state.Attempts["invalid-host-json"] = state.Attempts["invalid-host-json"] with { Acceptance = "host-rejected", Failure = "host_schema_properties" };
            store.Save(state);
        }
        Check.Equal(11, store.Read().Attempts["invalid-host-json"].Evidence!.Usage!.PromptTokens);
        Check.Equal(0, state.Tools.Count);
        Check.That(!state.Completed, "parse failure cannot fulfill objective");
        Check.That((await Child(store.Path, "matching")).Contains("host-unaccepted-paid-outcome-preserved", StringComparison.Ordinal), "host parse rejection reopen parks");
    }

    private static async Task MissingUsage()
    {
        var (store, _, host) = New("unknown-usage");
        var response = await host.Call("usage-absent", FixtureBehavior.MissingUsage);
        using (response.NativeContinuation) host.Accept("usage-absent", response);
        var attempt = store.Read().Attempts["usage-absent"];
        Check.Equal<int?>(null, attempt.Evidence!.Usage?.PromptTokens);
        Check.Equal<int?>(null, attempt.Evidence.Usage?.OutputTokens);
        Check.Equal<int?>(null, attempt.Evidence.Usage?.TotalTokens);
        Check.Equal<decimal?>(null, attempt.ObservedFixtureCost);
        Check.Equal(0.01m, attempt.Reserved); // Unknown never silently becomes zero paid custody.
    }

    private static async Task RouteProfiles()
    {
        var (store, state, host) = New("route-profiles");
        var original = state.Bundle;
        state.CurrentSettings = ChangedSettings(state);
        store.Save(state);
        var response = await host.Call("retained-text-route");
        using (response.NativeContinuation) host.Accept("retained-text-route", response);
        Check.Equal(original, state.Bundle);
        Check.Equal(original.Memory.ProfileId, state.CurrentSettings.Memory.ProfileId);
        Check.Equal(original.Conversation.ProfileId, state.CurrentSettings.Conversation.ProfileId);
        Check.That(original.Memory.ProfileId != original.Conversation.ProfileId, "independent memory conversation profiles");
        var (_, newState, newHost) = New("new-admission");
        newState.Bundle = state.CurrentSettings;
        newState.CurrentSettings = state.CurrentSettings;
        var newResponse = await newHost.Call("new-text-route");
        using (newResponse.NativeContinuation) newHost.Accept("new-text-route", newResponse);
        Check.Equal("fixture-gpt-next", newState.Attempts["new-text-route"].Descriptor.ModelId);
        Check.That(state.Attempts["retained-text-route"].Descriptor.RequestIdentity != newState.Attempts["new-text-route"].Descriptor.RequestIdentity,
            "changed route and bundle change prepared request identity");
    }

    private static async Task LivePolicy()
    {
        foreach (var policy in new[] { new HostPolicy(Authorized: false), new HostPolicy(Eligible: false), new HostPolicy(Revoked: true) })
        {
            var (_, state, host) = New("live-policy");
            state.Policy = policy;
            await Check.Throws<LlmClientException>(() => host.Call("retained-but-denied"));
            Check.Equal(0, state.PhysicalSends.Count);
            Check.Equal(0, state.Attempts.Count);
        }
        var (_, migrated, migratedHost) = New("no-google-transition");
        var google = migrated.Bundle.Foreground with { Provider = "Gemini", Model = "retained-google-model" };
        migrated.Bundle = migrated.Bundle with { Foreground = google };
        await Check.Throws<LlmClientException>(() => migratedHost.Call("google-forbidden", selectedRoute: google));
        Check.Equal(0, migrated.PhysicalSends.Count);
    }

    private static async Task WrongBinding()
    {
        foreach (var variant in new[] { "scope", "bundle", "operation", "route" })
        {
            var (_, state, host) = New("wrong-" + variant);
            var route = state.Bundle.Foreground;
            var refs = new LlmAttemptReferences("bad-binding", variant == "operation" ? "wrong.operation" : route.Operation,
                route.Phase, variant == "route" ? "wrong.route" : route.Fingerprint, variant == "bundle" ? "wrong.bundle" : state.Bundle.Id);
            if (variant == "scope")
            {
                using var wire = new RecoveryTransport(new EpisodeStore(Path.Combine(RunRoot, "wrong-scope-wire.json")), state, "bad-binding", FixtureBehavior.ModelDecision);
                using var http = new HttpClient(wire);
                using var prepared = LlmCallPreparation.Prepare(host.Request(route), RecoveryHost.Options, refs, "beta", http);
                var admission = new DurableAdmission(new EpisodeStore(Path.Combine(RunRoot, "wrong-scope.json")), state, route);
                await Check.Throws<LlmClientException>(() => prepared.GenerateAsync(admission));
            }
            else await Check.Throws<LlmClientException>(() => host.Call("bad-binding", suppliedReferences: refs));
            Check.Equal(0, state.PhysicalSends.Count);
        }
        var (_, duplicateState, duplicateHost) = New("duplicate");
        var response = await duplicateHost.Call("same-id");
        using (response.NativeContinuation) duplicateHost.Accept("same-id", response);
        var original = duplicateState.Attempts["same-id"];
        await Check.Throws<LlmClientException>(() => duplicateHost.Call("same-id"));
        Check.Equal(original, duplicateState.Attempts["same-id"]);
        Check.Equal(1, duplicateState.PhysicalSends.Values.Sum());
    }

    private static async Task BridgeShapes()
    {
        var (_, state, host) = New("bridge-shapes");
        foreach (var (route, schema, mode) in new[]
        {
            (state.Bundle.Evaluation, BridgeContractFixtures.Memory, FixtureBehavior.MemoryDecision),
            (state.Bundle.Document, BridgeContractFixtures.Document, FixtureBehavior.DocumentDecision)
        })
        {
            var request = host.Request(route, schema: schema) with { Messages = BridgeContractFixtures.AuthoredMessages() };
            var response = await host.Call(route.Operation, mode, route, suppliedRequest: request);
            using (response.NativeContinuation) BridgeContractFixtures.Validate(response.Text, schema);
            state.Attempts[route.Operation] = state.Attempts[route.Operation] with { Acceptance = "host-validated-fixture" };
        }
        Check.Equal(2, state.PhysicalSends.Values.Sum());
        var authors = BridgeContractFixtures.AuthoredMessages();
        Check.Equal(LlmMessageRole.User, authors[1].Role);
        using var agentProjection = JsonDocument.Parse(authors[2].Content);
        Check.Equal("Assistant", agentProjection.RootElement.GetProperty("author").GetString());
        Check.Equal("participant-message", state.Sources[0].Id);
        Check.Equal(SemanticAuthor.User, state.Sources[0].Author);
        Check.Equal(SemanticAuthor.Assistant, state.Sources[1].Author);
        using var unsupported = JsonDocument.Parse(BridgeContractFixtures.Document);
        Check.That(unsupported.RootElement.GetProperty("required").GetArrayLength() == 3, "document strict required citations");
    }

    private static async Task Search()
    {
        var (store, state, host) = New("shared-search");
        var request = OpenAiWebSearch.CreateRequest(state.Bundle.Foreground.Model, "synthetic query",
            new(MaxOutputTokens: 128, Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High)), 2);
        var response = await host.Call("search", FixtureBehavior.Search, suppliedRequest: request);
        using var native = response.NativeContinuation;
        Check.Equal("**source**", response.Text);
        Check.Equal("external-untrusted", response.WebSearch!.Trust);
        Check.Equal(1, response.WebSearch.CompletedCalls);
        Check.Equal(0, response.WebSearch.Citations[0].AnswerStartIndex);
        Check.Equal(10, response.WebSearch.Citations[0].AnswerEndIndex);
        var persisted = JsonSerializer.Deserialize<LlmWebSearchEvidence>(JsonSerializer.Serialize(response.WebSearch))!;
        Check.Equal(response.WebSearch.Sources[0].Url, persisted.Sources[0].Url);
        Check.That(!JsonSerializer.Serialize(state.Attempts["search"]).Contains("example.com", StringComparison.Ordinal), "structural journal excludes search source text");
        using var binding = LlmNativeContinuationBinding.FromRequest(LlmConfigurationReports.Describe(request, RecoveryHost.Options), request);
        var export = LlmNativeContinuationCodec.ExportSensitive(native, binding);
        using var payload = export.SensitivePayload;
        Check.That(export.Succeeded, "search native export");
        store.ProtectNative(state, payload!.CopySensitiveBytes());
        var bytes = store.UnprotectNative(state);
        try
        {
            var imported = LlmNativeContinuationCodec.ImportSensitive(bytes, binding);
            using var reopened = imported.Continuation;
            Check.That(imported.Succeeded, "search native import");
            Check.That(LlmNativeContinuationCodec.ValidateBinding(reopened!, binding).IsCompatible, "search native matching binding");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task NativeAuthentication()
    {
        var (store, state, host) = New("native-authentication");
        var response = await host.Call("native-source");
        using (response.NativeContinuation) host.Accept("native-source", response);
        var original = state.NativeEnvelope!;
        state.Scope = "beta";
        Check.ThrowsSync<AuthenticationTagMismatchException>(() => store.UnprotectNative(state));
        state.Scope = "alpha";
        var bytes = Convert.FromBase64String(original);
        bytes[^1] ^= 1;
        state.NativeEnvelope = Convert.ToBase64String(bytes);
        Check.ThrowsSync<AuthenticationTagMismatchException>(() => store.UnprotectNative(state));
        state.NativeEnvelope = original;
    }

    private static async Task HostAcceptance()
    {
        var (_, state, host) = New("host-negative-acceptance");
        var response = await host.Call("proposal");
        using (response.NativeContinuation)
        {
            Check.ThrowsSync<InvalidDataException>(() => host.Accept("proposal", response with
            { Text = "{\"decision\":\"reply\",\"candidate\":{\"reply\":\"Everything done\"},\"action\":null}" }));
            foreach (var invalid in new[]
            {
                "{\"decision\":\"action\",\"candidate\":null,\"action\":{\"capability_ref\":\"accept-item\",\"input\":{\"id\":\"item-a\",\"scope\":\"beta\",\"version\":0}}}",
                "{\"decision\":\"action\",\"candidate\":null,\"action\":{\"capability_ref\":\"accept-item\",\"input\":{\"id\":\"item-a\",\"scope\":\"alpha\",\"version\":99}}}"
            }) Check.ThrowsSync<InvalidDataException>(() => host.Accept("proposal", response with { Text = invalid }));
        }
        Check.Equal(0, state.Tools.Count);
        Check.That(!state.Completed, "narration and invalid effects not completion");
        Check.ThrowsSync<InvalidDataException>(() => host.Accept("unreserved-attempt", response));
    }

    private static async Task ProviderTransition()
    {
        var (store, state, _) = New("provider-transition");
        var oldRoute = state.Bundle.Foreground with { Provider = "Gemini", Model = "retained-google-model", Fingerprint = "route:retained-google:v1" };
        var oldBundle = state.Bundle with { Id = "retained-google-bundle", Foreground = oldRoute };
        var originalRefs = new LlmAttemptReferences("retained-google-original", oldRoute.Operation, oldRoute.Phase, oldRoute.Fingerprint, oldBundle.Id);
        var retained = new HostAttempt(new(originalRefs, state.Scope, LlmProviderWorkKind.Inference,
            "Gemini", "interactions", oldRoute.Model, "retained-synthetic-config", "retained-synthetic-request", "retained-synthetic-body", 123, 123, "unmeasured"),
            0.01m, new(originalRefs, "Gemini", oldRoute.Model, dispatchCertainty: LlmDispatchCertainty.MayHaveDispatched,
                outcome: LlmAttemptOutcome.Abandoned), "unknown");
        // Imported synthetic historical custody, not a new Google request. The exact old route,
        // operation/bundle, reservation and uncertainty survive the explicit provider transition.
        state.Attempts.Add(originalRefs.AttemptId, retained);
        state.Transitions.Add("retained-google-bundle -> bundle-v1: explicit OpenAI-only successor; original unknown remains");
        state.Replacements.Add(new(originalRefs.AttemptId, "openai-successor", "host migration policy permits OpenAI successor only"));
        store.Save(state);
        var reopened = store.Read();
        var host = new RecoveryHost(store, reopened);
        var response = await host.Call("openai-successor", replacementOf: originalRefs.AttemptId);
        using (response.NativeContinuation) host.Accept("openai-successor", response);
        for (var index = 0; index < 2; index++)
        {
            var id = "openai-continue-" + index;
            var next = await host.Call(id);
            using (next.NativeContinuation) host.Accept(id, next);
        }
        Check.That(reopened.Completed, "OpenAI successor satisfies preserved objective");
        Check.Equal(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(reopened.Attempts[originalRefs.AttemptId]));
        Check.Equal(0.04m, reopened.Attempts.Values.Sum(attempt => attempt.Reserved));
        Check.That(reopened.PhysicalSends.Keys.All(id => reopened.Attempts[id].Descriptor.ProviderName == "openai"), "zero new Google inference or embedding calls");
        Check.Equal(3, reopened.PhysicalSends.Values.Sum());
        Check.Equal(oldBundle.Memory.ProfileId, reopened.Bundle.Memory.ProfileId);
        Check.Equal(oldBundle.Conversation.ProfileId, reopened.Bundle.Conversation.ProfileId);
    }

    private static async Task ReturnedBeforeAccept()
    {
        var (store, state, host) = New("returned-crash-window");
        var first = await host.Call("accepted-before-crash");
        using (first.NativeContinuation) host.Accept("accepted-before-crash", first);
        var oldAcceptedCapsule = state.NativeEnvelope;
        var writer = await Child(store.Path, "matching", returnBeforeAccept: true);
        Check.That(writer.Contains("host-returned-without-accepting", StringComparison.Ordinal), "separate writer process delivered response");
        var delivered = store.Read();
        var returnedAttempt = delivered.Attempts["delivered-before-crash"];
        Check.Equal("returned", returnedAttempt.Acceptance);
        Check.Equal("completed", returnedAttempt.Evidence!.ProviderStatus);
        Check.Equal(11, returnedAttempt.Evidence.Usage!.PromptTokens);
        Check.That(delivered.ReopenedProcessId != Environment.ProcessId, "delivery occurred in a fresh process");
        Check.Equal(2, delivered.PhysicalSends.Values.Sum());
        Check.Equal(1, delivered.Tools.Count);
        Check.Equal(oldAcceptedCapsule, delivered.NativeEnvelope);
        var reader = await Child(store.Path, "matching");
        Check.That(reader.Contains("host-unaccepted-paid-outcome-preserved", StringComparison.Ordinal), "second fresh process parks returned-before-Accept");
        var parked = store.Read();
        Check.Equal(2, parked.PhysicalSends.Values.Sum());
        Check.Equal(1, parked.Tools.Count);
        Check.Equal(JsonSerializer.Serialize(returnedAttempt), JsonSerializer.Serialize(parked.Attempts["delivered-before-crash"]));
        Check.Equal(oldAcceptedCapsule, parked.NativeEnvelope);
        var recovery = new RecoveryHost(store, parked);
        await Check.Throws<LlmClientException>(() => recovery.Call("silent-reinference"));
        Check.Equal(2, parked.Attempts.Count);
        parked.Replacements.Add(new("delivered-before-crash", "explicit-returned-successor",
            "delivered proposal/native are unavailable; explicitly reconstruct accepted host evidence with a distinct successor"));
        parked.Transitions.Add("returned-unaccepted parked -> explicit accepted-evidence reconstruction; original paid attempt retained");
        store.Save(parked);
        var replacement = await recovery.Call("explicit-returned-successor", replacementOf: "delivered-before-crash");
        using (replacement.NativeContinuation) recovery.Accept("explicit-returned-successor", replacement);
        var completion = await recovery.Call("explicit-returned-completion");
        using (completion.NativeContinuation) recovery.Accept("explicit-returned-completion", completion);
        Check.That(parked.Completed, "explicit returned-outcome recovery completes objective");
        Check.Equal(0.04m, parked.Attempts.Values.Sum(attempt => attempt.Reserved));
        Check.Equal(JsonSerializer.Serialize(returnedAttempt), JsonSerializer.Serialize(parked.Attempts["delivered-before-crash"]));
        Check.Equal(4, parked.PhysicalSends.Values.Sum());
    }

    private static CapturedBundle ChangedSettings(EpisodeState state) => state.Bundle with
    {
        Id = "bundle-v2", Foreground = state.Bundle.Foreground with { Model = "fixture-gpt-next", Fingerprint = "route:foreground:v2" }
    };

    private static (EpisodeStore Store, EpisodeState State, RecoveryHost Host) New(string name)
    {
        var store = new EpisodeStore(Path.Combine(RunRoot, name + "-" + Guid.NewGuid().ToString("N"), "episode.json"));
        var state = new EpisodeState();
        store.Save(state);
        return (store, state, new(store, state));
    }

    private static async Task<string> Child(string statePath, string nativeMode, bool returnBeforeAccept = false)
    {
        var executable = Environment.GetEnvironmentVariable("AGENTICA_PROVIDER_CONSUMER_DOTNET") ?? "dotnet";
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(returnBeforeAccept ? "--return-before-accept" : "--resume");
        info.ArgumentList.Add(statePath);
        if (!returnBeforeAccept) info.ArgumentList.Add(nativeMode);
        using var child = Process.Start(info) ?? throw new InvalidOperationException("child_process_start_failed");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await child.WaitForExitAsync(timeout.Token);
        if (child.ExitCode != 0) throw new InvalidOperationException("fresh_process_failed:" + await error);
        return await output;
    }
}

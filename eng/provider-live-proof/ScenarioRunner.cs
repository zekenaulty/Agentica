using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Contracts;

namespace Agentica.ProviderLiveProof;

/// <summary>Checks canonical saved state and causal tool observations, not model narration.
/// Exported verification contains only identities, counts, hashes and bounded verdict codes.</summary>
internal static class ScenarioRunner
{
    internal const string ChallengeFile = "challenge.txt";
    internal const string Prompt = "Complete this isolated tool experiment. First make a plan containing only scratch.read for the relative file challenge.txt, then wait for its observation. The file contains JSON fields marker, left and right. After reading it, use a later plan to call utility.calculate with the expression consisting of the observed left integer, an asterisk, and the observed right integer. Wait for that calculation's observation. Finally call experiment.response.emit with exactly marker:product, substituting the observed marker and the exact integer product; include no other text or formatting. Do not guess the file values or combine the unknown calculation and response into the initial plan.";

    internal static ExperimentSettings Settings() => new()
    {
        Mode = "harness",
        Model = "gpt-6-luna",
        ReasoningEffort = "high",
        Instructions = "",
        ReasoningSummary = true,
        AmbientWebSearch = false,
        MemoryEnabled = false,
        ThreadRecallEnabled = false,
        ThreadSemanticRecallEnabled = false,
        MaxProviderCalls = 6,
        MaxOutputTokens = 8192,
        ContextWindowTokens = 131072,
        // Character bounds include planner instructions/schema and repair reserve.
        // The guarded native count independently enforces the 8,000-token limit.
        MaxInputCharacters = 40000,
        MaxHistoryMessages = 0,
        HistoryPolicy = "stop",
        MaxSteps = 6,
        MaxContinuations = 3,
        TimeoutSeconds = 600,
        MaxToolResultCharacters = 4000,
        MaxToolEvidenceCharacters = 8000,
        EnabledTools = [ExperimentToolIds.ScratchRead, ExperimentToolIds.Calculate]
    };

    internal static ScenarioVerification Verify(ExperimentStore store, ProofScenario scenario, ProviderLedger ledger)
    {
        scenario.Validate();
        if (scenario.TurnId is null) throw new InvalidDataException("proof.turn_not_observed");
        var conversation = store.Get(scenario.ChatId);
        var turn = store.GetTurn(scenario.ChatId, scenario.TurnId);
        var challengeText = File.ReadAllText(Path.Combine(store.WorkspaceRoot, scenario.ChatId, ChallengeFile), Encoding.UTF8);
        var challenge = JsonSerializer.Deserialize<ProofChallenge>(challengeText, HostProtocol.Json)
            ?? throw new InvalidDataException("proof.invalid_challenge");
        if (challenge.Marker.Length != 32 || !challenge.Marker.All(char.IsAsciiHexDigit) ||
            challenge.Left is < 1000 or >= 10000 || challenge.Right is < 1000 or >= 10000)
            throw new InvalidDataException("proof.invalid_challenge");
        var expected = challenge.Marker + ":" + ((long)challenge.Left * challenge.Right).ToString(CultureInfo.InvariantCulture);
        var events = ReadEvents(store, scenario.ChatId, scenario.TurnId);
        var failures = new List<string>();
        void Require(bool condition, string code) { if (!condition) failures.Add(code); }
        Require(turn.Status == "completed" && turn.AssistantText == expected, "answer_not_exact");
        Require(turn.UserText == Prompt && conversation.Turns.Count == 1, "conversation_not_isolated");
        Require(SettingsHash(turn.Settings) == SettingsHash(Settings()) &&
            SettingsHash(conversation.Settings) == SettingsHash(Settings()), "settings_changed");
        Require(turn.DroppedTraceCount == 0, "trace_coverage_incomplete");
        var outcomes = events.Where(item => item.Type == "harness.outcome").ToArray();
        Require(outcomes.Length == 1, "canonical_outcome_missing_or_ambiguous");
        var outcome = outcomes.Length == 1 ? outcomes[0].Data : JsonSerializer.SerializeToElement(new { });
        Require(Text(outcome, "status").Equals("Succeeded", StringComparison.OrdinalIgnoreCase), "host_did_not_accept");
        Require(Array(outcome, "residualObligations").Length == 0 && Has(outcome, "residualObligations"), "residual_obligations");
        Require(Array(outcome, "blockers").Length == 0 && Has(outcome, "blockers"), "host_blockers");
        Require(Array(outcome, "refinements").Length >= 2, "insufficient_model_refinement");
        Require(Array(outcome, "plans").Length >= 3, "insufficient_plan_versions");
        var receipts = Array(outcome, "receipts");
        var expectedTools = new[] { ExperimentToolIds.ScratchRead, ExperimentToolIds.Calculate, OpenAiExperimentExecutor.ResponseToolId };
        Require(receipts.Select(item => Text(item, "ToolId")).SequenceEqual(expectedTools), "tool_receipt_order");
        Require(receipts.Length == 3 && receipts.All(item => Text(item, "Status").Equals("Succeeded", StringComparison.OrdinalIgnoreCase)), "tool_receipt_failure");
        var read = receipts.FirstOrDefault(item => Text(item, "ToolId") == ExperimentToolIds.ScratchRead);
        var calculation = receipts.FirstOrDefault(item => Text(item, "ToolId") == ExperimentToolIds.Calculate);
        var response = receipts.FirstOrDefault(item => Text(item, "ToolId") == OpenAiExperimentExecutor.ResponseToolId);
        var readResult = Property(Property(read, "Data"), "result");
        Require(Text(readResult, "path") == ChallengeFile && Text(readResult, "content") == challengeText &&
            !Boolean(readResult, "truncated"), "scratch_observation_not_exact");
        var calculationResult = Property(Property(calculation, "Data"), "result");
        Require(Text(calculationResult, "exactDecimal") == ((long)challenge.Left * challenge.Right).ToString(CultureInfo.InvariantCulture), "calculation_observation_incorrect");
        var calculationStep = Text(calculation, "StepId");
        var multiplication = challenge.Left.ToString(CultureInfo.InvariantCulture) + "*" + challenge.Right.ToString(CultureInfo.InvariantCulture);
        var reversed = challenge.Right.ToString(CultureInfo.InvariantCulture) + "*" + challenge.Left.ToString(CultureInfo.InvariantCulture);
        Require(Array(outcome, "plans").SelectMany(plan => Array(plan, "Steps")).Any(step =>
            Text(step, "StepId") == calculationStep && Text(step, "ToolId") == ExperimentToolIds.Calculate &&
            Text(Property(step, "Input"), "expression").Replace(" ", "", StringComparison.Ordinal) is var expression &&
            (expression == multiplication || expression == reversed)), "calculation_not_bound_to_observed_operands");
        Require(Text(Property(response, "Data"), "content") == expected, "response_receipt_not_exact");
        var artifact = Array(outcome, "artifacts").FirstOrDefault(item => Text(item, "Kind") == OpenAiExperimentExecutor.ResponseArtifactKind &&
            Text(Property(item, "Payload"), "content") == expected);
        var artifactId = Text(artifact, "ArtifactId");
        var responseReceiptId = Text(response, "ReceiptId");
        Require(artifactId.Length != 0 && Array(artifact, "Evidence").Any(reference =>
            Text(reference, "Kind") == "receipt" && Text(reference, "RefId") == responseReceiptId), "artifact_receipt_not_linked");
        Require(Array(outcome, "CompletionEvidence").Any(reference => Text(reference, "Kind") == "artifact" &&
            Text(reference, "RefId") == artifactId && artifactId.Length != 0), "completion_evidence_not_resolved");

        long Observation(string tool) => events.Where(item => item.Type == "harness.event" &&
            Text(Property(item.Data, "executionEvent"), "Type") == "observation.made" &&
            Text(Property(Property(item.Data, "executionEvent"), "Context"), "ToolId") == tool)
            .Select(item => item.Sequence).DefaultIfEmpty(0).First();
        long Receipt(string id) => id.Length == 0 ? 0 : events.Where(item => item.Type == "harness.event" &&
            Text(Property(item.Data, "executionEvent"), "Type") == "receipt.emitted" &&
            Text(Property(Property(item.Data, "executionEvent"), "Context"), "ReceiptId") == id)
            .Select(item => item.Sequence).DefaultIfEmpty(0).First();
        var readObservation = Observation(ExperimentToolIds.ScratchRead);
        var calculationObservation = Observation(ExperimentToolIds.Calculate);
        var readReceipt = Receipt(Text(read, "ReceiptId"));
        var calculationReceipt = Receipt(Text(calculation, "ReceiptId"));
        var responseReceipt = Receipt(responseReceiptId);
        var dispatches = events.Where(item => item.Type == "provider.dispatch").ToArray();
        Require(dispatches.Length is >= 3 and <= 6, "inference_attempt_count");
        Require(readObservation > 0 && readReceipt > readObservation && calculationObservation > readReceipt &&
            calculationReceipt > calculationObservation && responseReceipt > calculationReceipt,
            "observation_receipt_causality");
        Require(dispatches.Any(item => item.Sequence < readObservation) &&
            dispatches.Any(item => item.Sequence > readReceipt && item.Sequence < calculationObservation) &&
            dispatches.Any(item => item.Sequence > calculationReceipt && item.Sequence < responseReceipt), "missing_model_decision_after_observation");
        var attempts = events.Where(item => item.Type == "provider.attempt").ToArray();
        Require(attempts.Length == dispatches.Length && dispatches.All(dispatch => attempts.Any(attempt =>
            attempt.CallId == dispatch.CallId && Text(Property(attempt.Data, "evidence"), "ProviderStatus") == "completed")),
            "provider_terminal_evidence_missing");
        var requests = events.Where(item => item.Type == "provider.request").ToArray();
        foreach (var request in requests)
        {
            var body = Property(request.Data, "body");
            Require(Text(body, "model") == "gpt-6-luna" && Text(Property(body, "reasoning"), "effort") == "high" &&
                Boolean(body, "stream") && Has(body, "store") && !Boolean(body, "store") &&
                !Has(body, "previous_response_id") && !Has(body, "conversation"), "provider_request_not_stateless_or_selected");
            Require(ledger.Attempts.Any(attempt => attempt.Kind == "inference" && attempt.BodyHash == Text(request.Data, "hash")), "dispatch_not_in_cumulative_ledger");
        }
        Require(ledger.Attempts.Count(item => item.Kind == "inference") <= 6 &&
            ledger.Attempts.Count(item => item.Kind == "count") <= 8 && ledger.HeldUsd <= 5m,
            "cumulative_proof_limit_exceeded");
        var codes = string.Join(",", failures.Distinct(StringComparer.Ordinal));
        return new(failures.Count == 0, codes, turn.Status, Hash(turn.AssistantText), Hash(challenge.Marker),
            SettingsHash(turn.Settings), Hash(outcome.GetRawText()),
            Hash(JsonSerializer.Serialize(events.Select(item => new { item.Sequence, item.Type, item.CallId, hash = Hash(item.Data.GetRawText()) }), HostProtocol.Json)),
            dispatches.Length, Array(outcome, "refinements").Length, events.Count,
            readObservation, readReceipt, calculationObservation, calculationReceipt, responseReceipt,
            artifactId, responseReceiptId);
    }

    private static IReadOnlyList<SavedEvent> ReadEvents(ExperimentStore store, string chatId, string turnId)
    {
        var events = new List<SavedEvent>();
        long after = 0;
        while (true)
        {
            var page = store.ReadEvents(chatId, turnId, after, 100);
            if (page.Count == 0) break;
            foreach (var item in page)
                events.Add(new(item.Sequence, item.Type, item.CallId, store.ReadPayload(chatId, turnId, item.Sequence)));
            after = page[^1].Sequence;
        }
        return events;
    }

    private static string SettingsHash(ExperimentSettings settings) => Hash(JsonSerializer.Serialize(settings, HostProtocol.Json));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Has(JsonElement element, string name) => Property(element, name).ValueKind != JsonValueKind.Undefined;
    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        ? element.EnumerateObject().Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value).FirstOrDefault() : default;
    private static string Text(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static JsonElement[] Array(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().ToArray() : [];
    private sealed record SavedEvent(long Sequence, string Type, string? CallId, JsonElement Data);
}

internal sealed record ScenarioVerification(bool Accepted, string FailureCodes, string TurnStatus,
    string AnswerHash, string MarkerHash, string SettingsHash, string OutcomeHash, string TraceHash,
    int InferenceAttempts, int Refinements, int TraceEvents, long ReadObservationSequence, long ReadReceiptSequence,
    long CalculationObservationSequence, long CalculationReceiptSequence, long ResponseReceiptSequence,
    string ArtifactId, string ResponseReceiptId);

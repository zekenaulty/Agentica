using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Chat.Recall;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentMeasuredContextTests
{
    [Fact]
    public async Task Actual_measurement_admits_a_large_current_input_without_treating_wire_bytes_as_tokens()
    {
        var (conversation, turn) = Fixture(new ExperimentSettings
        {
            MaxOutputTokens = 64000,
            ContextWindowTokens = 131072,
            MaxInputCharacters = 256000,
            HistoryPolicy = "recent"
        }, 0, new string('a', 62413) + new string('\u00e9', 14));
        var counter = new Counter(_ => 15000);
        var prepared = await ExperimentContextCompiler.PrepareAsync(conversation, turn, counter);
        var counted = Assert.Single(counter.Requests);
        Assert.True(counted.Length + 512 > prepared.Manifest.InputAllowance);
        Assert.Equal(15000, prepared.Manifest.EstimatedInputUnits);
        Assert.Equal(15000, prepared.Manifest.TokenCount!.Tokens);
        Assert.Equal(counted.Length, prepared.Manifest.WireBytes);
        Assert.Equal(Hash(counted), prepared.Manifest.TokenCount.RequestHash);
        using var json = JsonDocument.Parse(counted);
        Assert.Equal(turn.UserText, Assert.Single(json.RootElement.GetProperty("input").EnumerateArray()).GetProperty("content").GetString());
        Assert.False(json.RootElement.TryGetProperty("instructions", out _));
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task A_four_turn_floor_is_measured_once_and_remains_intact_despite_larger_wire_bytes()
    {
        var settings = new ExperimentSettings
        {
            ThreadRecallEnabled = true,
            ThreadRecallMode = "manual",
            ThreadRecallRecentTurns = 4,
            MaxOutputTokens = 64000,
            ContextWindowTokens = 131072,
            MaxInputCharacters = 256000
        };
        var (conversation, turn) = Fixture(settings, 4, "Current input stays exact.", new string('x', 20000));
        var counter = new Counter(_ => 43000);
        var source = new Source(turn.Sequence - 1, "old exact source") { BeforeAccess = () => Assert.NotEmpty(counter.Measurements) };
        var prepared = await ExperimentContextCompiler.PrepareAsync(conversation, turn, counter, source);
        Assert.Equal(8, prepared.Messages.Count);
        Assert.Equal(conversation.Turns.SelectMany(item => new[] { item.UserText, item.AssistantText }), prepared.Messages.Select(item => item.Content));
        Assert.Single(counter.Requests);
        Assert.True(prepared.Manifest.WireBytes > prepared.Manifest.InputAllowance);
        Assert.Equal(43000, prepared.Manifest.TokenCount!.Tokens);
        Assert.Empty(prepared.Recall!.Evidence);
        Assert.Equal(0, source.RetrievalCalls);
        Assert.Equal(settings, turn.Settings);
    }

    [Theory]
    [InlineData(true, "context.current_input_limit")]
    [InlineData(false, "context.recent_floor_limit")]
    public async Task Measured_floor_failure_distinguishes_current_only_and_combined_overflow_before_retrieval(bool currentTooLarge, string expectedCode)
    {
        var (conversation, turn) = Fixture(RecallSettings(), 1, "Current", "Earlier");
        var counter = new Counter(json => currentTooLarge || json.GetProperty("input").GetArrayLength() > 1 ? 5000 : 1000);
        var source = new Source(turn.Sequence - 1, "unread exact source");
        var error = await Assert.ThrowsAsync<ExperimentException>(() => ExperimentContextCompiler.PrepareAsync(conversation, turn, counter, source));
        Assert.Equal(expectedCode, error.Code);
        Assert.Contains("model input token allowance", error.Message, StringComparison.Ordinal);
        Assert.Contains("input token allowance 3840", error.Message, StringComparison.Ordinal);
        Assert.Contains("output reserve 256", error.Message, StringComparison.Ordinal);
        Assert.Contains("safety reserve 4096", error.Message, StringComparison.Ordinal);
        Assert.Contains(currentTooLarge ? "Changing the recall floor cannot repair" : "current input alone fits", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, counter.Requests.Count);
        Assert.Equal(0, source.Accesses);
    }

    [Theory]
    [InlineData(true, "context.current_input_limit")]
    [InlineData(false, "context.recent_floor_limit")]
    public async Task Character_failures_name_the_actual_gate_without_spending_a_token_count(bool currentTooLarge, string expectedCode)
    {
        var settings = RecallSettings() with { MaxInputCharacters = 1024 };
        var (conversation, turn) = Fixture(settings, 1, currentTooLarge ? new string('<', 200) : "Current", new string('<', 300));
        var counter = new Counter(_ => throw new InvalidOperationException("Character failures must not reach token measurement."));
        var error = await Assert.ThrowsAsync<ExperimentException>(() => ExperimentContextCompiler.PrepareAsync(conversation, turn, counter));
        Assert.Equal(expectedCode, error.Code);
        Assert.Contains("serialized request character limit", error.Message, StringComparison.Ordinal);
        Assert.Contains("model input tokens not measured", error.Message, StringComparison.Ordinal);
        Assert.Contains(currentTooLarge ? "token settings cannot repair" : "Token settings do not repair", error.Message, StringComparison.Ordinal);
        Assert.Empty(counter.Requests);
    }

    [Fact]
    public async Task Recent_history_uses_bounded_measured_binary_selection_of_complete_pairs()
    {
        var settings = new ExperimentSettings
        {
            HistoryPolicy = "recent",
            MaxHistoryMessages = 500,
            MaxInputCharacters = 256000,
            ContextWindowTokens = 8192,
            MaxOutputTokens = 256
        };
        var (conversation, turn) = Fixture(settings, 200, "Current", "past");
        var counter = new Counter(json => 1000 + json.GetProperty("input").GetArrayLength() * 100);
        var prepared = await ExperimentContextCompiler.PrepareAsync(conversation, turn, counter);
        Assert.Equal(26, prepared.Messages.Count); // 13 complete pairs + current = 27 messages -> 3700 fixture tokens.
        Assert.True(prepared.Manifest.TokenCount!.Tokens <= prepared.Manifest.InputAllowance);
        Assert.True(counter.Requests.Count <= 12);
        Assert.Equal(374, prepared.Manifest.OmittedMessageIds.Count);
        Assert.Equal(conversation.Turns[^13].Id + ":user", prepared.Messages[0].Id);
        Assert.Equal(conversation.Turns[^1].Id + ":assistant", prepared.Messages[^1].Id);
    }

    [Fact]
    public async Task Stop_policy_reports_removable_history_token_overflow_without_changing_the_policy_or_transcript()
    {
        var settings = new ExperimentSettings { HistoryPolicy = "stop", ContextWindowTokens = 8192, MaxOutputTokens = 256 };
        var (conversation, turn) = Fixture(settings, 1, "Current", "Earlier");
        var counter = new Counter(json => json.GetProperty("input").GetArrayLength() > 1 ? 5000 : 1000);
        var failure = await Assert.ThrowsAsync<ExperimentException>(() => ExperimentContextCompiler.PrepareAsync(conversation, turn, counter));
        Assert.Equal("context.history_limit", failure.Code);
        Assert.Contains("current input alone fits", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Select recent-history omission", failure.Message, StringComparison.Ordinal);
        Assert.Equal("stop", turn.Settings.HistoryPolicy);
        Assert.Single(conversation.Turns);
        Assert.Equal(2, counter.Requests.Count);
    }

    [Fact]
    public async Task Optional_quotes_are_reduced_by_measured_counts_with_exact_ranges_and_no_network_binary_loop()
    {
        var (conversation, turn) = Fixture(RecallSettings() with { ThreadRecallSelections = [new("old:user", 0, 6000)] }, 1, "Current", "Recent");
        var source = new Source(turn.Sequence - 1, string.Concat(Enumerable.Repeat("<\U0001f4a1>", 1500)));
        var counter = new Counter(json => 1000 + EvidenceCharacters(json));
        var prepared = await ExperimentContextCompiler.PrepareAsync(conversation, turn, counter, source);
        var quote = Assert.Single(prepared.Recall!.Evidence);
        Assert.Equal(source.Content[quote.StartOffset..quote.EndOffset], quote.Content);
        Assert.Equal(1500, quote.Content.Length);
        Assert.False(char.IsHighSurrogate(quote.Content[^1]));
        Assert.Equal(2500, prepared.Manifest.TokenCount!.Tokens);
        Assert.Equal(4, counter.Requests.Count); // Mandatory, full evidence, two measured exact-prefix reductions.
        Assert.Contains(prepared.Recall.Manifest.Omissions, item => item.Reason.Contains("model-input-token-limit", StringComparison.Ordinal));
        Assert.Equal(2, prepared.Messages.Count);
        var finalBytes = counter.Requests.Single(bytes => Hash(bytes) == prepared.Manifest.TokenCount.RequestHash);
        Assert.Equal(finalBytes.Length, prepared.Manifest.WireBytes);
        using var final = JsonDocument.Parse(finalBytes);
        Assert.Equal(turn.UserText, final.RootElement.GetProperty("input").EnumerateArray().Last().GetProperty("content").GetString());
    }

    [Fact]
    public async Task Nonfitting_optional_metadata_falls_back_to_the_already_counted_protected_floor()
    {
        var (conversation, turn) = Fixture(RecallSettings() with { ThreadRecallSelections = [new("old:user", 0, 1024)] }, 1, "Current", "Recent");
        var source = new Source(turn.Sequence - 1, new string('q', 1024));
        var counter = new Counter(json => EvidenceCharacters(json) == 0 ? 1000 : 5000);
        var prepared = await ExperimentContextCompiler.PrepareAsync(conversation, turn, counter, source);
        Assert.Empty(prepared.Recall!.Evidence);
        Assert.Equal(2, prepared.Messages.Count);
        Assert.Equal(1000, prepared.Manifest.TokenCount!.Tokens);
        Assert.Equal(Hash(counter.Requests[0]), prepared.Manifest.TokenCount.RequestHash);
        Assert.Equal(6, counter.Requests.Count); // Four attempts, then reuse mandatory measurement without another request.
        Assert.Contains(prepared.Recall.Manifest.Omissions, item => item.Reason.Contains("omitted after bounded fitting", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Harness_projection_does_not_claim_direct_envelope_tokens_or_apply_the_byte_proxy(bool recall)
    {
        var settings = new ExperimentSettings
        {
            Mode = "harness",
            ThreadRecallEnabled = recall,
            ThreadRecallMode = "manual",
            ContextWindowTokens = 8192,
            MaxOutputTokens = 256,
            MaxInputCharacters = 12000
        };
        var (conversation, turn) = Fixture(settings, 1, "Current", new string('<', 1000));
        var counter = new Counter(_ => throw new InvalidOperationException("Harness input must be counted only after its native planner request exists."));
        var prepared = await ExperimentContextCompiler.PrepareAsync(conversation, turn, counter);
        Assert.Equal(2, prepared.Messages.Count);
        Assert.Null(prepared.Manifest.TokenCount);
        Assert.Null(prepared.Manifest.WireBytes);
        Assert.Equal(0, prepared.Manifest.EstimatedInputUnits);
        Assert.Contains("native", prepared.Manifest.Estimator, StringComparison.Ordinal);
        Assert.Empty(counter.Requests);
    }

    [Fact]
    public async Task A_mismatched_measurement_hash_is_rejected_before_any_retrieval()
    {
        var (conversation, turn) = Fixture(RecallSettings(), 1, "Current", "Recent");
        var source = new Source(turn.Sequence - 1, "unread");
        var counter = new Counter(_ => 1000) { CorruptHash = true };
        var failure = await Assert.ThrowsAsync<ExperimentException>(() => ExperimentContextCompiler.PrepareAsync(conversation, turn, counter, source));
        Assert.Equal("context.token_count_invalid", failure.Code);
        Assert.Equal(0, source.Accesses);
    }

    [Fact]
    public async Task Cancellation_during_mandatory_measurement_prevents_historical_retrieval()
    {
        var (conversation, turn) = Fixture(RecallSettings(), 1, "Current", "Recent");
        var source = new Source(turn.Sequence - 1, "unread");
        using var cancellation = new CancellationTokenSource();
        var counter = new Counter(_ => 1000) { OnCount = token => { Assert.Equal(cancellation.Token, token); cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExperimentContextCompiler.PrepareAsync(conversation, turn, counter, source, cancellation.Token));
        Assert.Single(counter.Requests);
        Assert.Equal(0, source.Accesses);
    }

    private static ExperimentSettings RecallSettings() => new()
    {
        ThreadRecallEnabled = true,
        ThreadRecallMode = "manual",
        ThreadRecallNeighborRadius = 0,
        ThreadRecallRecentTurns = 1,
        ContextWindowTokens = 8192,
        MaxOutputTokens = 256,
        MaxInputCharacters = 256000
    };
    private static int EvidenceCharacters(JsonElement request)
    {
        var inputs = request.GetProperty("input").EnumerateArray().ToArray();
        if (inputs.Length < 2 || inputs[^2].GetProperty("content").GetString() is not { } text ||
            !text.StartsWith("{\"historicalThreadEvidence\":", StringComparison.Ordinal)) return 0;
        using var quotation = JsonDocument.Parse(text);
        return quotation.RootElement.GetProperty("historicalThreadEvidence").GetProperty("evidence")
            .EnumerateArray().Sum(item => item.GetProperty("quote").GetString()!.Length);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static (ExperimentConversation Conversation, ExperimentTurn Turn) Fixture(ExperimentSettings settings, int priorTurns,
        string current, string previousUser = "Earlier")
    {
        var now = DateTimeOffset.UtcNow;
        var turns = Enumerable.Range(2, priorTurns).Select(sequence => new ExperimentTurn("past-" + sequence, "conversation", sequence,
            previousUser, "Earlier answer", "completed", now, now, 1, settings, "transcript-only")).ToArray();
        var conversation = new ExperimentConversation("conversation", "Synthetic measured fixture", now, now, 1, settings, turns);
        return (conversation, new("current", "conversation", priorTurns + 2, current, "", "running", now, null, 1, settings, "transcript-only"));
    }
    private sealed class Counter(Func<JsonElement, int> count) : IExperimentInputTokenCounter
    {
        public bool CorruptHash { get; init; }
        public Action<CancellationToken>? OnCount { get; init; }
        public List<byte[]> Requests { get; } = [];
        private readonly List<ExperimentTokenCount> _measurements = [];
        public IReadOnlyList<ExperimentTokenCount> Measurements => _measurements;
        public Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = request.ToArray(); Requests.Add(bytes);
            OnCount?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(bytes);
            var measured = new ExperimentTokenCount(count(document.RootElement), "deterministic fixture count; not live provider tokens",
                CorruptHash ? "wrong-hash" : Hash(bytes));
            _measurements.Add(measured);
            return Task.FromResult(measured);
        }
    }
    private sealed class Source(int asOf, string content) : IConversationEvidenceSource
    {
        public Action? BeforeAccess { get; init; }
        public int Accesses { get; private set; }
        public int RetrievalCalls { get; private set; }
        public string Content => content;
        public string ScopeRef => "conversation";
        public int AsOfSequence => asOf;
        private void Access() { BeforeAccess?.Invoke(); Accesses++; }
        public ConversationEvidenceHead GetHead() { Access(); return new(ScopeRef, asOf, asOf, 1, "fixture-head"); }
        public ConversationEvidenceCoverage GetCoverage() { Access(); return Coverage(); }
        private ConversationEvidenceCoverage Coverage() => new("COMPLETE", asOf, asOf, 1, 1, true, "fixture-index");
        public ConversationSourcePage ListMessageHeadersPage(int? beforeOrder = null, int limit = 40) { Access(); return new([], null, 1, asOf); }
        public ConversationEvidenceResult ReadExact(string sourceId, int startOffset = 0, int maxCharacters = 4000, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Access(); RetrievalCalls++;
            var end = Math.Min(content.Length, startOffset + maxCharacters);
            if (end < content.Length && end > startOffset && char.IsHighSurrogate(content[end - 1])) end--;
            var header = new ConversationSourceHeader("old:user", "old", 1, "user", DateTimeOffset.UnixEpoch,
                Hash(Encoding.UTF8.GetBytes(content)), content.Length, 2);
            return Result([new(header, startOffset, end, content[startOffset..end], "exact fixture source", [])]);
        }
        public ConversationEvidenceResult ReadNeighbors(string sourceId, int radius = 1, int maxCharacters = 8000, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Access(); RetrievalCalls++; return Result([]); }
        public ConversationEvidenceResult Search(ConversationEvidenceSearch query, CancellationToken cancellationToken = default) =>
            ReadExact("old:user", 0, query.MaxCharacters, cancellationToken);
        private ConversationEvidenceResult Result(IReadOnlyList<ConversationEvidenceHit> hits) => new("fixture-read", ScopeRef, asOf,
            "COMPLETE", "canonical fixture", Coverage(), hits.Count, hits.Count, hits, null, false, [], 0);
    }
}

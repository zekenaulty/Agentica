using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Providers;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentContextBudgetTests
{
    [Fact]
    public void Recent_policy_reports_a_current_only_draft_that_exceeds_the_reserved_window()
    {
        // Synthetic size match only: none of the owner's draft content is retained here.
        var draft = new string('a', 62413) + new string('\u00e9', 14);
        Assert.Equal(62427, draft.Length);
        Assert.Equal(62441, Encoding.UTF8.GetByteCount(draft));
        var settings = new ExperimentSettings
        {
            MaxOutputTokens = 64000,
            ContextWindowTokens = 131072,
            MaxInputCharacters = 256000,
            MaxHistoryMessages = 128,
            HistoryPolicy = "recent",
            AmbientWebSearch = true,
            MaxWebSearchCalls = 20
        };
        var (conversation, turn) = Fixture(settings, draft, "earlier question", "earlier answer");
        var error = Assert.Throws<ExperimentException>(() => ExperimentContextCompiler.Prepare(conversation, turn));
        Assert.Equal("context.limit", error.Code);
        Assert.Contains("request proxy allowance", error.Message, StringComparison.Ordinal);
        Assert.Contains("Current-only request", error.Message, StringComparison.Ordinal);
        Assert.Contains("allowance 62976", error.Message, StringComparison.Ordinal);
        Assert.Contains("output reserve 64000", error.Message, StringComparison.Ordinal);
        Assert.Contains("safety reserve 4096", error.Message, StringComparison.Ordinal);
        Assert.Contains("history omitted 2/2", error.Message, StringComparison.Ordinal);
        Assert.Contains("not measured provider tokens", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Select recent-history", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Small_escaped_content_is_rejected_by_its_actual_serialized_character_size()
    {
        var settings = new ExperimentSettings
        {
            MaxInputCharacters = 1024,
            ContextWindowTokens = 8192,
            MaxOutputTokens = 256,
            HistoryPolicy = "recent"
        };
        var (conversation, turn) = Fixture(settings, new string('<', 200));
        var error = Assert.Throws<ExperimentException>(() => ExperimentContextCompiler.Prepare(conversation, turn));
        Assert.Equal("context.limit", error.Code);
        Assert.Contains("serialized request character limit", error.Message, StringComparison.Ordinal);
        Assert.Contains("current input/settings alone do not fit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("history omitted 0/0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_policy_omits_complete_escaped_pairs_until_the_real_wire_request_fits()
    {
        var settings = EscapedHistorySettings("recent");
        var (conversation, turn) = Fixture(settings, "next", new string('<', 300), new string('<', 300));
        var prepared = ExperimentContextCompiler.Prepare(conversation, turn);
        Assert.Empty(prepared.Messages);
        Assert.Equal(2, prepared.Manifest.OmittedMessageIds.Count);
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var traces = new List<ExperimentTraceInput>();
        var result = await executor.ExecuteAsync(Context(turn, prepared.Messages, traces), CancellationToken.None);
        Assert.Equal("completed", result.Status);
        var body = Assert.Single(handler.Bodies);
        using var json = JsonDocument.Parse(body);
        var input = Assert.Single(json.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("next", input.GetProperty("content").GetString());
        Assert.Equal(body.Length + 512, prepared.Manifest.EstimatedInputUnits);
        Assert.True(prepared.Manifest.EstimatedInputUnits <= prepared.Manifest.InputAllowance);
        Assert.Single(traces, trace => trace.Type == "provider.dispatch");
    }

    [Fact]
    public void Stop_policy_reports_the_same_escaped_wire_overflow_without_omitting_history()
    {
        var (conversation, turn) = Fixture(EscapedHistorySettings("stop"), "next", new string('<', 300), new string('<', 300));
        var error = Assert.Throws<ExperimentException>(() => ExperimentContextCompiler.Prepare(conversation, turn));
        Assert.Contains("request proxy allowance", error.Message, StringComparison.Ordinal);
        Assert.Contains("history omitted 0/2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Select recent-history omission", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_preflight_and_dispatch_size_the_identical_complete_settings_envelope()
    {
        var settings = new ExperimentSettings
        {
            Instructions = "  <system>\u00e9</system>\r\n",
            ReasoningSummary = true,
            AmbientWebSearch = true,
            MaxWebSearchCalls = 20,
            HistoryPolicy = "recent"
        };
        var (conversation, turn) = Fixture(settings, "next\n\"quoted\"", "prior <request>", "Unicode \u00e9 answer");
        var prepared = ExperimentContextCompiler.Prepare(conversation, turn);
        Assert.Equal(2, prepared.Messages.Count);
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var traces = new List<ExperimentTraceInput>();
        var result = await executor.ExecuteAsync(Context(turn, prepared.Messages, traces), CancellationToken.None);
        Assert.Equal("completed", result.Status);
        var body = Assert.Single(handler.Bodies);
        Assert.Equal(body.Length + 512, prepared.Manifest.EstimatedInputUnits);
        var request = JsonSerializer.SerializeToElement(Assert.Single(traces, trace => trace.Type == "provider.request").Data);
        Assert.Equal(prepared.Manifest.EstimatedInputUnits, request.GetProperty("estimatedInputUnits").GetInt32());
        using var json = JsonDocument.Parse(body);
        Assert.Equal(settings.Instructions, json.RootElement.GetProperty("instructions").GetString());
        Assert.Equal(settings.Model, json.RootElement.GetProperty("model").GetString());
        Assert.Equal("high", json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", json.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Equal("auto", json.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(20, json.RootElement.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public void Harness_keeps_its_existing_content_projection_budget_path()
    {
        var settings = new ExperimentSettings
        {
            Mode = "harness",
            MaxInputCharacters = 1024,
            ContextWindowTokens = 8192,
            MaxOutputTokens = 256
        };
        var (conversation, turn) = Fixture(settings, new string('<', 200));
        var prepared = ExperimentContextCompiler.Prepare(conversation, turn);
        Assert.Equal(1224, prepared.Manifest.EstimatedInputUnits);
        Assert.Contains("UTF-8 content bytes", prepared.Manifest.Estimator, StringComparison.Ordinal);
    }

    private static ExperimentSettings EscapedHistorySettings(string policy) => new()
    {
        MaxInputCharacters = 12000,
        ContextWindowTokens = 8192,
        MaxOutputTokens = 256,
        MaxHistoryMessages = 128,
        HistoryPolicy = policy
    };
    private static (ExperimentConversation Conversation, ExperimentTurn Turn) Fixture(ExperimentSettings settings,
        string input, string? previousUser = null, string? previousAnswer = null)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = previousUser is null ? Array.Empty<ExperimentTurn>() : new[]
        {
            new ExperimentTurn("previous", "conversation", 1, previousUser, previousAnswer ?? "", "completed", now, now,
                1, settings, "transcript-only")
        };
        var conversation = new ExperimentConversation("conversation", "Synthetic budget fixture", now, now, 1, settings, previous);
        var turn = new ExperimentTurn("current", conversation.Id, previous.Length + 1, input, "", "running", now, null,
            1, settings, "transcript-only");
        return (conversation, turn);
    }
    private static ExperimentExecutionContext Context(ExperimentTurn turn, IReadOnlyList<ExperimentMessage> history,
        List<ExperimentTraceInput> traces) => new(turn.ConversationId, turn, history, Path.GetTempPath(),
            item => { traces.Add(item); return Task.CompletedTask; }, _ => Task.CompletedTask);
    private static OpenAiProviderConfiguration Configuration() => new(name => name == "OPENAI_API_KEY" ? "fixture-key" : null);
    private sealed class Handler : HttpMessageHandler
    {
        public List<byte[]> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var completed = new
            {
                type = "response.completed",
                response = new
                {
                    id = "response-budget",
                    status = "completed",
                    output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Okay" } } } }
                }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(completed) + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}

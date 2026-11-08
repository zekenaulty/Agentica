using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.Tests;

public sealed class ReadableStreamTests
{
    [Fact]
    public async Task Simultaneously_open_summary_parts_keep_their_actual_indexes()
    {
        var events = await Capture([
            Part("response.reasoning_summary_part.added", "r", 0, 0, "summary_text", "", 1),
            Part("response.reasoning_summary_part.added", "r", 0, 1, "summary_text", "", 2),
            new { type = "response.reasoning_summary_text.delta", item_id = "r", output_index = 0, summary_index = 0, delta = "Alpha ", sequence_number = 3 },
            new { type = "response.reasoning_summary_text.delta", item_id = "r", output_index = 0, summary_index = 1, delta = "Beta ", sequence_number = 4 },
            new { type = "response.reasoning_summary_text.delta", item_id = "r", output_index = 0, summary_index = 0, delta = "continues.", sequence_number = 5 },
            new { type = "response.reasoning_summary_text.delta", item_id = "r", output_index = 0, summary_index = 1, delta = "ends.", sequence_number = 6 },
            Part("response.reasoning_summary_part.done", "r", 0, 1, "summary_text", "Beta ends.", 7),
            Part("response.reasoning_summary_part.done", "r", 0, 0, "summary_text", "Alpha continues.", 8), Terminal()]);
        var deltas = events.Where(item => item.Kind == LlmStreamEventKind.ThoughtSummaryDelta).ToArray();
        Assert.Equal("Alpha continues.", string.Concat(deltas.Where(item => item.Source!.SummaryIndex == 0).Select(item => item.Text)));
        Assert.Equal("Beta ends.", string.Concat(deltas.Where(item => item.Source!.SummaryIndex == 1).Select(item => item.Text)));
        events.Last().Response?.NativeContinuation?.Dispose();
    }

    [Fact]
    public async Task Native_blocks_preserve_interleaved_identity_whitespace_and_all_legacy_deltas()
    {
        var fragments = Enumerable.Range(0, 40).Select(index => index % 3 == 0 ? " " : index % 3 == 1 ? "same" : "\r\n").ToArray();
        var summary = string.Concat(fragments);
        var events = new List<object> { Part("response.reasoning_summary_part.added", "reason-a", 0, 0, "summary_text", "", 1) };
        events.AddRange(fragments.Select((fragment, index) => (object)new
        { type = "response.reasoning_summary_text.delta", item_id = "reason-a", output_index = 0, summary_index = 0, delta = fragment, sequence_number = index + 2 }));
        events.Add(new { type = "response.reasoning_summary_text.done", item_id = "reason-a", output_index = 0, summary_index = 0, text = summary, sequence_number = 42 });
        events.Add(Part("response.reasoning_summary_part.done", "reason-a", 0, 0, "summary_text", summary, 43));
        events.Add(Part("response.reasoning_summary_part.added", "reason-a", 0, 1, "summary_text", "", 44));
        events.Add(new { type = "response.reasoning_summary_text.delta", item_id = "reason-a", output_index = 0, summary_index = 1, delta = "Second paragraph.", sequence_number = 45 });
        events.Add(Part("response.reasoning_summary_part.done", "reason-a", 0, 1, "summary_text", "Second paragraph.", 46));
        events.Add(new { type = "response.content_part.added", item_id = "answer-a", output_index = 1, content_index = 0, part = new { type = "output_text", text = "" }, sequence_number = 47 });
        events.Add(new { type = "response.output_text.delta", item_id = "answer-a", output_index = 1, content_index = 0, delta = "An", sequence_number = 48 });
        events.Add(new { type = "response.output_text.delta", item_id = "answer-a", output_index = 1, content_index = 0, delta = "swer.", sequence_number = 49 });
        events.Add(new { type = "response.output_text.done", item_id = "answer-a", output_index = 1, content_index = 0, text = "Answer.", sequence_number = 50 });
        events.Add(new { type = "response.content_part.done", item_id = "answer-a", output_index = 1, content_index = 0, part = new { type = "output_text", text = "Answer." }, sequence_number = 51 });
        events.Add(Terminal());
        var captured = await Capture(events);
        Assert.Equal(fragments.Append("Second paragraph."), captured.Where(item => item.Kind == LlmStreamEventKind.ThoughtSummaryDelta).Select(item => item.Text));
        Assert.Equal(["An", "swer."], captured.Where(item => item.Kind == LlmStreamEventKind.TextDelta).Select(item => item.Text));
        var first = captured.Where(item => item.Source?.ItemId == "reason-a" && item.Source.SummaryIndex == 0).ToArray();
        Assert.Equal(summary, string.Concat(first.Where(item => item.Block?.Lifecycle == LlmStreamBlockLifecycle.Delta).Select(item => item.Text)));
        Assert.Equal(LlmStreamBlockLifecycle.Started, first[0].Block!.Lifecycle);
        Assert.Equal(LlmStreamBlockLifecycle.Completed, first[^1].Block!.Lifecycle);
        Assert.Equal(summary, first[^1].Block!.TextSnapshot);
        Assert.All(first, item => { Assert.Equal(1, item.Source!.Version); Assert.Equal(0, item.Source.OutputIndex); });
        Assert.All(captured.Where(item => item.Kind == LlmStreamEventKind.ContentBlock), item => Assert.Null(item.Text));
        var completed = Assert.Single(captured, item => item.Kind == LlmStreamEventKind.Completed);
        Assert.Equal(fragments.Append("Second paragraph."), completed.Response!.ThoughtSummaries!.Select(item => item.Text));
        Assert.Equal("Answer.", completed.Response.Text);
        completed.Response.NativeContinuation?.Dispose();
    }

    [Fact]
    public async Task Interrupted_stream_keeps_open_block_without_inventing_a_native_end()
    {
        var captured = new List<LlmStreamEvent>();
        using var http = ClientHttp([Part("response.reasoning_summary_part.added", "r", 0, 0, "summary_text", "", 1),
            new { type = "response.reasoning_summary_text.delta", item_id = "r", output_index = 0, summary_index = 0, delta = "Unfinished", sequence_number = 2 }]);
        var error = await Assert.ThrowsAsync<LlmClientException>(async () =>
        {
            await foreach (var item in Client(http).StreamAsync(Request())) captured.Add(item);
        });
        Assert.Equal("stream_incomplete", error.ErrorClass);
        Assert.Equal("Unfinished", Assert.Single(captured, item => item.Kind == LlmStreamEventKind.ThoughtSummaryDelta).Text);
        Assert.DoesNotContain(captured, item => item.Block?.Lifecycle is LlmStreamBlockLifecycle.Completed or LlmStreamBlockLifecycle.TextCompleted);
        Assert.DoesNotContain(captured, item => item.Kind == LlmStreamEventKind.Completed);
    }

    [Fact]
    public async Task Incomplete_part_and_identity_absence_are_observed_without_fabrication()
    {
        var events = await Capture([
            new { type = "response.reasoning_summary_text.delta", delta = "old fixture", sequence_number = "unknown" },
            new { type = "response.reasoning_summary_part.done", item_id = "r", output_index = 0, summary_index = 2, status = "incomplete", part = new { type = "summary_text", text = "partial" } },
            Terminal()]);
        var legacy = Assert.Single(events, item => item.Kind == LlmStreamEventKind.ThoughtSummaryDelta);
        Assert.Null(legacy.Source!.SequenceNumber); Assert.Null(legacy.Source.ItemId); Assert.Null(legacy.Source.SummaryIndex);
        Assert.True(Assert.Single(events, item => item.Kind == LlmStreamEventKind.ContentBlock).Block!.Incomplete);
        Assert.Equal(7, (int)LlmStreamEventKind.ContentBlock);
        var old = JsonSerializer.Serialize(new LlmStreamEvent(LlmStreamEventKind.TextDelta, "unchanged"));
        Assert.DoesNotContain("Source", old); Assert.DoesNotContain("Block", old);
        events.Last().Response?.NativeContinuation?.Dispose();
    }

    private static object Part(string type, string itemId, int output, int summary, string partType, string text, int sequence) => new
    { type, item_id = itemId, output_index = output, summary_index = summary, part = new { type = partType, text }, sequence_number = sequence };
    private static object Terminal() => new
    {
        type = "response.completed",
        sequence_number = 52,
        response = new
        { id = "response-fixture", status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Answer." } } } } }
    };
    private static LlmRequest Request() => new("fixture-model", [new(LlmMessageRole.User, "inspect")]);
    private static OpenAiResponsesLlmClient Client(HttpClient http) => new(new("fixture-key"), http);
    private static HttpClient ClientHttp(IEnumerable<object> events) => new(new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(item => "data: " + JsonSerializer.Serialize(item) + "\n\n")), Encoding.UTF8, "text/event-stream") })));
    private static async Task<List<LlmStreamEvent>> Capture(IEnumerable<object> events)
    {
        using var http = ClientHttp(events);
        var result = new List<LlmStreamEvent>();
        await foreach (var item in Client(http).StreamAsync(Request())) result.Add(item);
        return result;
    }
}

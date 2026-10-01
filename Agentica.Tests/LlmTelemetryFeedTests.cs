using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Tests;

public sealed class LlmTelemetryFeedTests
{
    [Fact]
    public async Task Ui_consumer_receives_progress_while_planner_is_still_generating()
    {
        using var feed = new LlmTelemetryFeed("fixture");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planner = new LlmWorkflowPlanner(new GatedClient(release.Task),
            new LlmPlannerOptions(ModelId: "fixture-model"), feed.Report);
        var planning = planner.CreatePlanAsync(new PlanningRequest(
            new RunRequest("Inspect state"), [], [], []));
        await using var reader = feed.ReadAllAsync().GetAsyncEnumerator();
        try
        {
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("started", reader.Current.Record.Kind);
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("activity", reader.Current.Record.Kind);
            Assert.False(planning.IsCompleted);
            release.SetResult();
            var plan = await planning.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("plan_live", plan.PlanId);
            feed.Complete();
            var delivered = new List<LlmTelemetryDelivery>();
            while (await reader.MoveNextAsync()) delivered.Add(reader.Current);
            var terminal = delivered[^1].Record;
            Assert.Equal("completed", terminal.Kind);
            Assert.Equal(12, terminal.Usage?.OutputTokens);
            Assert.NotNull(terminal.FirstOutputElapsedMs);
            Assert.Same(terminal, feed.LastTerminal);
            Assert.Equal(0, feed.DroppedRecords);
        }
        finally
        {
            release.TrySetResult();
            await planning.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Stalled_consumer_has_bounded_loss_and_latest_terminal_with_cumulative_progress()
    {
        using var feed = new LlmTelemetryFeed("fixture", capacity: 2);
        await Task.Run(() =>
        {
            feed.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
            for (var index = 0; index < 10_000; index++)
                feed.Report(new LlmStreamEvent(LlmStreamEventKind.TextDelta, "abc"));
            feed.Report(new LlmStreamEvent(LlmStreamEventKind.Completed,
                Response: new LlmResponse("fixture", "model", "",
                    FinishReason: LlmFinishReason.Stop)));
            feed.Complete();
        }).WaitAsync(TimeSpan.FromSeconds(5));

        var items = new List<LlmTelemetryDelivery>();
        await foreach (var item in feed.ReadAllAsync()) items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Equal(10_000, feed.DroppedRecords);
        Assert.All(items, item => Assert.Equal(10_000, item.DroppedRecords));
        Assert.Equal(30_000, items[^1].Record.OutputCharacters);
        Assert.Equal(10_002, items[^1].Record.Sequence);
        Assert.Equal("completed", items[^1].Record.Kind);
        Assert.Same(items[^1].Record, feed.Latest);
        Assert.Same(feed.Latest, feed.LastTerminal);
    }

    [Fact]
    public async Task Telemetry_omits_output_and_native_payload_and_bounds_opted_in_summary()
    {
        const string secret = "private-native-payload";
        using var continuation = new LlmNativeContinuation("fixture", "model",
            secret, "[{\"content\":\"private-native-payload\"}]");
        using var feed = new LlmTelemetryFeed("fixture");
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Activity, "untrusted text with spaces"));
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta, secret));
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Completed,
            Response: new LlmResponse("fixture", "model", secret,
                NativeContinuation: continuation)));
        feed.Complete();
        await foreach (var item in feed.ReadAllAsync())
        {
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(item), StringComparison.Ordinal);
            Assert.Null(item.Record.ThoughtSummaryDelta);
            if (item.Record.Kind == "activity") Assert.Equal("unclassified", item.Record.Activity);
        }
        Assert.Equal(secret.Length, feed.LastTerminal?.OutputCharacters);
        Assert.Equal(secret.Length, feed.LastTerminal?.ThoughtSummaryCharacters);

        using var summaries = new LlmTelemetryFeed("fixture", includeThoughtSummaries: true,
            maxSummaryCharacters: 4);
        summaries.Report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta, "abc😀more"));
        Assert.Equal("abc", summaries.Latest?.ThoughtSummaryDelta);
        Assert.True(summaries.Latest?.ThoughtSummaryTruncated);
        Assert.Equal(9, summaries.Latest?.ThoughtSummaryCharacters);
    }

    [Fact]
    public async Task Consumer_cancellation_allows_reconnection_and_does_not_close_feed()
    {
        using var feed = new LlmTelemetryFeed("fixture");
        using var cancelled = new CancellationTokenSource();
        await using (var reader = feed.ReadAllAsync(cancelled.Token).GetAsyncEnumerator())
        {
            var pending = reader.MoveNextAsync().AsTask();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Cancelled));
        feed.Complete();
        var kinds = new List<string>();
        await foreach (var item in feed.ReadAllAsync()) kinds.Add(item.Record.Kind);
        Assert.Equal(["started", "cancelled"], kinds);
    }

    [Fact]
    public async Task Closing_active_feed_does_not_manufacture_success_or_accept_more_updates()
    {
        using var feed = new LlmTelemetryFeed("fixture");
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        feed.Complete();
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Completed));
        Assert.Null(feed.LastTerminal);
        var items = new List<LlmTelemetryDelivery>();
        await foreach (var item in feed.ReadAllAsync()) items.Add(item);
        Assert.Equal("started", Assert.Single(items).Record.Kind);
    }

    [Fact]
    public async Task Terminal_snapshot_survives_queue_eviction_and_next_call_has_new_identity()
    {
        using var feed = new LlmTelemetryFeed("fixture", capacity: 1);
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Failed, "provider_timeout"));
        var failed = Assert.IsType<LlmTelemetryRecord>(feed.LastTerminal);
        feed.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        feed.Complete();
        var items = new List<LlmTelemetryDelivery>();
        await foreach (var item in feed.ReadAllAsync()) items.Add(item);
        var latest = Assert.Single(items).Record;
        Assert.NotEqual(failed.CallId, latest.CallId);
        Assert.Equal(1, latest.Sequence);
        Assert.Equal(0, latest.OutputCharacters);
        Assert.Same(failed, feed.LastTerminal);
        Assert.Equal(2, feed.DroppedRecords);
    }

    private sealed class GatedClient(Task release) : ILlmStreamingClient
    {
        public Task<LlmResponse> GenerateAsync(LlmRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamEvent(LlmStreamEventKind.Activity, "reasoning");
            await release.WaitAsync(cancellationToken);
            const string json = """
                {"planId":"plan_live","steps":[{"stepId":"inspect","toolId":"query_state",
                "kind":"Query","effect":"ReadOnly","input":{},"reason":"Inspect state"}]}
                """;
            yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, json);
            yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                Response: new LlmResponse("fixture", request.ModelId, json, json,
                    Usage: new LlmUsage(OutputTokens: 12), FinishReason: LlmFinishReason.Stop));
        }
    }
}

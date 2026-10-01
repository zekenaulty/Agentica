# Live provider telemetry

`LlmTelemetryFeed` connects `LlmWorkflowPlanner`'s streaming callback to an
asynchronous UI consumer. The callback performs bounded in-memory work and
never waits for the UI, a socket, or a console writer. Activity becomes readable
while generation is still running; a gated planner test verifies this timing.

```csharp
using var feed = new LlmTelemetryFeed("gemini", capacity: 64);
var planner = new LlmWorkflowPlanner(client, plannerOptions, feed.Report);
// Install planner in the host's AgenticaRunner as usual.

async Task<OutcomeEnvelope> RunAndCloseFeedAsync()
{
    try { return await runner.RunAsync(request, runCancellation); }
    finally { feed.Complete(); }
}

var run = RunAndCloseFeedAsync();
await foreach (var update in feed.ReadAllAsync(uiCancellation))
{
    await PublishToUiAsync(update, uiCancellation);
}
var outcome = await run;
```

The host owns `PublishToUiAsync`, UI disconnect handling, and observation of the
run task. Cancelling or failing the UI consumer does not cancel the provider or
runner. After disposing the old reader, a consumer can reconnect to the same
feed and inspect `Latest` and `LastTerminal`. Use one feed for each concurrently
active planner operation; sequential calls, including repair/refinement calls,
receive separate call IDs. Raw stream events have no concurrent call identity,
so several overlapping planner calls must not share one feed.

Each record contains a call ID, sequence, UTC timestamp, elapsed time, cumulative
output and thought-summary character counts, first-output time, normalized
activity, and terminal usage/finish reason. Visible output text, full responses,
native reasoning, encrypted content, and thought signatures never enter a
record. Provider thought summaries are optional and limited to 2,048 characters
per update by default. Truncation is explicit and preserves UTF-16 surrogate
pairs. Activity and failure fields accept bounded codes; arbitrary message text
becomes `unclassified`.

The default queue holds 64 records and can be configured from 1 to 4,096. When
full, it drops the oldest record. `DroppedRecords` is cumulative across the feed
and accompanies every delivered record; call sequence gaps and cumulative
counters let a UI show that progress updates were omitted. Summary fragments
must not be rendered as an uninterrupted transcript after loss or truncation.
`Latest` and `LastTerminal` retain one bounded snapshot each, independently of
queue eviction. Earlier call terminals may be lost as newer calls finish; the
feed is an operator view, while run outcomes and receipts provide execution
evidence. A host needing every event must provide a separate durable sink with
its own retention policy.

Call `Complete` in the producer's `finally` block. It closes the feed after
queued records drain and ignores subsequent callbacks. Closing an unfinished
feed does not invent a provider completion. Disposal also closes the feed.
Exactly one asynchronous reader may be active at a time.

The Lab's existing human/JSONL console reporter remains available. Hosts that
send telemetry to a UI can use this feed directly instead of running display
I/O inside the stream callback.

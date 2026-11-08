# Readable provider stream metadata v1

This additive client contract separates capture granularity from reading
granularity. It adds observed coordinates and content lifecycle to
`LlmStreamEvent`. All old event-kind values (0–6), delta `Text` strings, and
`LlmResponse.ThoughtSummaries` entries retain their existing meaning. This
delivery does not aggregate or remove those legacy fragments.

## Exact API

```csharp
// New nullable init properties on the existing event:
LlmStreamSource? Source;
LlmStreamBlock? Block;

public sealed record LlmStreamSource(string Provider, string EventType,
    long? SequenceNumber = null, string? ItemId = null,
    int? OutputIndex = null, int? ContentIndex = null, int? SummaryIndex = null)
{
    public int Version { get; init; } = 1;
}

public enum LlmStreamBlockKind { OutputText = 0, ReasoningSummary = 1 }
public enum LlmStreamBlockLifecycle
{ Started = 0, Delta = 1, TextCompleted = 2, Completed = 3 }

public sealed record LlmStreamBlock(LlmStreamBlockKind Kind,
    LlmStreamBlockLifecycle Lifecycle, string? TextSnapshot = null,
    bool? Incomplete = null);

// Appended enum member; existing numeric values do not change:
LlmStreamEventKind.ContentBlock = 7;
```

`Source` and `Block` are omitted from ordinary JSON when null. Consumers of
serialized metadata must check `Source.Version`; missing/unknown metadata can
still carry a legacy exact delta, without qualified native block semantics.
The contract adds no new provider request, credential, persistence requirement,
tool executor or paid summary call. The host owns call/attempt identity, scope,
storage, user-facing field selection and acceptance.

Native blocks identify content and lifecycle, not semantic milestones. A
paragraph break is formatting. Useful progress touchpoints require actual
authored approach/finding/change-of-direction content or observed operation,
result and recovery transitions. One operation may span calls/retries, and one
call may contain multiple useful passages. Hosts own that correlation. The
Lab reading view counts summary sections and observed operations; it has no
automatic semantic progress detector. Identical logical evidence split into
one or 1,000 deltas must produce the same completed passages and operation state.

**OutputText is provider output text.** It can be a structured plan or JSON,
as well as prose. Only the host can select content for a user-facing answer.
Block completion does not establish provider call success, tool execution,
host acceptance, delivery, or objective fulfillment.

## OpenAI Responses mapping

| Actual source event | Existing/new event | Block lifecycle | Snapshot |
| --- | --- | --- | --- |
| `response.reasoning_summary_part.added` | ContentBlock | Started | `part.text` |
| `response.reasoning_summary_text.delta` | ThoughtSummaryDelta, unchanged Text | Delta | none |
| `response.reasoning_summary_text.done` | ContentBlock | TextCompleted | `text` |
| `response.reasoning_summary_part.done` | ContentBlock | Completed | `part.text` |
| `response.content_part.added` with output_text | ContentBlock | Started | `part.text` |
| `response.output_text.delta` | TextDelta, unchanged Text | Delta | none |
| `response.output_text.done` | ContentBlock | TextCompleted | `text` |
| `response.content_part.done` with output_text | ContentBlock | Completed | `part.text` |

Boundary events have null event `Text`. `TextSnapshot` is a distinct full
provider snapshot: **never append it as another delta**. Keep the exact
concatenated chunks and snapshot separately. If they differ, the reading view
may use the actual snapshot while declaring the fragment coverage difference.
Do not deduplicate repeated words or insert spaces between fragments.

Coordinates are copied only when observed. A block is addressed within a host
call/attempt by provider/item/output index and its summary/content index.
Never merge two summary indexes simply because they share an item. Parallel
blocks retain their identities and source ordering. `Incomplete=true` reflects
an actual incomplete native part; absent status remains null. An unmatched
start remains unmatched after interruption. Hosts can mark an interrupted
reading projection using the failed/cancelled attempt without inventing a
native end event.

`SequenceNumber` is the provider ordinal, distinct from a host trace cursor.
Only some provider events produce stream events, so gaps among these ordinals
alone do not establish capture loss. Retention gaps and dropped events remain
host facts. Existing completion/exception/attempt-evidence semantics remain;
the adapter still requires actual valid terminal completion.

Existing observed web-search Activity/WebSearch evidence also carries Source
coordinates where supplied. Search evidence identifies an observed provider
operation, not execution of an arbitrary host tool. Other adapters continue
to emit their existing events with null metadata until their native mapping
is qualified. The xAI Responses route shares this parser when those source
events are actually supplied; no xAI live-capability claim is made here.

Primary schema checked November 8, 2026:
[OpenAI Responses streaming events](https://developers.openai.com/api/reference/resources/responses/streaming-events).

## Lab first reading slice

Lab preserves its existing sanitized provider source records, normalized
fragments, raw diagnostic endpoints and storage policy. Existing normalized
summary/text events gain source coordinates; planner stream diagnostics gain
the additive Source/Block. There is no destructive trace migration.

`chat-work.mjs` creates a read-only projection of loaded retained evidence:
native summary parts, labelled legacy call reconstruction, actual receipt
messages and observed search state. Whole-item snapshots do not duplicate
streamed text. Empty summaries add no scaffold. Unknown metadata versions
remain reconstructed legacy fragments. Raw structured planner output and
opaque replay material do not become user-facing prose.

The conversation shows compact live activity, optional exact summary excerpt,
and a collapsed Work disclosure. Its paragraphs and real operation results
stay available alongside the answer. Diagnostics remains independently
collapsed. Completion does not close an inspection the reader opened.
Selected summary/answer text is preserved when more text arrives; an explicit
show-latest control reveals pending display updates. Canonical output and raw
capture continue while a display update is deferred.

The compact current excerpt shows recent text, bounded to 220 Unicode code
points plus an omission marker. It preserves surrogate pairs and uses native
grapheme segmentation when available to avoid beginning inside a cluster. It
does not freeze at the beginning of a long growing part. Full retained text is
unchanged. Interleaved blocks retain their original reading order, while the
compact excerpt follows the block with the latest observed update sequence.
Snapshot comparisons check content, including equal-length
disagreements. Lab currently compares loaded captured text with a complete
available snapshot; it has no separate semantic-block page/snapshot-slice
contract to change.

The Work reader starts retained inspection at the beginning and then advances
the existing cursor on each **Load more retained work** action. Sequence
coverage and payload availability are separate: large work records stored
behind payload references have explicit load controls and coverage warnings.
Loads use the existing turn-scoped payload endpoint and shared eight-payload
display cache; opening Work does not eagerly fetch them. Failed reads remain
retryable. Loaded payloads are projected without rewriting the trace envelope,
and snapshots remain separate from deltas. Records exceeding per-event
retention are labelled unavailable rather than reconstructed from JSON previews.

**Bounded first slice:** the reading view covers the currently loaded trace
window and clearly labels omitted pages, incomplete blocks, snapshot mismatch,
and service drops. The existing 4,000-event/8 MiB per-turn retention policy and
per-event ceilings remain. This is not a claim of unlimited retention. A compact
durable work projection with pagination by semantic block remains follow-up
work; this slice does not claim long-run reading completeness from one tail
page. Read/reopen and display do not dispatch another model call. Harness
answers retain their existing host-accepted artifact path; this slice does not
stream arbitrary planner JSON as an answer or add partial structured-field
parsing for future hosts.

## Qualification

- `ReadableStreamTests`: actual SSE parser, exact forty-fragment concatenation,
  indexes, simultaneous blocks, independent text/part snapshots, native
  incomplete state, old fields and interrupted/open blocks.
- `OpenAiWebSearchTests`: observed search coordinates alongside existing evidence.
- Lab executor/persistence tests: native coordinates, original fragment values,
  source capture, paged durable reopen and cross-conversation rejection.
- `chat-ui.test.mjs`: interleaving/replay, legacy reconstruction, snapshot gaps,
  real receipt versus narration, absent summaries and unknown version handling.
- `ui-readable-work.mjs`: intercepted real browser reading journey, selection,
  progressive host-owned answer, tool result, inspection, completion, reload
  and narrow viewport. `ui-experiment-markdown.mjs` retains prior Markdown,
  citation, disclosure and reconnect checks. Neither uses a live provider.
- `ui-work-reader-regressions.mjs`: interleaved current preview, three-page
  cursor progression, separate large payloads, failed-read retry and explicit
  per-event retention loss. All three audited regressions fail before the
  corrections and pass afterward against intercepted Edge browser fixtures.

These are producer/offline/browser evidence. Live emissions, installed Bridge
behavior and the owner's judgment of usefulness remain separate acceptance.

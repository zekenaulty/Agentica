# Shared OpenAI native web search

P06 promotes the Lab's existing native search behavior into `Agentica.Clients`.
Lab storage, conversation settings, scoped tool registration, budgets and UI remain
host concerns. The shared client does not grant tools or accept external claims.

## Request and evidence

`LlmRequest.WebSearch` is null by default. Null sends no native search tool,
choice or call limit. `LlmWebSearchOptions` enables `Auto` ambient selection or
`Required` explicit search, with the existing 1–20 call ceiling (default 3).
The OpenAI request uses `web_search`, `tool_choice`, `max_tool_calls` and
`include: ["web_search_call.action.sources"]`. Other adapter routes reject this
option before dispatch rather than silently ignoring it or substituting a tool.

`OpenAiWebSearch.CreateRequest(modelId, query, generationOptions, maxToolCalls,
attemptCapture)` builds a required search request with exactly one user query.
It has no history, system/developer instructions, structured-output policy or
native continuation. Hosts pass the selected model/effort/output controls and own
the cumulative allowance across subrequests. A failed or unresolved reservation
is not automatically released by this helper.

Search lifecycle events appear as ordinary `Activity` events carrying typed
`WebSearch` snapshots. `LlmResponse.WebSearch`, `LlmClientException.WebSearch` and
`LlmAttemptCapture.LatestWebSearch` retain detached external evidence. Supply an
attempt capture to read it after ordinary cancellation or early disposal. The
structural `LlmAttemptEvidence` contains no search query, source title or URL.
UI observers remain best effort; the capture is independent of their delivery.

The evidence includes observed call IDs/status/action, optional provider query
and query lists, safe sources and citation anchors. Missing queries remain absent;
the client never invents one from user text. Calls, query lists, strings, sources
and annotations are bounded; truncation is explicit. `CompletedCalls` is the
count of observed completed native calls, not a billing amount or host acceptance.
Required search rejects missing/incomplete search; any response exceeding the
requested call ceiling rejects delivery while retaining its evidence and usage.

All source content is `external-untrusted`. Source URLs must be bounded absolute
HTTP(S), contain no control characters, and have no user information. Unsafe URLs
are omitted from the public reference projection. Native continuation remains
private exact provider history; it does not execute a URL or authorize later
search. The follow-up still requires its own explicit search option.

## Stateless private reasoning

`OpenAiResponsesClientOptions.IncludeEncryptedReasoning` is an additive init-only
option, true by default. It requests `reasoning.encrypted_content` alongside search
source expansion. Both include values survive composition. The existing three
positional options and deconstruction are unchanged. `store: false` and omission
of `previous_response_id` remain fixed; exact private replay travels in the bounded
native carrier and explicit sensitive codec, never normal JSON or display.

An explicit false omits the encrypted request. If a returned reasoning item has no
encrypted state, the adapter reports `openai.continuation.available: False` and
returns no native carrier. No reasoning replay is synthesized. The actual serialized
include affects the configuration fingerprint, so callers can distinguish the
opt-out. Existing xAI encrypted-request behavior remains unchanged.

## Citations and unchanged text

The client preserves canonical answer text and Markdown exactly. Citation records
retain provider `start_index`/`end_index`, output and content indices, and optional
UTF16 offsets into that unchanged concatenated text. Serialization/reload retains
these anchors. Completed message-item annotations are retained even if the later
response fails; a whole-answer offset remains unknown until earlier text is known.

The [official web-search guide](https://developers.openai.com/api/docs/guides/tools-web-search)
describes character positions but does not establish Unicode scalar versus UTF16
indexing. `CitationIndexUnit` therefore makes that projection explicit:

- `ProviderCharacters` preserves native indices and projects unambiguous BMP text.
  Surrogate-containing text retains raw positions and clickable sources without
  fabricating inline coordinates.
- `Utf16CodeUnits` uses validated UTF16 boundaries.
- `UnicodeScalars` converts scalar indices to UTF16 with surrogate-aware traversal.

Invalid, reversed or out-of-range positions remain raw evidence and receive no
usable canonical range. Hosts must render visible clickable citations, with source
links as fallback when inline positions cannot be projected. HTML/Markdown escaping
and host output retention policy still apply to external titles and query text.

## Qualification boundary

Protocol fixtures cover default-off envelopes, ambient/required choice and limits,
live activity, missing/native queries, refusal/incomplete/late failure/cancellation,
unsafe URLs, independent explicit query construction, immutable bounded snapshots,
native replay, multi-part citation offsets, surrogate conversion and JSON reload.
They make zero live calls. P03 supplies required host admission; P07 moves existing
Lab ambient and explicit paths onto these contracts and verifies browser/storage
preservation. Other provider-native search features remain outside this scope.

# Lab experimental chat

The Lab owns this experiment surface. Agentica core has no conversation database, persona, chat UI or vendor conversation state added by this feature.

## Two explicit conditions

- **Clean Room**, the default: direct OpenAI Responses with `store:false`, only this conversation's selected complete user/assistant transcript and the current message. A zero-length instruction editor omits `instructions`; nonempty text is sent exactly. No planner schema, persona, tool manifest, memory lookup or cross-conversation context is injected.
- **Harness**, opt-in: the existing `AgenticaRunner`, `LabPlannerFactory`, planning frames, schema/validation, tools, observations, refinements, continuation and evidence-based response completion. The injected planning condition and effective requests are inspectable. A response artifact establishes completion of this chat turn, not an arbitrary external objective.

Open `/chat.html` from the Lab dashboard. Create, rename and revisit saved conversations. Model, reasoning effort, summary request, exact instructions, context controls and tool bindings are saved per conversation and snapshotted per turn. A new conversation always starts with clean defaults; another conversation's Harness configuration is not inherited.

The model selector groups account-visible GPT models from 5.6 onward by version and Terra, Luna, Sol or Astra family. It includes the eight current catalog IDs, preserves exact dated/custom IDs, and marks a documentation fallback as access-unchecked when account listing fails. Listing models does not perform inference or establish every model/effort/tool combination.

## Ambient and explicit web search

Both paths use [OpenAI Responses web search](https://developers.openai.com/api/docs/guides/tools-web-search), the selected model and the existing server-side credential. Search is off in new conversations.

- **Ambient web search**, enabled per Clean Room conversation: binds native `web_search` with `tool_choice:auto`. The model may search while answering; it may also decide no search is needed. This adds no instruction overlay and preserves the exact instruction editor. The ordinary selected conversation context remains in the response request.
- **`web.search`**, explicitly bound in Harness: the actual Agentica planner chooses a public query, the tool makes a separate streaming request with `tool_choice:required`, and structured untrusted results return to the planner before refinement/completion. That request carries only the explicit query, with no conversation history or custom instructions. The result is not streamed into the assistant transcript as a second answer.

Ambient search initially applies to direct Clean Room responses; Harness uses the explicit tool. A bound tool grants its declared use without a second approval prompt. Searches can incur provider tool charges. Model/account combinations remain provider-validated; there is no fallback model or hidden retry.

`MaxWebSearchCalls` (default 3, range 1–20) bounds native built-in search calls across a turn, using request `max_tool_calls` and a cumulative Harness allowance. Search subrequests also pass the parent provider-call, input and deadline guards. An incomplete search retains its unresolved allowance rather than treating unknown usage as zero. Source and citation lists are bounded and flag truncation.

The trace retains native search actions, call identities, citations, sources and reported usage. Assistant citations with unambiguous ASCII offsets are clickable inline; Unicode or invalid offsets retain source links without guessing placement. Explicit tool citations stay with their tool source evidence because their character offsets refer to the search answer. URLs are limited to HTTP(S). Referenced web content is untrusted external evidence, and a citation establishes attribution rather than truth. Search is provider-assisted retrieval, not a background browsing service.

## Ownership and reuse map

| Owner | Responsibility |
| --- | --- |
| Lab Web `Chat/ExperimentStore` | SQLite schema/version, conversation configuration and transcript, partial/terminal turn evidence, source payloads and normalized trace |
| Lab Web context compiler | Scope-before-budget selection of this conversation's complete dialogue, explicit omission manifest, no memory binding |
| Lab Web executor/transport | Exact direct Responses mapping; observed HTTP for direct and planner calls; call/input admission before dispatch; normalized events and allowed native records |
| Existing Agentica core | Harness planning, deterministic validation, tool/effect policy, canonical evidence, refinement, continuation, completion |
| Existing provider configuration | Server-only credential reuse; model/effort defaults for new conversations; existing provider settings UI |
| Nyx Lab, read-only donor | Trace partitioning, durable partial capture, paging, context-depth and investigation/evidence patterns |

Source cuts: Agentica base `977d0cab5421fd4e09fd33f1224f241b7e425abe`; Bridge donor `187b203c45d316e8f166dcb1cbd239e5a564a321`. [Lumina's November 2 implementation brief](https://docs.google.com/document/d/1artpVE9-1tgRnXMvgURW03HMB4nfZHg-eV6thZLBXQE/edit) supplies the product reconciliation. Current Nyx, Bridge, Cloud, external-host protocol and existing Lab service/storage are preserved.

## Persistence and continuation

Data lives under `<Agentica:StorageDirectory>/chat/experiments.sqlite3`, with conversation scratch files under `chat/workspaces/<conversation-id>/`. SQLite schema version 1 uses WAL. Turn admission and its conversation update commit together; settings use revision conflict checks. A per-store exclusive service lease prevents two processes from dispatching the same chat store concurrently.

Repeated turn POSTs with the same request ID/content return the original turn and do not invoke the provider again. A different body under the same identity is refused. On process restart, a running turn becomes interrupted with partial output/evidence retained and usage explicitly unknown where not returned. It is never automatically retried.

Between turns/restarts, replay is **visible transcript only**. Opaque provider reasoning is not persisted or replayed. Every turn labels this limit. Model/effort/instruction changes are configuration revisions, not claims of equivalent native reasoning continuity. Within one Harness run, the existing planner session may retain compatible native state. Model changes cannot transfer opaque state from an earlier turn.

## Trace and inspection

Per-turn SSE replays durable, ordered events using `Last-Event-ID`/`after`. UI disconnect does not cancel a turn; Stop is explicit. Transcript output and its event cursor are published under one store lock to prevent snapshot/reconnect duplication.

Two diagnostic layers remain distinguishable:

- `provider.record`: allowed original native event fields, including type, call/item identity and indices, stored in a separate retained payload with a source reference. Private reasoning content, encrypted carriers and credentials are removed. These records are diagnostic evidence, not a provider continuation carrier.
- Normalized summaries, output, plan/refinement, tool/effect/evidence, context and outcome events: display chronology. Never reconstruct native reasoning or replay state from these views.

The inspector links exact sanitized outgoing JSON, SHA256 of the actual request bytes, context selection, provider source records, tool evidence and usage. No HTTP authorization header is captured. Summary absence is shown as not emitted/retained. Measured token fields remain separate from conservative context estimates; no price estimate is fabricated.

Trace limits: 4000 ordinary events and 8 MiB retained JSON per turn; terminal evidence remains visible after quota exhaustion. Source/event payloads have a 1 MiB ceiling; loss/truncation is explicit. Read pages are bounded to 100 events and approximately 32000 characters. Large payloads load separately. The browser keeps a bounded 1000-event window with older durable pages accessible. Conversation/turn/output ceilings are explicit in validation.

## Context controls

Reasoning effort, context-depth preset, model window/output reservation, history selection and Harness execution limits are separate. Depth presets change editable history/character ceilings. `stop` refuses overflow before dispatch; `recent` deliberately removes oldest complete pairs and records omitted IDs. No second model call silently summarizes history. Wire admission includes instructions and schemas, not just visible message length.

Context estimates are conservative UTF-8 request proxies, not measured provider token counts. Tool results have individual limits, while the Harness evidence planner enforces a cumulative receipt/observation projection limit without deleting canonical runtime proof. The current refinement observation must fit or the run stops truthfully.

Direct Clean Room history selection and dispatch size the same complete serialized Responses envelope, including optional instructions, reasoning and search metadata. Recent-history omission removes complete older pairs until that actual envelope fits. If the current input alone cannot fit, `context.limit` reports the failed gate, request size, output/safety reservations and omissions; it does not imply a settings revision conflict. A rejected send retains its draft and request identity for an explicit retry after configuration changes.

Memory is disabled in settings validation, context compilation and offered capabilities. History is durable chat data; it is not admitted memory. No future memory adapter is mounted by this release.

## Opt-in capability palette

All tools require Harness mode and an explicit conversation binding. The same core `ToolRegistration` carries stable ID, version/provenance, schema, effect and security declaration for a future MCP projection:

- `utility.clock` — UTC or selected time zone.
- `utility.calculate` — bounded decimal arithmetic, no code evaluation.
- `utility.text.inspect`, `utility.json.inspect` — explicit supplied values with paging and precise size reporting.
- `conversation.read`, `conversation.search` — only the current turn's supplied transcript; omitted stored history is not secretly retrieved.
- `scratch.list`, `scratch.read`, `scratch.write` — this conversation's isolated text scratch namespace. Writes create new files; no overwrite, shell, active checkout or other-conversation access.
- `web.search` — query-only OpenAI web research through the observed provider path, with citations, sources and explicit external-call accounting.

The terminal response tool is an internal Harness completion capability. Binding a utility grants its declared use within that scope; no repeated approval prompt is invented. Unsafe writes are not automatically retried. Filesystem checks constrain application paths and links; they are not an OS sandbox against a malicious same-user native process racing filesystem changes.

Nyx has no generic portable shell/files/calculator/fetch collection to copy. Its participant search, Google grounding/sidecar, skill draft/library, media and background inquiry are distinct integrations. Those are not silently mounted here; arbitrary HTTP fetch, media and MCP hosting remain future opt-in adapters.

## API

- `GET/POST /api/chats`; `GET/PATCH /api/chats/{id}`.
- `PUT /api/chats/{id}/settings` with `expectedRevision`.
- `POST /api/chats/{id}/turns` with content and optional stable `requestId`.
- `GET .../turns/{turnId}`, `/events`, `/trace`, `/events/{sequence}/payload`; `POST .../cancel`.
- `GET .../turns/{turnId}/citations` pages retained citation events for replay without loading native trace records; payload references remain conversation-scoped.
- `GET /api/chats/capabilities`, `/api/chats/models`.

Chat APIs reject browser requests from an unrelated Origin; the existing external-host CORS contract remains unchanged. This is a loopback operator Lab, not a multi-user authenticated service. The model list uses the account's available IDs when configured, with manual IDs still supported. Effort availability is provider/model-specific; unsupported combinations return a bounded, redacted provider error with no hidden downgrade.

## Verification

See [qualification](implementation/lab-experimental-chat-20261102/qualification.md) for commands, actual results, original failures and evidence limits. The optional browser fixture is explicitly credential-free:

```text
dotnet run --project samples/Agentica.Lab.Web.FixtureHost -- --fixture-endpoint http://127.0.0.1:1/ --experiment-fixture --urls http://127.0.0.1:5090 --contentRoot <absolute-Agentica.Lab.Web-directory> --Agentica:StorageDirectory <disposable-directory>
```

`--experiment-fixture` supplies synthetic turn events; it is not an actual provider or Agentica Harness proof. Provider HTTP fixtures separately exercise real transport and the actual Harness query/result/refinement/completion path. Runtime memory integration, native cross-restart reasoning preservation, comparisons/forking and live-provider/model qualification remain separate work.

# MCP and streaming providers: first runtime slices

These slices add a host-bound MCP client tool, streamed Gemini Developer API, OpenAI Responses, Anthropic Messages, and xAI Grok Responses clients, and native Ollama streaming. Agentica's runtime stays independent of provider and MCP SDK types.

The planner's provider-neutral stream observer now receives `Started` before network enumeration, live activity/text/summary deltas, and one terminal completion, failure, or cancellation signal while the observer is healthy. A recoverable observer exception disables that observer for the call without changing the provider result. Lab measures from call start, so first-output timing includes connection and provider wait time. A failed or cancelled stream remains a failed planner call even if earlier deltas were shown. Provider-native signatures, encrypted reasoning, and raw Ollama thinking remain outside generic stream events.

## Streamed Gemini planning

Set `GEMINI_API_KEY` (or `GOOGLE_API_KEY`), then use Lab `run` or `chat` with `--planner gemini`. Lab selects Interactions by default for Developer API credentials; `AGENTICA_GEMINI_API=legacy` selects the older GenerateContent client. Vertex mode continues to select that SDK path by default, and `AGENTICA_GEMINI_API=interactions` explicitly selects the Developer API. The Interactions client posts `store=false` and `stream=true`. The host supplies full bounded input for each independent request. Lab reports interaction activity, visible text deltas, provider supplied thought summaries when requested, and terminal token usage to stderr while the call is in progress. The planner accepts output only after a completed interaction event.

The streaming client can return a private native continuation from complete terminal model steps or reconstruct supported thought and model-output steps from `step.start`, `step.delta`, and `step.stop` events. A follow-up request replays those steps with the next user input under the same model and system instruction while keeping `store=false`. Signed thought steps retain their signature; signatures are absent from ordinary response/request serialization, planner text, and receipts. Missing signed steps, incomplete stream steps, and unknown delta types withhold continuation. Provider function calls are rejected. Numeric thinking budgets and temperature remain unsupported. Legacy Gemini generation remains available and refuses assistant/tool history rather than flattening it without signatures.

On 2026-09-30, a live Gemini 2.5 Flash smoke call showed that `interaction.completed` omitted `steps` while streamed `thought_signature` and model-output deltas were present. After implementing step reconstruction, two consecutive Developer API calls with `store=false` both completed and produced private native continuations. Only event counts, usage, completion, and continuation availability were printed; response text and signatures were not logged. This verifies the text/thought path for that model, not function-call or multimodal replay.

## OpenAI Responses streaming

Set `OPENAI_API_KEY`, then use Lab `run` or `chat` with `--planner openai` and optionally `--model <model-id>`. The default model is `gpt-4.1`; choose a reasoning-capable model when requesting thought summaries. The client posts `store=false` and `stream=true` to the Responses API. Lab reports activity, output timing/size, provider-supplied reasoning summaries when requested, and terminal usage. Only `response.completed` with `status=completed` is accepted. Truncated, failed, incomplete, refused, or unexpected native tool-call responses do not become plans.

For a stateless follow-up, the adapter privately retains the complete native output array, including encrypted reasoning items, after the original user input. The follow-up replays those items with one new user message under the same provider, model, and instructions. If a reasoning item lacks `encrypted_content`, continuation is withheld. The opaque payload is excluded from ordinary request/response serialization, logs, planner text, and receipts. The adapter's JSON mode is an output hint; Agentica's planner still validates the result against its own workflow contract. Numeric thinking budgets, temperature, and native provider tool calls are not mapped by this slice. Contract tests cover replay, stream completion, and binding failures; no OpenAI credential was available for a live provider test.

## Anthropic Messages streaming

Set `ANTHROPIC_API_KEY`, then use Lab `run` or `chat` with `--planner anthropic` and optionally `--model <model-id>`. The default model is `claude-sonnet-4-6`. The adapter reconstructs text, thinking, and redacted-thinking content blocks from SSE, reporting text and provider-supplied thinking summaries as they arrive. It accepts output only after `message_stop`, complete content blocks, and an `end_turn` or `max_tokens` stop reason. A `max_tokens` result has no continuation and is identified as truncated.

For a follow-up, the private continuation holds the previous user turn and complete assistant blocks, including opaque thinking signatures and redacted-thinking data. A missing signature withholds continuation. Provider tool-use blocks are rejected until their native execution cycle has a governed contract. The adapter maps dynamic thinking to adaptive mode and explicit numeric budgets to manual mode with the provider's minimum and output-limit checks. It validates JSON schema syntax but does not send Agentica's workflow schema as a provider constraint because its free-form tool-input objects exceed the provider's constrained schema subset; Agentica still validates the plan. Contract tests pass, but no Anthropic credential was available for a live provider test.

## xAI Grok Responses streaming

Set `XAI_API_KEY`, then use Lab `run` or `chat` with `--planner grok` and optionally `--model <model-id>`. The default model is `grok-4.7`. The adapter shares the bounded Responses SSE parser with OpenAI, but has its own endpoint, credential, provider identity, and continuation binding. It sends `store=false`, `stream=true`, and `include=["reasoning.encrypted_content"]`. The stream reports visible text and timing; raw reasoning text is represented only by a non-content activity event. The native output array, including encrypted reasoning, is privately replayed on a same-provider follow-up. Contract tests cover ciphertext replay and cross-provider rejection; no xAI credential was available for a live provider test.

## Host-approved MCP tool

The Lab enables one read-only MCP tool when `AGENTICA_MCP_ENDPOINT` is set. Supply all of these host-owned values:

```text
AGENTICA_MCP_ENDPOINT=https://example.org/mcp
AGENTICA_MCP_SERVER_ID=example
AGENTICA_MCP_TOOL_NAME=search
AGENTICA_MCP_TOOL_SHA256=<canonical input schema SHA-256>
AGENTICA_MCP_TOOL_ID=mcp.example.search
AGENTICA_MCP_TOOL_DISPLAY_NAME=Approved Search
AGENTICA_MCP_TOOL_DESCRIPTION=Search the approved source
```

`McpSchemaFingerprint.Sha256(schemaJson)` computes the canonical schema hash from a reviewed tool schema. The endpoint must be HTTPS or loopback HTTP. The host must trust the server and classify the named operation as read-only; MCP annotations from the server are only hints. The adapter lists the server's tools, requires exactly one matching name and the pinned schema, and rechecks those conditions immediately before every call. Only the locally configured tool is added to the planner catalog. Lab `run` uses that tool catalog when configured; `chat` adds it to its existing local tools.

Run `Agentica.Lab mcp-inspect <endpoint> <server-id>` to list remote tool names, input schemas, and canonical hashes for review before pinning one in the host configuration. Discovery alone never registers a tool.

The SDK transport uses Streamable HTTP with optional host-supplied bearer authentication. Result content currently accepts text blocks and optional structured JSON, with a 65,536-character default limit per channel. Unsupported binary/image/resource blocks fail. The receipt carries server/tool/schema identities, a content digest, and success/error status. The observation carries the remote result as untrusted data. The MCP server's actual behavior must still be checked by the host; this adapter cannot prove that a remote operation is read-only.

A loopback integration test now exercises the actual MCP SDK HTTP session through protocol discovery fallback, initialization, tool listing, pinned binding, authenticated tool call, and result receipt. This verifies the adapter path with a local fixture server; it does not establish the behavior of a third-party server.

For a protected Streamable HTTP server, set `AGENTICA_MCP_BEARER_TOKEN` in the host environment. The token is sent as an Authorization header and is never included in planner descriptions or receipts. The endpoint remains HTTPS or loopback HTTP.

For several host-bound tools on one server, set `AGENTICA_MCP_BINDINGS_FILE` to a local JSON manifest instead of the single-tool environment settings. This file is an authority-bearing host configuration; review it before installing it. Example:

```json
{
  "serverId": "project-services",
  "endpoint": "https://example.org/mcp",
  "tools": [
    {
      "remoteName": "search",
      "schemaSha256": "<canonical pinned SHA-256>",
      "toolId": "mcp.project.search",
      "displayName": "Project Search",
      "description": "Search authorized project records",
      "kind": "Query",
      "effect": "ReadOnly",
      "reads": ["UserContent"],
      "exposesToPlanner": ["ExternalUntrusted"],
      "externalOutput": "Mixed",
      "approvalRequirement": "None",
      "retrySafety": "Idempotent",
      "maxResultCharacters": 65536
    }
  ]
}
```

Each entry declares its local `kind`, `effect`, `reads`, `exposesToPlanner`, `externalOutput`, `approvalRequirement`, and `retrySafety`. The host may bind an `Action` with `ExternalSideEffect` or `WritesLocalState`; `ApprovalRequirement=None` means the installed binding is the standing grant, while `ExplicitGrant` requires a separate per-invocation grant. The manifest cannot bind unknown effects or retry semantics. Lab allows only the effect classes actually present in its installed MCP bindings, in addition to its normal local effects. Discovery without a manifest entry remains inert. This is a tool registration contract, not a claim that the remote server faithfully performs its advertised effect.

## Native Ollama streaming

Lab `run` and `chat` accept `--planner ollama` with `--model <installed-model>` or `OLLAMA_MODEL`. `AGENTICA_OLLAMA_ENDPOINT` may set the full `/api/chat` URL; otherwise `OLLAMA_HOST` or local loopback is used. The native adapter consumes Ollama's streamed newline-delimited JSON, reports live output and thinking activity, and waits for a terminal `done` record before accepting a response. Raw `message.thinking` stays out of planner text and receipts. A completed text turn can be replayed privately as native assistant `content` plus `thinking` under the same model and system instruction; this is bounded local transcript replay, not a signed thought proof. Direct assistant/tool history and numeric thinking budgets remain rejected. Provider tool calls are not treated as a completed text plan.

## Host binding and authority

The host's deliberate installation of a tool into an active execution surface is a standing, scoped grant for that capability. Agentica no longer infers an extra one-shot approval from `ExternalSideEffect` alone. An installed declaration may still explicitly require a per-invocation grant; effect policy, schema validation, manifest identity, and host resource fences continue to constrain dispatch. Mere MCP discovery does not install a tool. The originating objective and valid delegated descendants supply purpose; installation alone does not authorize an unrelated objective. Scoped task orchestration can ask the host to derive a distinct child scope with a receipt before child dispatch. See `host-binding-authority.md` for the bounded-context contract and host-owned proof boundary.

## Bounded planning input

The workflow planner now compiles each provider-facing initial or refinement request under `LlmPlannerOptions.MaxInputCharacters` (default 131,072 characters). It keeps the objective, host request context, projected frames, planning constraints, tool catalog, and newest observation and receipt. If the assembled input exceeds the ceiling, it removes older observations and receipts, larger oldest entry first. Each original evidence item gets an include/omit decision in an in-memory compilation receipt, alongside the exact input character count and SHA-256. The request metadata includes the hash and omission counts without copying omitted content. When mandatory material cannot fit, compilation fails before provider dispatch. Repair turns reserve room for quoted invalid output and fail if their assembled messages still exceed the ceiling.

The character ceiling remains an exact guard. A host may also set `LlmPlannerOptions.ContextWindowBudget` with the model's declared window and separate reserves for output, reasoning, tool results, and a safety margin. The compiler trims optional older evidence until both ceilings fit, and fails before dispatch when mandatory context or a repair turn exceeds either ceiling. Library hosts can supply an `ILlmInputTokenEstimator` for their provider and model. The default estimator counts UTF-8 bytes in the logical request plus a fixed overhead; it is an approximation, not a provider tokenizer or a guarantee against provider-side overflow. The in-memory receipt and request metadata name the estimator and record its estimated input and allowance. Lab `run` and `chat` accept `--max-input-characters <count>` (minimum 8192) and `--context-window-tokens <count>`; the latter uses the configured output limit plus 2048 tokens each for tool results and safety margin. Provider-native continuation has a separate size/lifecycle concern.

The compiler does not yet choose representation depth for frame entries, inspect provider-specific serialized wire requests, or reconcile provider-reported input usage against its estimate. Hosts should configure the actual model window and conservative reserves; an incorrect declared window or estimator can still cause a provider rejection.

The source review informing this boundary used the [Domain of Domains architectural thesis](https://docs.google.com/document/d/1_QI28ViQ0MFmf0HLvCNTfZOmmTbzuH9EXuZZhYrfEq4), [Domain Context Allocation](https://docs.google.com/document/d/1dS1dq_C5a4vVy2GukkokSJh1mGPE2HaIyMjv0DcgOOE), [Nyx cognition loops](https://docs.google.com/document/d/1gFNO5HjeNbx4TH2k10aywJm1UsK5Z8X57syl4LDgGbw), and [Nyx provider trace decision](https://docs.google.com/document/d/1_TDRUkvg1hv-tKf4xVo4fOl_rT1vs3NOhXY7Sai3EWs). Semantic scope and authority stay separate; native provider thought/signature records remain private continuation data, while normalized stream activity can be shown live.

## Thought-testing research boundary

The [Gemini thought-signature thesis](https://docs.google.com/document/d/1UefWYjbN5tRSctXRnnyo0Y_4M7JgFwvY0XdrOQBIPSI) led to the public [thought-testing repository](https://github.com/zekenaulty/thought-testing), inspected at commit `907724687183d2bf73c31df34f5cc1e01281d498`. The repository distinguishes unchanged live replay from an isolated sibling probe. Its controlled results support some source-specific, query-conditioned recovery of local planning relationships and a bounded intervention effect. They do not establish a complete, decodable, or portable reasoning-state snapshot; a later exact-ledger protocol failed its stronger composite. Agentica therefore treats native continuation as opaque transport custody. The explicit objective, host policy, evidence, validation, and receipts remain canonical. Probe outputs, if added later, must stay observational and never enter the live planner history or grant execution authority. A later evaluation should compare matched baseline/intervention continuations with wrong-carrier and visible-only controls before promoting a reasoning-engineering feature.

## Next contracts

- Add bounded persistence and lifecycle disposal for provider-native continuations. Gemini, OpenAI, Anthropic, xAI, and Ollama continuation cover bounded thought/reasoning and text output; provider tool steps and unknown deltas require separate native handling.
- Extend stream telemetry with explicit tool-call phases and structured timestamps/usage snapshots suitable for a UI; keep provider-specific payloads behind adapters.
- Add provider token-aware frame compilation: reserve output, reasoning, tool-result and safety capacity, then select representation depth for optional frame entries with omission/degradation receipts.
- Expand MCP transport authentication beyond host bearer tokens and support additional safe result types. Verify mutation effects with a real host-specific acceptance predicate and test a separately operated MCP server.
- Run live OpenAI, Anthropic, xAI, and Ollama follow-up smoke calls when credentials or a local Ollama model are available.

The user supplied the Bounded Context Envelope and Host/Application Authority synthesis directly; the four linked Google source documents were also reviewed for the contracts above.

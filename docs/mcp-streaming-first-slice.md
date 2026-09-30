# MCP and streaming providers: first runtime slices

These slices add a host-bound MCP client tool, streamed Gemini Developer API and OpenAI Responses clients, and native Ollama streaming. Agentica's runtime stays independent of provider and MCP SDK types.

## Streamed Gemini planning

Set `GEMINI_API_KEY` (or `GOOGLE_API_KEY`), then use Lab `run` or `chat` with `--planner gemini`. Lab selects Interactions by default for Developer API credentials; `AGENTICA_GEMINI_API=legacy` selects the older GenerateContent client. Vertex mode continues to select that SDK path by default, and `AGENTICA_GEMINI_API=interactions` explicitly selects the Developer API. The Interactions client posts `store=false` and `stream=true`. The host supplies full bounded input for each independent request. Lab reports interaction activity, visible text deltas, provider supplied thought summaries when requested, and terminal token usage to stderr while the call is in progress. The planner accepts output only after a completed interaction event.

The streaming client can return a private native continuation from complete terminal model steps or reconstruct supported thought and model-output steps from `step.start`, `step.delta`, and `step.stop` events. A follow-up request replays those steps with the next user input under the same model and system instruction while keeping `store=false`. Signed thought steps retain their signature; signatures are absent from ordinary response/request serialization, planner text, and receipts. Missing signed steps, incomplete stream steps, and unknown delta types withhold continuation. Provider function calls are rejected. Numeric thinking budgets and temperature remain unsupported. Legacy Gemini generation remains available and refuses assistant/tool history rather than flattening it without signatures.

On 2026-09-30, a live Gemini 2.5 Flash smoke call showed that `interaction.completed` omitted `steps` while streamed `thought_signature` and model-output deltas were present. After implementing step reconstruction, two consecutive Developer API calls with `store=false` both completed and produced private native continuations. Only event counts, usage, completion, and continuation availability were printed; response text and signatures were not logged. This verifies the text/thought path for that model, not function-call or multimodal replay.

## OpenAI Responses streaming

Set `OPENAI_API_KEY`, then use Lab `run` or `chat` with `--planner openai` and optionally `--model <model-id>`. The default model is `gpt-4.1`; choose a reasoning-capable model when requesting thought summaries. The client posts `store=false` and `stream=true` to the Responses API. Lab reports activity, output timing/size, provider-supplied reasoning summaries when requested, and terminal usage. Only `response.completed` with `status=completed` is accepted. Truncated, failed, incomplete, refused, or unexpected native tool-call responses do not become plans.

For a stateless follow-up, the adapter privately retains the complete native output array, including encrypted reasoning items, after the original user input. The follow-up replays those items with one new user message under the same provider, model, and instructions. If a reasoning item lacks `encrypted_content`, continuation is withheld. The opaque payload is excluded from ordinary request/response serialization, logs, planner text, and receipts. The adapter's JSON mode is an output hint; Agentica's planner still validates the result against its own workflow contract. Numeric thinking budgets, temperature, and native provider tool calls are not mapped by this slice. Contract tests cover replay, stream completion, and binding failures; no OpenAI credential was available for a live provider test.

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

The first SDK transport uses unauthenticated Streamable HTTP. Result content currently accepts text blocks and optional structured JSON, with a 65,536-character limit per channel. Unsupported binary/image/resource blocks fail. The receipt carries server/tool/schema identities, a content digest, and success/error status. The observation carries the remote result as untrusted data. The MCP server's actual behavior must still be checked by the host; this adapter cannot prove that a remote operation is read-only.

## Native Ollama streaming

Lab `run` and `chat` accept `--planner ollama` with `--model <installed-model>` or `OLLAMA_MODEL`. `AGENTICA_OLLAMA_ENDPOINT` may set the full `/api/chat` URL; otherwise `OLLAMA_HOST` or local loopback is used. The native adapter consumes Ollama's streamed newline-delimited JSON, reports live output and thinking activity, and waits for a terminal `done` record before accepting a response. Raw `message.thinking` stays out of planner text and receipts. Ollama does not supply a portable signed thought continuation in this protocol, so native assistant/tool history and numeric thinking budgets are rejected rather than silently flattened.

## Host binding and authority

The host's deliberate installation of a tool into an active execution surface is a standing, scoped grant for that capability. Agentica no longer infers an extra one-shot approval from `ExternalSideEffect` alone. An installed declaration may still explicitly require a per-invocation grant; effect policy, schema validation, manifest identity, and host resource fences continue to constrain dispatch. Mere MCP discovery does not install a tool. The originating objective and valid delegated descendants supply purpose; installation alone does not authorize an unrelated objective. See `host-binding-authority.md` for the bounded-context contract and current implementation gap.

## Next contracts

- Extend the private native continuation contract to Anthropic signatures and xAI reasoning items, with exact replay tests and bounded persistence. Gemini and OpenAI continuation currently cover bounded thought/reasoning and text output; provider tool steps and unknown deltas require separate native handling.
- Add a provider-neutral stream event contract for time-to-first-token, token usage, tool-call phases, and terminal/unknown outcomes; keep provider-specific payloads behind adapters.
- Add context-window budgeting at frame compilation: reserve output and safety margin, then account for mandatory layers and candidate selection with omission receipts.
- Expand MCP transport authentication, safe result types, explicit mutation authorization, and integration testing against a live local MCP server.
- Add Anthropic and xAI Grok adapters using the same bounded streaming contract and provider-specific continuation tests; extend Ollama with native continuation if the transcript model can preserve every required provider field. Run a live OpenAI smoke when credentials are provided.

The user has supplied the Bounded Context Envelope and Host/Application Authority synthesis directly. The referenced original Google Domain of Domains thesis and Nyx cognition-loop source still need direct review before their additional claims become core contracts.

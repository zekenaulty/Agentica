# MCP and Gemini Interactions: first runtime slice

This slice adds a host-approved MCP client tool and an opt-in streamed Gemini Developer API client. Agentica's runtime stays independent of provider and MCP SDK types.

## Streamed Gemini planning

Set `GEMINI_API_KEY` (or `GOOGLE_API_KEY`) and `AGENTICA_GEMINI_API=interactions`, then use Lab `run` or `chat` with `--planner gemini`. The client posts to the Gemini Interactions Developer API with `store=false` and `stream=true`. The host supplies full bounded input for each independent request. Lab reports interaction activity, visible text deltas, provider supplied thought summaries when requested, and terminal token usage to stderr while the call is in progress. The planner accepts output only after a completed interaction event.

This client supports one text generation at a time. It rejects native assistant/tool history because replaying Gemini thought signatures requires exact typed steps. It also rejects numeric thinking budgets and temperature until the Interactions mapping is specified and tested. Provider function calls are rejected. Raw thought signatures are never projected as ordinary text or placed in receipts. Legacy Gemini generation remains the default.

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

## Next contracts

- Preserve provider-native continuation artifacts, including Gemini thought signatures, OpenAI encrypted reasoning, Anthropic signatures, and xAI reasoning items, without exposing opaque internals in generic narration.
- Add a provider-neutral stream event contract for time-to-first-token, token usage, tool-call phases, and terminal/unknown outcomes; keep provider-specific payloads behind adapters.
- Add context-window budgeting at frame compilation: reserve output and safety margin, then account for mandatory layers and candidate selection with omission receipts.
- Expand MCP transport authentication, safe result types, explicit mutation authorization, and integration testing against a live local MCP server.
- Add OpenAI, Anthropic, xAI Grok, and Ollama adapters using the same bounded streaming contract and provider-specific continuation tests.

The Google Domain of Domains thesis and Nyx cognition-loop source must be read directly before turning their design into core contracts. Local summaries suggest keeping semantic domain scope separate from authorization and treating thought-test conclusions as evidence rather than authority.

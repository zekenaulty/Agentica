# Provider reasoning controls

`LlmThinkingOptions.AtEffort(LlmReasoningEffort.Low, includeThoughts: true)` selects a
named provider setting while preserving the existing summary-display preference.
Effort is soft guidance with provider-specific semantics. It does not reserve tokens,
set a hard budget, or imply equal reasoning work across models. Use `MaxOutputTokens`
for the provider's output limit.

Lab `run` and `chat` expose named effort with
`--reasoning-effort none|minimal|low|medium|high|xhigh|max`, for example:

```text
Agentica.Lab run "Inspect the workspace" --planner openai --model <compatible-model> --reasoning-effort low --include-thoughts
Agentica.Lab chat "Review the current task" --planner gemini --model <compatible-model> --reasoning-effort high
```

The names are case-insensitive; numeric enum values are rejected. `--include-thoughts`
continues to control summary display independently. In these two CLI commands,
`--reasoning-effort` and `--thinking-budget` are alternative selectors: supplying both
is a usage error before provider configuration or execution. Budget-only commands
and omitted controls retain their existing behavior. Provider-specific combined
budget/effort requests remain available through the library API described below.
Scenario commands retain their existing options.

| Adapter | Field sent for `Effort` | Accepted adapter values |
| --- | --- | --- |
| OpenAI Responses | `reasoning.effort` | none, minimal, low, medium, high, xhigh, max |
| xAI Responses | `reasoning.effort` | low, medium, high, xhigh |
| Gemini Interactions | `generation_config.thinking_level` | minimal, low, medium, high |
| Ollama chat | `think` as a JSON string | low, medium, high |
| Anthropic Messages | `output_config.effort` | low, medium, high, xhigh, max |

These are adapter vocabularies, not model capability declarations. A particular model
may support fewer values, reject disabling thinking, or assign a different meaning to a
level. Agentica does not rewrite model IDs or guess token counts. Select a compatible
model and inspect provider errors. An unsupported adapter value fails locally with
`unsupported_reasoning_effort`; an undefined enum fails with `invalid_reasoning_effort`.

Existing requests keep their behavior when `Effort` is omitted. Omitting `Thinking`
leaves each provider's defaults in effect. `Dynamic()` still means provider-managed
thinking: it does not select a universal named effort. `Off()` and `Budget(tokens)`
retain their earlier provider-specific support. Budgets below `-1` now fail explicitly.

## Provider details

- OpenAI keeps the effort and optional `summary: "auto"` in the same `reasoning`
  object. Supported effort values and defaults vary by model; `none` is not supported
  by every reasoning model. Numeric budgets remain unsupported in this adapter.
  See [OpenAI reasoning](https://developers.openai.com/api/docs/guides/reasoning).
- xAI accepts its own effort subset. On some Grok variants effort controls agent count;
  on others it controls reasoning depth. Earlier models may reject or coerce a level.
  See [xAI reasoning](https://docs.x.ai/developers/model-capabilities/text/reasoning).
- Gemini Interactions sends a level independently of `thinking_summaries: "auto"`.
  Some Gemini models do not support `minimal` or `medium`. Numeric budgets and disabling
  by zero remain rejected for Interactions. The legacy GenerateContent mapper also
  supports levels; it rejects a simultaneous explicit budget and level and omits the
  dynamic-budget sentinel when a level is chosen.
  See [Gemini thinking](https://ai.google.dev/gemini-api/docs/thinking).
- Ollama preserves the wire type: `Dynamic()` sends `true`, `Off()` sends `false`,
  and `AtEffort(Low)` sends `"low"`. A request that combines off with a named effort
  fails with `conflicting_thinking_controls`. For example, use a named level with
  `gpt-oss:20b`. Hosts should inspect `/api/show` and its `thinking.values` and
  `thinking.default` when available: some models permit only boolean controls or only
  named levels, and unsupported names can fall back to the server's default. This
  adapter currently exposes the documented low/medium/high vocabulary; it does not
  perform a discovery request on every generation.
  See [Ollama thinking](https://docs.ollama.com/capabilities/thinking).
- Anthropic keeps mode, display, and effort separate. `AtEffort(...)` uses the existing
  adaptive mode; `Budget(tokens) with { Effort = ... }` preserves manual `enabled`
  mode and validates `1024 <= budget < max_tokens`. That combination is useful on
  compatible older models such as Opus 4.5. Newer adaptive-only models can reject
  manual budgets. Disabled mode retains its existing validation; whether a model
  permits disabled thinking at a given effort remains a provider constraint.
  See [Anthropic effort](https://platform.claude.com/docs/en/build-with-claude/effort)
  and [thinking configurations](https://platform.claude.com/docs/en/build-with-claude/thinking).

Request fixtures exercise the actual HTTP serialization and streaming response path for
all five adapters, omission of defaults, summary/effort coexistence, unsupported values,
and budget conflicts. They use an in-memory HTTP handler and require no credentials.

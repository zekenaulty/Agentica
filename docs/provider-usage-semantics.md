# Provider output-token semantics

`LlmUsage` preserves its six positional source counters and constructor:
`PromptTokens`, `OutputTokens`, `ThinkingTokens`, `TotalTokens`,
`CachedPromptTokens`, and `ToolUsePromptTokens`. These counters are not
universally disjoint categories. Adding all of them double-counts some providers.

The optional `OutputTokenSemantics` init property declares only whether
`OutputTokens` already includes `ThinkingTokens`. External callers and old
serialized usage default to `Unknown`. No provider is inferred from numeric
coincidence or response text.

| Semantics | `NonThinkingOutputTokens` | `GeneratedOutputTokens` |
| --- | --- | --- |
| `Unknown` | Unknown | Unknown |
| `IncludesThinking` | Output minus thinking, when both are reported | Reported output |
| `ExcludesThinking` | Reported output | Output plus thinking, when both are reported |

The derived properties return `null` when a required operand is absent, a source
counter is negative, the inclusion relationship contradicts the counters,
generated output exceeds a supplied total, or the derived sum overflows `int`.
Explicit reported zero remains zero. Missing thinking does not mean zero:
an inclusive output total can still be known while its non-thinking portion is
unknown; an exclusive non-thinking count can be known while its generated total
is unknown. Raw source values remain available even when derivation is refused.
Cached and tool-use prompt counts are never added to generated output.

`NonThinkingOutputTokens` is deliberately not named visible output. Non-thinking
usage can still include invisible formatting, tool-call structure, or modality
tokens. Neither derived property measures the exact displayed answer. Do not
tokenize a reasoning summary or use its character length to invent thinking
usage. These properties also do not establish quality, current context occupancy,
a monetary cost, or a complete billable total including provider tool charges.

## Adapter assignments

The following official sources were checked on 2026-11-03. Endpoint contracts
matter: a related endpoint can use a different accounting rule.

| Adapter | Assignment | Evidence and limits |
| --- | --- | --- |
| OpenAI Responses | `IncludesThinking` | OpenAI documents output usage as all generated tokens, with reasoning as a subset. Non-reasoning output can contain hidden structure. [Output counts](https://developers.openai.com/api/docs/guides/token-counting), [reasoning guide](https://developers.openai.com/api/docs/guides/reasoning). |
| xAI Responses | `IncludesThinking` | The official Responses example explicitly includes reasoning in `usage.output_tokens`; it separately warns that compaction uses a different rule. This assignment applies to the implemented Responses adapter, not a future compaction adapter. [Official cookbook](https://docs.x.ai/cookbook/examples/prompt-caching). |
| Gemini Interactions | `ExcludesThinking` | Output and thought tokens are separate fields; the official example reports input 62, output 171, thoughts 297, total 530. [Thinking and usage](https://aistudio.google.com/docs/thinking#pricing). |
| Gemini GenerateContent, SDK text and image mappers | `ExcludesThinking` | Google defines total as prompt + tool-use prompt + thoughts + candidates. The existing output field remains the candidates count. [UsageMetadata](https://aistudio.google.com/api-reference/generate-content#Resource:GenerateContentResponse). |
| Anthropic Messages | `IncludesThinking` | Thinking is counted with output usage even when full reasoning is not returned. The adapter receives no separate thinking-token usage field, so it does not derive non-thinking output from summaries or signatures. [Thinking guide](https://platform.claude.com/docs/en/build-with-claude/thinking). |
| Ollama chat | `Unknown` | Official usage documents `eval_count` as output tokens but does not explicitly establish the thinking inclusion relationship used by every supported model. Preserve `eval_count`; do not claim an exact visible count or infer zero thinking. [Usage](https://docs.ollama.com/api/usage). |

OpenAI/xAI/Gemini provider totals remain provider-reported values. Anthropic and
Ollama retain their existing local input-plus-output total when both operands
are nonnegative and the sum fits `int`; invalid, absent or overflowing operands
now yield `null` rather than a wrapped total. That local sum does not establish
all billable input, cache-write categories, or provider tool charges.

## Streaming and proof boundary

Completed `LlmStreamEvent` responses and `LlmTelemetryFeed` retain the same
`LlmUsage` object, so the semantics and derived quantities travel through the
existing reusable projection. Token counts are published only when supplied in
terminal usage; text/thought-summary character progress remains a separate
measurement. Native reasoning/signatures/continuations are unchanged and remain
in their existing private channel.

Focused fake-HTTP tests exercise the actual per-provider streamed completion
mapping and preserve the existing privacy and continuation assertions. Usage
contract tests cover old constructor/deconstruction/JSON, inclusion/exclusion,
explicit zero, absent operands, malformed relationships, overflow, cache/tool
non-additivity, SDK mapping, and completed telemetry. They establish adapter
mapping and arithmetic, not live model access, billing accuracy, or provider
answer quality.

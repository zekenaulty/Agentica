# Live modern-provider verification

`LiveModernProviderSmokeTests` is disabled by default. It makes exactly two
streamed calls per selected provider, with no retries and a four-minute total
deadline. It verifies successful completion, progress before completion,
replayable private continuation, and recovery of a random verification word
through native history on the follow-up. It disposes all returned carriers.
Only timing, counts, usage, model name, and continuation availability may be
logged; output, summaries, native history, signatures, and keys are omitted.

Configure the provider's ordinary key environment variable, then explicitly
select both provider and model. For example, from the repository with its SDK:

```powershell
$env:AGENTICA_RUN_LIVE_PROVIDERS = 'openai,gemini'
$env:AGENTICA_LIVE_OPENAI_MODEL = 'gpt-5.4-mini-2026-03-17'
$env:AGENTICA_LIVE_GEMINI_MODEL = 'gemini-2.5-flash'
dotnet test Agentica.Tests/Agentica.Tests.csproj --filter FullyQualifiedName~LiveModernProviderSmokeTests
```

Supported selectors are `openai`, `gemini`, `ollama`, `anthropic`, and `xai`.
Each requires `AGENTICA_LIVE_<PROVIDER>_MODEL`. Optional
`AGENTICA_LIVE_<PROVIDER>_EFFORT` accepts `dynamic` or a named reasoning effort.
The default is `dynamic` for Gemini and `low` for the others. Select a model
that supports the chosen control. A live check can incur provider usage charges.

## Recorded checks: September 30, 2026

The host explicitly deferred remaining live Anthropic, xAI, and Ollama checks.
The current acceptance gate is implemented adapters and a tested integration
seam for all five providers. The following results retain the live evidence
already gathered; failures and deferrals are not converted into live passes.

| Provider/model | Result | Evidence boundary |
| --- | --- | --- |
| OpenAI `gpt-5.4-mini-2026-03-17`, low | PASS | Two streamed, stateless calls, private continuation, verification-word recovery; existing host credential reused |
| Gemini `gemini-2.5-flash`, dynamic | PASS | Two Developer API Interactions calls with `store=false`, private continuation, verification-word recovery |
| Ollama `gpt-oss:20b`, low | FAIL before inference | HTTP 500 reproduced by `/api/show`; installed legacy GGUF tensor type is unsupported by local Ollama 0.34.2 |
| Ollama `qwen3-vl:8b`, dynamic | TIMEOUT | Model metadata and loading succeed; first streamed call did not complete within four minutes |
| Anthropic | NOT RUN | No live credential available |
| xAI | NOT RUN | No live credential available |

No Ollama model was downloaded, deleted, or replaced, and its runtime was not
restarted. The failures above do not establish successful live Ollama reasoning
replay. All five adapters separately have deterministic request/stream fixtures
and real `AgenticaRunner` tests for query, normalized evidence, native follow-up,
governed host mutation, and receipt-backed completion. These fixtures establish
the code contract without asserting live service or local-model availability.

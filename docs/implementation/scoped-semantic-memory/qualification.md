# Scoped semantic retrieval qualification

## Implemented boundary

Issue #45 implements provider-neutral core encoding/retrieval/citations and linked
export execution, an OpenAI embedding adapter, and separate Lab model mapping and
SQLite writes. The Lab consumes the core through the existing Agentica loop.
Personal/governed memory and cross-conversation retrieval remain disabled.

## Local evidence

Windows, .NET SDK 10.0.401, Release with warnings as errors. The tracked SDK pin
remains 10.0.302; locked restores left dependency lock files unchanged.

| Proof | Result | Scope |
| --- | --- | --- |
| Core + embedding transport tests | 104 passed | Bounded exports, profile/scope/perspective filtering, fresh canonical sources, absolute UTF-16 citations, malformed/lying collections, fatal errors, exact write acknowledgement, HTTP validation/cancellation/no retry |
| Focused client-suite entrypoint | 55 passed | Same embedding transport tests, separately compiled through the focused client project; not 55 additional unique cases |
| Host recall + storage tests | 44 passed | 22 prior recall cases, 14 storage cases and 8 semantic integration cases |
| Chat JavaScript checks | 29 passed | Existing Markdown/trace/settings behavior plus explicit semantic-preview limitation |
| Independent source review | Four findings corrected | Collection bounds/fatal errors, paid effect declaration, lazy optional storage and truthful bounded-result window |
| Browser configuration | Save + reload verified | Isolated chat/storage/preview, semantic checkbox retained, blank instructions and personal memory disabled |

The complete local host proof runs `ExperimentService` with the actual
`OpenAiExperimentExecutor` and existing Agentica runner. A synthetic planner
searches an older nonlexical source beyond the protected recent window, reads the
exact original, refines and produces the terminal response artifact. Both HTTP
seams are intercepted; source/citation/projection storage uses actual SQLite.
Other cases reopen both stores without document re-encoding, verify participant
and agent attribution and nonzero Unicode offsets, reject foreign conversations,
and prove unknown dispatch is latched unavailable without retry/fallback.

Corrupt and schema-99 optional sidecars permit actual Lab startup and an Off
direct turn, make no embedding calls during preview, and become unavailable only
when semantic retrieval is invoked. Semantic search declares
`Action/ExternalSideEffect` and `Additive` retry safety; exact reads remain
`Query/ReadOnly/Idempotent`. Nonzero semantic offsets are refused before dispatch.

Private local TRX/browser artifacts are under `.agentica/verification/semantic/`.
No owner conversation was submitted or edited for these tests. The existing live
preview was left running during qualification.

## Limits and release gates

This is offline execution/transport/storage and local browser evidence. No live
embedding request or semantic-quality/cost benchmark was performed. Synthetic
vectors prove the seam rather than actual language similarity. Other embedding
vendors are not qualified by this slice.

The initial Lab budgets are 32 missing sources / 64,000 characters, at most 512
inspected source headers and four embedding requests per admitted turn. Oversized
whole sources and bounded scans remain explicit omissions. Semantic retrieval
offers one bounded result window; local lexical inspection retains paging.

Before merge, the exact PR head must pass the repository's complete Windows and
Linux CI gates. CI results belong to the immutable GitHub check run, not this
pre-merge local qualification table.

Upgrade preserves canonical schema 2 and adds a lazy disposable sidecar schema 1.
Saved settings gain an optional default-false flag. Older binaries may reject
newer serialized settings; keep a stopped-service database backup for a deliberate
rollback rather than opening newer data with an older build.

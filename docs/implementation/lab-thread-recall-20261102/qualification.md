# Thread history recall qualification

## Scope and source

Implements the local exact-source slice of [Lumina's thread-scoped episodic recall design](https://docs.google.com/document/d/1l0-1l0hXbAbCdM3Qw-iVNDDMtBYLR2zCf6t9KF9zwU0/edit), read in full on November 2, 2026 and rechecked at the same revision before delivery. Personal/governed memory, embeddings, semantic vectors, federation and MCP hosting remain future work. The Lab owns storage, selection and UI; no new Agentica core abstraction is introduced.

The existing feature worktree is `codex/lab-clean-room-chat`, following `16ca1b8c9c1ba41509bb98411372b25bd0e12f19`. The owner's active primary checkout and peer checkouts are preserved. Private user messages are excluded from fixtures, source and this report.

## Acceptance surfaces

| Surface | Required observation |
| --- | --- |
| Recall off | No evidence-source access/index materialization; unchanged Direct request bytes/categories; blank instructions omitted |
| Canonical custody | Additive v1→v2 migration preserves canonical JSON, rollback on malformed source, newer schema refused, restart retains source identity |
| Scope | Server-bound conversation and inclusive pre-turn cutoff; foreign, future and uncommitted sources unavailable; model cannot override scope |
| Retrieval | FTS5 phrase-first and bounded keyword fallback, exact canonical verification, Unicode/diacritics/overlap, complete/partial/no-match distinction |
| Admission | Current input and recent floor protected; optional exact spans fit the shared actual-wire admission gate; calls and evidence bounded |
| Agentic execution | Actual AgenticaRunner chooses search, consumes evidence, reads a returned source, refines and emits the answer artifact |
| Browser | Opt-in saved modes, exact pins/original readback/source jump, newest-20 paging, retained recall manifest, active stream continuity and draft preservation |
| Maintenance | Explicit background rebuild, cancel/resume/restart, bounded progress, broken FTS cannot prevent saving canonical chat |

## Executed local evidence

The initial full Release Web run compiled on installed SDK **10.0.401** and reported **230 passed, 2 failed, 1 Windows symlink skip** (233 total). One failure was an outdated negative-query fixture: the new transparent keyword fallback correctly matched a word in the question. The negative fixture now uses an absent unique token. The other was the new fake provider emitting an initial-plan JSON object where the actual Harness refinement contract requires a `refinedPlan` envelope. Runtime validation correctly refused it. Final integrated results supersede this initial run below.

Final local commands and results:

- `dotnet test Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj -c Release --no-build --no-restore`: **244 passed, 1 Windows symlink capability skip, 0 failed** (245 total); evidence `verification/test-results/thread-recall-qualified.trx`.
- `node --test browser-tests/host-sdk.test.mjs browser-tests/host-recovery.test.mjs browser-tests/host-result-validation.test.mjs browser-tests/host-operations.test.mjs Agentica.Lab.Web.Tests/chat-ui.test.mjs`: **102 passed**, including 19 chat checks.
- Full solution Release build: **0 warnings, 0 errors**. Full solution `dotnet format --verify-no-changes --no-restore`: pass. The worktree SDK pin was temporarily selected for the installed SDK during formatting and restored byte-for-byte; the checked-in pin is unchanged.
- The actual Runner completes the three-call fake-provider search → read → refine → response-artifact path. A separate hostile-history fixture deliberately proposes an unbound `scratch.write`; the Runner rejects it and creates no file. This proves binding enforcement, not model immunity to hostile text.
- Natural-language Auto queries, adjacent corrections, source unavailability, shared prefetch/tool call and character ceilings, optional neighbor failures, one-code-unit Unicode remainders, and precise schema bounds pass. No broad retry or alternate source/provider is attempted after an unavailable source.

Independent review found two concurrency issues at the existing service boundary: settings could change between preflight and admission, and completion status could become visible before its terminal SSE event. Admission now checks the preflight settings revision and history head inside the store lock; completion and its terminal event publish under one critical section. Focused concurrent-publication, stale-admission and idempotent-replay tests pass.

A final cancellation review found that synchronous host prefetch was not receiving the turn token. Context compilation now forwards cancellation through every source operation and checks it between allocation steps. One linked service deadline covers preparation plus execution, with explicit Stop remaining `cancelled` and deadline exhaustion reported as `failed/turn.timeout`. The focused recall/API/persistence/budget suite passes **48 tests**, including four source cancellation cases, cancellation inside search with zero provider dispatch, and actual service Stop versus deadline tests retaining partial output (`verification/test-results/thread-recall-cancellation.trx`).

The initial feature head `b9ddb0bed99af9be0516c1ac3efe733b9c60f33f` passed both Windows and Linux [CI run 41569261239](https://github.com/zekenaulty/Agentica/actions/runs/41569261239), including the pinned SDK, audits, build, format, analyzers, tests, coverage, package validation, container smoke and secret scan. The cancellation follow-up is qualified separately at its new head.

The first synthetic 5,000-turn test measured newest-20 payload **33,357 bytes / 10 ms**, full compatibility read **8,317,644 bytes / 136 ms**, recent-17 load **5 ms**, explicit 10,000-source rebuild **177,037 ms**, and early exact lookup **391 ms**. These are local measurements of this corpus and machine, not latency guarantees. Rebuild is explicit, local, cancellable background work; ordinary opening and turn admission do not load the whole transcript. Deterministic gates verify page cardinality and payload reduction rather than imposing hardware timing thresholds.

Browser verification used the explicit credential-free fixture on port **5090**, a disposable store, and synthetic messages:

- Intercepted 5,000-turn UI test: newest 20, bounded older pages, preserved scroll position, source jump, exact emoji UTF-16 range, active SSE during paging/reconciliation, explicit recall manifest payload load, 320 px layout and zero history-index requests for fresh Off.
- Actual HTTP/SQLite/SSE test: 25 completed fixture seed turns and one recalled turn; older source lies outside the inference window, local lexical search finds it, exact `[20,34)` read/pin returns `copper-lantern`, saved settings reload, and `context.recall` binds it to the frozen cutoff. No browser API mocks in this service test and no page errors.
- Existing draft/request-ID/context-limit/settings-conflict recovery browser regression remains passing.

Artifacts remain outside Git under the task's `verification/browser/` directory: `thread-recall-qualification.json`, `thread-recall-service-qualification.json`, `experiment-thread-recall-source.png`, `experiment-thread-recall-service.png`, and `send-recovery-qualification.json`. The synthetic browser executor proves allocation/persistence/UI; its synthetic request is not evidence of actual provider wire construction. Separate HTTP adapter and real-runner fixtures own that proof.

## Local installation

Before updating port 5089, a consistent SQLite backup and separate migration copy were created outside Git. Opening the copy with the new runtime preserved all canonical conversation, turn and trace columns byte-for-byte and created zero index segments. The idle preview was then upgraded from that verified source. Its single conversation, 22 turns and 17,042 trace events retained their hashes; saved revision 7 and its exact instructions/settings remained unchanged. The actual browser was refreshed with its current unsent draft retained in memory and restored exactly. Readback confirmed recall off, newest-20 paging, saved revision 7 and the new recall control. No user message was sent and no inference was run. Backup and verification manifests are under `verification/recall-upgrade-20261102-204209/`.

## Storage and retention limits

The index is disposable, per-conversation, and contains exact transcript text. Only completed user/assistant messages are eligible. An explicit search catches up at most 256 sources; incomplete coverage is reported. Exact fallback scans at most 1,000 canonical sources or 8 MiB of indexed text, and candidate inspection is capped at 1,000 segments. Optional local query expansion uses at most 12 non-stopword terms and claims lexical overlap, not semantic relevance or accepted truth.

Supported canonical store mutations remove both normal and healthy FTS projection rows transactionally. Missing/broken FTS degrades gracefully and preserves canonical writes. Raw external database edits are outside the supported mutation API: canonical checks immediately exclude changed/ineligible sources, while orphan FTS rows are purged on the next explicit search/rebuild. Removal of rows is not secure erasure of SQLite free pages, WAL files or backups.

This release adds no user-facing transcript edit/delete or retention-policy UI. Such controls must implement coordinated removal of source projections, already-retained diagnostic quotations/requests and backups under an explicit retention contract. Existing authorized per-conversation traces may retain quotations presented on earlier turns; source readback always resolves canonical eligibility again. No claim of comprehensive retroactive erasure is made.

## Evidence limits

- No paid/live provider inference or embeddings were used for qualification. Local fixtures do not establish real model attention, answer quality, semantic recall or account-specific compatibility.
- Selected/presented source text remains historical evidence with original role/hash/range. It does not become instructions, current accepted truth or governed memory.
- Existing service ceilings remain: 5,000 saved turns per conversation and per-turn trace/output limits. This feature improves bounded access within those limits.
- Local SDK differs from the unchanged pinned 10.0.302. Remote Windows/Linux CI qualifies the pinned environment separately.

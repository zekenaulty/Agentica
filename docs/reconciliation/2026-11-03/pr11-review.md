# PR 11 independent merge review

Initial review date: 2026-11-03. Reviewed head: `785040e3a7dc2c0b3c905716f14c8fbe01143a8e`. Base: `977d0cab5421fd4e09fd33f1224f241b7e425abe`.

## Current disposition: admitted to main

Integration update verified on 2026-11-03: [PR #11](https://github.com/zekenaulty/Agentica/pull/11) merged at **2026-11-03T13:41:34Z**. The admitted source head is `cc813f6a9c2b1a851a1698b52ab2e96a4a68716f`; merge commit `3cb2d63395fee277a8eb790c57b271cd6277125b` has parents `977d0cab5421fd4e09fd33f1224f241b7e425abe` and that exact head. The fetched `origin/main` resolves to the merge commit at this update.

[CI run 41643125122](https://github.com/zekenaulty/Agentica/actions/runs/41643125122) completed successfully for the admitted head:

- [Windows locked build and filesystem proof](https://github.com/zekenaulty/Agentica/actions/runs/41643125122/job/125941415324): success.
- [Locked build, proof tests, and package validation](https://github.com/zekenaulty/Agentica/actions/runs/41643125122/job/125941415402): success.

Review thread `PRRT_kwDOSVcrvM6y6M3k` is resolved. The retained-inspection correction and its bounded regression evidence below therefore no longer block admission. This admits the Lab-owned experimental chat slice; it does not establish live model quality, all-provider parity, governed memory, public-package readiness or the separately qualified obligation/learning contracts.

This update used read-only Git ancestry and GitHub PR, workflow/job and review-thread queries. No build, provider call, runtime restart or user-data mutation was performed for the documentation update. The original review snapshot and focused correction evidence are retained below.

## Initial disposition (historical)

**Conditional go after the focused trace correction is committed and the new head passes CI.** The 77-file feature is coherent as a Lab-owned experimental chat feature. The review found one P2 defect; the local correction and focused evidence below address it. No additional blocking defect was identified in this bounded review of persistence, turn admission, native input counting, Markdown rendering, and retained trace inspection. This is not an exhaustive proof of every feature or model combination.

At the initial review, GitHub reported the pull request open and mergeable with a clean merge state. The assigned review evidence included green Windows/Linux CI run `41627385784`; that evidence applies to the initially reviewed head, not subsequent fixes.

## Finding at initial review (subsequently resolved)

At the initial review, GitHub review thread `PRRT_kwDOSVcrvM6y6M3k` was unresolved and not outdated. Its original review was submitted against `b2a0cccf0ca82878c8d4158b6e596ae4a5849355`, but its finding remained applicable to the initially reviewed head:

- `Agentica.Lab.Web/wwwroot/chat.mjs:157`: `mergeTrace` always retains the highest 1,000 sequence numbers.
- `chat.mjs:397`: `appendEvents` merges all incoming events into that same live window.
- `chat.mjs:490`: `loadTrace` advances its durable-page cursor before handing the page to `appendEvents`.
- `chat.mjs:491`: an early retained page is consequently discarded when the window already contains 1,000 later events.

Read-only reproduction using the exported production function: merge a live tail containing sequences 1001–2000 with the retained page 1–100. Result: first 1001, last 2000, zero retained-page events visible, and 100 events reported omitted. The operator cannot inspect the fetched initial request or context manifest although the paging cursor advances. The existing Read from start control clears the window once; concurrent SSE can fill or evict that window again.

The minimal repair should give retained inspection a bounded window independent of the live tail, or otherwise deliberately move the display to the fetched page. New live events must not evict an actively inspected retained page. A regression must start with at least 1,000 live events, load an early page, receive more SSE, and show that the fetched request/context records remain inspectable. Existing collapse and payload-loading behavior should remain intact.

Comment: <https://github.com/zekenaulty/Agentica/pull/11#discussion_r4449009871>

## Reviewed boundaries and evidence

| Boundary | Source/coverage checked | Assessment |
| --- | --- | --- |
| Canonical custody | `ExperimentStore` lease, transaction boundaries, `InitializeHistorySchema`; `ExperimentRecallStorageTests` migration/rollback/newer-version tests | Additive v1→v2 migration retains canonical bodies, rejects newer schema, and rolls back failed migration. Disposable FTS failure does not prevent canonical saves. |
| Turn admission | `ExperimentService.SendAsync`, `ExperimentStore.Begin`; persistence and native-admission fixtures | Preflight precedes saved running turn. Admission rechecks both settings revision and history head under the store lock. Request replay is content-bound and does not redispatch an existing request. |
| Native counts | `OpenAiExperimentInputTokenCounter`, `ExperimentProviderTransport`, measured/compiler and native-admission tests | Counts are bound to the exact generation body hash; state references are refused; responses, attempts and cancellation are bounded. Production does not fall back to bytes when counts fail. Character ceilings remain separate from token allowance. |
| Provider effects | Direct and Harness transport composition, retained qualification | Count-only checks create no saved turn or generation. Generation budgets and stateless replay are distinct from count requests; interrupted dispatch is retained without automatic replay. |
| Markdown | `chat-markdown.mjs`, pinned vendor provenance, unit/browser fixture source | Renderer constructs allowlisted DOM rather than inserting generated HTML. Unsafe links remain inert, images do not fetch remotely, and bounded DOM allocations fall back to exact literal text. Both roles share the renderer. |
| Inspection | `chat.mjs`, UI unit tests, Markdown browser workflow | Closed defaults and explicit disclosure preservation are covered. The early-retained-page plus full-live-tail case is absent and remains a real defect. |

No builds, provider calls, user chat submissions, preview restarts, SQLite mutations, GitHub comments, review resolutions or merges were performed in this review. The read-only Node reproduction did not load a browser or user data. Broader product gaps belong in the roadmap rather than this merge gate; live answer quality, full provider parity, governed memory, comprehensive deletion/retention, and long-horizon Nyx replacement are not established by this feature.

## Focused correction and qualification

After the review, the parent authorized the focused correction in the existing Lab worktree. `chat.mjs` now keeps a separate bounded 1,000-event retained inspection window. Loading retained pages selects that window, new SSE continues updating the separate live tail and assistant output, and Resume live returns to that tail. Read from start resets inspection without deleting live activity. Coverage and eviction messages identify the selected window; a retained page response also accounts for live events that arrived during its fetch. Payload reads remain explicit and capped by the existing cache; trace disclosures still default closed.

Two pure regressions cover full live-tail versus retained-page separation and inspection bounding/omission accounting. The complete existing host/chat JavaScript command passes **111 tests, zero failures**, including the new regressions. A new fully intercepted `browser-tests/ui-experiment-retained-trace.mjs` passes: live 1001–2000, visible retained 1–100, additional SSE, retained 1–200, preserved explicit open state and one lazy payload request, Resume live at 1027–2026, accurate 26 live evictions, and retained restart without losing the live tail. Its exact synthetic request body, original user text and streamed assistant source are preserved. Zero page errors, external requests, service calls, user data or provider work are involved.

The existing Markdown browser workflow passes after explicitly choosing Resume live before its live-inspector assertions. All other Markdown, streaming, citation, collapse and lazy-payload assertions remain intact. Local browser verification reused the already-installed Chromium headless executable through a process-local Playwright launch override; no browser/runtime installation or user-browser interaction was performed. Root owns committing, remote CI, review-thread resolution and merge.

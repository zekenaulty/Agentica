# Agentica provider migration readiness — final proposal

- **Date:** 2026-11-06
- **Version:** 1.1 — reconciled with Bridge's final packet and Lumina's catch-up; ready for owner review
- **Approval:** owner approved on 2026-11-06 and requested GitHub issue publication; execution goal not yet created
- **Publication:** epic [#47](https://github.com/zekenaulty/Agentica/issues/47), stories #48–#56; see [verified publication record](PUBLICATION.md)
- **Implementation scope:** Agentica repository and Agentica-owned workspaces
- **Baseline:** `cb9154dacb327880585d518d66ee24773b8cc1b9`

## 1. Decision requested

Approve the nine stories below as the Agentica-side provider-readiness program. After approval, publish one new tracking epic under [Program #12](https://github.com/zekenaulty/Agentica/issues/12), create one issue per story, and populate linked issue numbers/URLs in the companion [story manifest](stories.json). Retain P01–P09 as stable identifiers for dependencies, anchors and idempotent publication. The owner will set the execution goal with us after approval. Planning approval, issue publication and goal creation remain identifiable steps.

Proposed goal wording:

> Make Agentica's shared provider clients ready for provider-only Nyx adoption: preserve every observed attempt outcome and usage fact, provide bounded durable native-continuation exchange and host-owned attempt admission, consolidate OpenAI search and live telemetry in the shared clients, and qualify the existing Agentica Lab loop plus a packed provider-only consumer through restart and recovery. Keep Nyx's canonical state, identity, memory admission and execution ownership intact.

This program delivers the Agentica side and a concrete Bridge handoff. Installed Bridge migration has an external acceptance gate described in section 10; it is not established by an Agentica fixture.

The [Bridge alignment and dependency crosswalk](BRIDGE-ALIGNMENT.md) is part of this proposal. It maps Bridge A1–A6 and PM01–PM18 to the existing nine Agentica stories. Bridge's first cutover is **offline and OpenAI-only**, including historical reindexing; no recovery, fallback, probe or preparatory embedding may introduce a new Google model call. Agentica retains its five-provider library and offline conformance work.

## 2. Working-time estimate

These are estimates of autonomous agent execution, verification and review time on this machine, not human sprints.

| Measure | Estimate |
| --- | --- |
| Combined active work across root and delegated workers | **20–33 agent-hours** |
| Elapsed execution with up to three coordinated implementation lanes | **9–16 working hours** |
| Additional allowance for CI correction, SDK/dependency issues and integration review | **2–5 hours** |
| Practical scheduling allowance | **Approximately 12–20 hours; outer estimate 11–21 hours** |

Story estimates include focused checks and ordinary review fixes. Parallel work reduces elapsed time, not the summed effort. Root contract decisions, shared transport edits, integrated checks and merges serialize part of the work. Approval waits, external Bridge work, production rollout and extended provider/network outages are excluded.

Revision 1.1 adds one to two aggregate hours in P08 for the explicit embedding-consumer boundary and resolved route/profile recovery cases. The approximate 12–20-hour elapsed allowance remains within the existing contingency. Bridge's separately estimated 40–75-hour Bridge/Cloud program excludes Agentica engineering; do not add aggregate agent-hours to elapsed hours or assume both projects run serially.

Re-estimate after P02/P03 establish physical-attempt control, and after P08 establishes package/dependency coexistence. Report a changed estimate with the concrete cause; do not reduce acceptance to fit the estimate.

The largest uncertainties are SDK-internal retries, backwards-compatible outcome delivery on cancellation, provider-native payload compatibility, and extracting Lab transport without bypassing admission. The local machine currently has .NET 9.0.318 and 10.0.401; repository proof requires **10.0.302**. Provision the pinned SDK in an Agentica-owned tool cache before code qualification, preserving `global.json`.

## 3. Baseline and custody

The November 6 audit found 27 registered worktrees: 26 clean and one dirty primary checkout. Its three modified documentation files and nine untracked guidance/planning files have no unique unlanded implementation; their content is represented or superseded on main. Preserve those bytes. No old feature-branch merge is a prerequisite.

All 34 local branches are either main, ancestors of main, or patch-equivalent to landed changes. GitHub has no open PRs and only remote main at the baseline above. Latest observed main CI was [successful run 41752427694](https://github.com/zekenaulty/Agentica/actions/runs/41752427694), dated November 4. That is historical baseline evidence, not execution evidence for this plan.

The plan is authored on `codex/provider-migration-plan` in the previously clean consolidated checkout. Recheck status, base/head and applicable guidance before implementation. Keep primary-checkout normalization and obsolete-worktree retirement separate from the implementation critical path. Existing previews and saved chats require their own runtime disposition; Git changes do not update a running binary.

### Existing capabilities to extend

- Strict OpenAI JSON Schema support: [#21](https://github.com/zekenaulty/Agentica/issues/21), completed.
- Explicit provider usage inclusion/unknown semantics: [#22](https://github.com/zekenaulty/Agentica/issues/22), completed.
- Standing MCP binding and current readiness: [#23](https://github.com/zekenaulty/Agentica/issues/23), completed.
- Scoped semantic retrieval, canonical citations and linked host exports: [#45](https://github.com/zekenaulty/Agentica/issues/45), completed.
- Lab already contains OpenAI native search, native input counting, request inspection, isolated persistent chat and an actual Agentica harness path.

The open [provider audit #10](https://github.com/zekenaulty/Agentica/issues/10) remains the related audit of defaults and supported controls. New implementation stories link it without duplicating its broad audit. Existing reuse/recovery/learning work [#24](https://github.com/zekenaulty/Agentica/issues/24), [#25](https://github.com/zekenaulty/Agentica/issues/25), [#26](https://github.com/zekenaulty/Agentica/issues/26) and [#31](https://github.com/zekenaulty/Agentica/issues/31) retain their own acceptance. The completed OA-H epic [#14](https://github.com/zekenaulty/Agentica/issues/14) stays closed.

## 4. Architecture and invariants

The reusable client boundary is:

```text
host objective, compiled context and standing provider binding
  → immutable prepared request and effective configuration
  → host reservation / attempt admission
  → one physical inference dispatch
  → live observations and retained terminal evidence
  → host parsing, validation and acceptance
  → update context and continue / suspend / complete
```

One dispatch means one provider POST per admitted physical inference attempt. The enclosing host can make multiple calls as its objective requires. Counting calls are distinct external work with their own identities and limits.

### Required distinctions

| Subject | Required owner/meaning |
| --- | --- |
| Provider-native continuation | Adapter-owned opaque replay material; host encrypts, stores, scopes and retains it |
| Operation routing | Bridge resolves an actual operation/phase assignment, task default and bundle default; Agentica consumes the resolved request |
| Execution bundle | Host-captured operation routes and distinct retrieval-profile references; current permissions/eligibility are rechecked at use |
| Work-episode continuity | Host objective, observations, accepted decisions, obligations and original attempt custody |
| Autobiographical integration | Nyx decides how work becomes attributed experience and how conclusions enter canonical memory |
| Provider completion | A response/terminal transport fact |
| Application acceptance | Host schema, policy, evidence and completion decision |
| UI telemetry | Observable summaries/activity; bounded and allowed to be best effort |
| Attempt evidence | Required accounting/recovery record with host-owned durability |

Invariant checklist:

1. Existing deliberate bindings supply standing use authority. Readiness and admission enforce current scope, limits and custody.
2. One retry owner. Disable hidden SDK retries or expose each physical attempt to host admission; wrappers cannot conceal extra dispatches.
3. Every supported exit preserves facts already observed: identity, status, reason, nullable usage and its provider semantics. Unknown usage is never measured zero. Reasoning subsets are never added twice to inclusive output totals.
4. Pre-dispatch failure, dispatched/unknown, provider failure, cancellation, incomplete output and accepted output remain distinguishable. Partial output cannot fall through to success.
5. Ordinary cancellation semantics remain usable while callers can retain the latest attempt evidence. Already observed final usage survives cancellation races.
6. A changed effective provider/request configuration creates a new attempt. An uncertain original attempt retains its custody and cumulative exposure through rollback or restart. Captured bindings govern already admitted work; unrelated settings edits apply to new admissions. Bridge's initial migration permits only recorded transitions to OpenAI, never a new Google call.
7. Native payloads, signatures and private reasoning do not enter ordinary serialization, logs, UI traces, receipts or planner text. An explicit export is sensitive even when its provider payload is encrypted. Existing plain native transcript carriers are not labelled encrypted reasoning.
8. Recover host work from durable objective/evidence/obligations when a native capsule is unavailable. Private reasoning remains opaque. Insufficient host evidence produces an explicit suspension reason.
9. Preserve exact compiled request identity through admission and dispatch. Counts/estimates name their basis and include the relevant complete request. A failed native count remains unavailable.
10. Preserve current strict schemas, clean-room isolation, saved chats, source attribution and host-owned memory admission. No canonical-memory reprocessing is triggered by provider selection.

Public type names and method signatures are proposed until P01 resolves the smallest compatible API. Reuse existing `ILlmClient`, `ILlmStreamingClient`, `LlmResponse`, `LlmClientException`, stream events and artifacts where possible.

P01–P06's new inference interfaces are LLM contracts. They do not implicitly extend `IMemoryEmbeddingProvider`. The A5 embedding handoff uses the existing `OpenAiEmbeddingClient` with a host-owned admitted/observed HTTP transport; P08 demonstrates that integration. Reuse a genuinely provider-neutral helper only where it reduces duplication without imposing a new embedding API.

### Shared compatibility and rollback rules

- Prefer additive or opt-in APIs and preserve existing callers. Keep necessary bug corrections explicit in release notes and regressions.
- Version new retained/exported formats. Hosts choose when to write them; readers reject unsupported versions explicitly and preserve the original payload for compatible recovery.
- A rollback preserves retained outcomes, reservations and unresolved original attempts. Reverting a feature cannot erase paid exposure or authorize repeat dispatch.
- Host work remains recoverable from accepted evidence when a native export cannot be read. Existing canonical conversations/memories and host database schemas are not rewritten by this program.
- Qualify Lab on disposable candidate state. Any later update of an existing preview must preserve its saved state and have an explicit runtime transition; no automatic preview restart is part of publishing this plan.

## 5. Story plan

All keys are local planning identifiers. No GitHub issue number is implied.

| Key | Priority | Story | Merge dependencies | Agent-hours | Issue |
| --- | --- | --- | --- | ---: | --- |
| P01 | P0 | Define retained provider-attempt evidence and compatibility contracts | None | 2–3 | [#48](https://github.com/zekenaulty/Agentica/issues/48) |
| P02 | P0 | Preserve outcomes and single-attempt semantics across provider adapters | P01 | 3–5 | [#49](https://github.com/zekenaulty/Agentica/issues/49) |
| P03 | P0 | Add immutable request preparation and host admission | P01 | 3–5 | [#50](https://github.com/zekenaulty/Agentica/issues/50) |
| P04 | P1 | Report effective controls and provider capabilities | P01 | 2–3 | [#51](https://github.com/zekenaulty/Agentica/issues/51) |
| P05 | P0 | Add bounded durable exchange for existing native-continuation carriers | P01, P02 | 3–5 | [#52](https://github.com/zekenaulty/Agentica/issues/52) |
| P06 | P1 | Promote OpenAI native search and citation streaming from Lab | P01, P03 | 2–3 | [#53](https://github.com/zekenaulty/Agentica/issues/53) |
| P07 | P1 | Adopt shared contracts in Lab's existing chat and harness paths | P02–P06 | 1–2 | [#54](https://github.com/zekenaulty/Agentica/issues/54) |
| P08 | P0 | Qualify a packed provider-only host through the complete recovery loop | P02–P06 | 3–5 | [#55](https://github.com/zekenaulty/Agentica/issues/55) |
| P09 | P0 | Complete release proof, bounded live OpenAI proof and Bridge handoff | P07, P08 | 1–2 | [#56](https://github.com/zekenaulty/Agentica/issues/56) |

### P01 — Retained provider-attempt evidence and compatibility contracts

**Outcome:** callers can obtain an immutable bounded record of observed call facts on every supported exit, without losing existing client entrypoints.

**Current gap:** shared exceptions and stream completion cannot carry all observed terminal identity, usage and uncertainty facts on failure/cancellation.

**Primary paths:** `Agentica.Clients/Llm/`, provider contract tests and new contract documentation.

**Acceptance:**

- Distinguish pre-dispatch rejection, admitted attempt, dispatched/unknown and provider terminal outcomes; keep host acceptance separate.
- Retain observed provider/model/response/request identities, reason/classification, nullable usage and inclusion semantics without fabricating missing data.
- Preserve opaque host operation/phase identifiers and resolved route/configuration references where supplied; do not interpret Nyx operation names or introduce a second router.
- Define evidence delivery for success, exception and cancellation, including a terminal-event/observer policy that cannot rely on the lossy UI feed.
- Required host admission/journal failures prevent dispatch; optional UI observer failures have an explicit isolated policy.
- Existing ordinary generate/stream consumers compile and preserve their success/error behavior; new metadata is additive or exposed through an explicit opt-in interface.
- Resolve signature ownership before worker transport edits. Include pinned-SDK preflight in this story's execution checklist.

**Boundary:** no canonical task store, episode framework, scheduler, provider pricing service or new authority system.

### P02 — Outcome retention and physical-attempt control

**Outcome:** the five provider families preserve the new evidence contract and respect host-owned retries.

**Current gap:** terminal error paths lose ordinary response evidence, and physical-attempt control needs protocol/SDK-specific qualification.

**Primary paths:** `Agentica.Clients/OpenAI/`, `Gemini/`, `Anthropic/`, `Xai/`, `Ollama/`; matching focused test sources. Include Gemini Interactions and selectable GenerateContent, plus existing compatibility entrypoints.

**Acceptance:**

- Deterministic protocol-specific fixtures cover success, refusal/blocked output, incomplete/truncated output, malformed output, HTTP errors, late stream errors, cancellation and disconnection where applicable.
- Every fact received before an error remains accessible. Unavailable fields and unsupported protocol states are identified honestly.
- One admitted inference attempt produces one physical inference request. Audit and control Google SDK retry behavior as well as Agentica wrappers.
- Usage already received survives parsing/refusal/postprocessing errors and cancellation races; deltas alone never establish a completed call.
- Preserve provider-native summaries/signatures and existing usage arithmetic without leaking private material or double-counting reasoning.
- Both streaming and ordinary generation paths consume the same retained outcome semantics. Fixtures do not qualify live-provider behavior.

**Rollback:** retain prior public entrypoints and host attempt records. Do not silently retry an unresolved call during rollback.

### P03 — Prepared requests and host-owned admission

**Outcome:** a provider-only consumer can inspect and admit the exact effective request before a single inference dispatch.

**Current gap:** useful count/hash/admission plumbing is Lab-owned; shared client callers lack an equivalent planner-independent preparation boundary.

**Primary paths:** optional prepared-call contracts/helpers under `Agentica.Clients/Llm/` and provider adapters; reusable parts of `ExperimentProviderTransport` and input-count request construction.

**Acceptance:**

- Prepared content/options are immutable; dispatch is bound to the admitted bytes or an explicitly documented provider request representation and nonsecret identity.
- Host supplies attempt identity and durable reservation/admission hooks. Failure to persist a required reservation yields zero inference POSTs.
- Expose request/configuration identity, bounded size and input accounting without requiring `RunRequest`, `PlanningRequest` or `AgenticaRunner`.
- OpenAI retains native count support for the complete applicable request. Other modes report their available count/estimate basis; proxy estimates are never called native counts.
- Keep count work separate from generation: separate admission/usage limits, no new conversation turn and no generated response.
- Changed prepared input/configuration invalidates the previous admission. Reuse cannot cross host scope or duplicate a consumed attempt.
- Preserve a host's captured per-operation route fingerprint and execution-bundle reference without conflating them. Nested/restart calls retain captured routes while live authorization, eligibility and revocation checks remain current.
- Preserve existing host cumulative budget semantics across failed attempts and restarts through integration tests, while durable storage stays with the host.

**Boundary:** host context compilation stays authoritative. Prefer a small optional provider-call seam over a new universal context compiler.

### P04 — Effective configuration and capability reporting

**Outcome:** consumers can determine the actual configured behavior and supported controls before dispatch.

**Current gap:** provider-specific option mapping exists without one shared effective-configuration/capability report usable by a host.

**Primary paths:** client option/profile reporting, relevant model-option validation and a provider conformance matrix. Related audit: #10.

**Acceptance:**

- Cover OpenAI, Gemini Interactions/GenerateContent, Anthropic, xAI/Grok and Ollama with explicit supported/unsupported/unknown states.
- Report API mode, effective model, requested/applied/default reasoning effort or thinking budget, structured-output mode, continuation exchange, native search and input-accounting basis.
- Unsupported requested combinations return a clear reason. Preserve documented provider differences; apply no hidden model or effort downgrade.
- Distinguish configured adapter capability, observed technical readiness and model/account qualification. Report evidence source/as-of data; avoid hard-coded claims about every future model.
- Return a nonsecret effective per-route configuration identity usable by host receipts and diagnostics. Actual operation/phase routing remains host-owned; broad stage/contract labels are not routing authorities. A complete host bundle identity is a separate reference.
- Model selection for a tool applies to that tool's own inference phases. Preserve existing combined requests, including memory prose/keywords, and treat deterministic tools as having no model assignment.
- Use the existing #10 audit for broad default/model research; this story implements the specific reporting contract.

### P05 — Durable native-continuation exchange

**Outcome:** hosts can export and import the existing bounded provider-native carriers with explicit format and compatibility results.

**Current gap:** current native payloads are private/in-memory and excluded from normal serialization; hosts have no public durable exchange contract.

**Primary paths:** `LlmNativeContinuation`, adapter-owned codecs/validators and focused round-trip tests.

**Acceptance:**

- Introduce an explicitly invoked versioned sensitive export/import contract, separate from ordinary request/response serialization.
- Offline round-trip every currently implemented carrier: OpenAI, Gemini API modes, Anthropic, xAI and Ollama. Preserve native item ordering, assistant phases, opaque signatures and provider-specific fields needed by the matching adapter.
- Bound size, collection/depth and malformed data; reject unsupported versions, disposed payloads and incompatible provider/model/instruction/configuration bindings with explicit reasons.
- Keep current compatibility guards until a broader model policy is individually qualified. No native state crosses providers implicitly.
- Test persisted reopen into a fresh process/consumer as well as unavailable, invalid and incompatible capsule paths. Host reconstruction uses durable accepted evidence; it does not claim reasoning replay equivalence.
- Host owns encryption, scope, expiry and deletion; test serialization/log redaction and host envelope integrity. Do not make claims of cryptographic authenticity for unsigned provider content.

### P06 — Shared OpenAI native search and source streaming

**Outcome:** both provider-only consumers and Lab can use the existing native-search capability through shared client contracts.

**Current gap:** Lab implements native search and annotations directly while shared request/response contracts do not express that capability.

**Primary paths:** OpenAI request/stream handling, typed source/annotation contracts and Lab's current ambient/explicit search implementations.

**Acceptance:**

- Reuse current opt-in options, search limits, activity, returned sources and citation annotation positions; preserve missing query fields as missing.
- Retain search evidence through failures and native continuation where supported. Search output stays external/untrusted evidence for host acceptance.
- Existing ambient search and the explicit harness web-search tool remain independently usable with their saved limits and existing scoped authority.
- No-search paths have no search dispatch. The explicit isolated search subrequest preserves its current isolation from conversation history/custom instructions.
- Sources remain clickable, canonical answer text and Markdown remain unchanged, and source positions survive streaming/reload without unsafe URLs.

**Boundary:** this story promotes existing OpenAI behavior. Additional provider-native search features and multimodal expansion require their own later scope.

### P07 — Lab adoption and live observability

**Outcome:** Lab exercises the shared provider plumbing through its existing persistent clean-room chat and actual Agentica harness.

**Current gap:** Lab duplicates provider request/stream/search handling and cannot yet consume the planned shared evidence/admission/codec contracts.

**Primary paths:** `Agentica.Lab.Web/Chat/`, explicit composition-root registrations, relevant host/UI tests.

**Acceptance:**

- Direct chat and harness calls use shared preparation, count/admission, outcome and search contracts; consolidate duplicate request/stream parsing where doing so preserves behavior.
- The existing runner performs bounded context → model decision → bound tool → observation → refinement → accepted answer. Verify resulting host evidence and residual obligations.
- Provider summaries and search/tool activity appear during execution. Distinguish provider events, host progress, terminal evidence and application acceptance; keep traces collapsed until inspected.
- A failed/cancelled turn exposes available usage/status and an accurate recovery state. Private native payloads remain undisplayed.
- Saved conversations, drafts, blank/default instructions, model/effort settings, citations and clean-room isolation survive the change and reopen.
- Verify explicit DI composition cannot bypass the turn-owned observed/admitted transport. Reuse current tests; add regressions only for changed behavior.

**Boundary:** no chat redesign, memory activation or implicit preview/production restart. Qualify an isolated candidate using disposable state.

### P08 — Packed provider-only consumer and complete recovery loop

**Outcome:** an independent synthetic host consumes packed Agentica packages and completes its own repeated decision/action/result loop without adopting Agentica orchestration.

**Current gap:** the package smoke consumer constructs package types but does not exercise Bridge-shaped inference, durable continuation or host recovery.

**Primary paths:** reusable package-consumer fixture and `eng/Verify-Packages.ps1`; proposed fixture files remain inside Agentica. Synthetic state only.

**Acceptance:**

- Build a consumer from packed packages/local feed rather than a project reference. Exercise actual client generation/streaming; instantiate neither `AgenticaRunner` nor a second scheduler.
- Pin representative foreground, evaluation/memory and document decision-schema fixtures to the reviewed Bridge source cut; preserve semantic author and strict supplied schema. Document drift and consumer qualification limits.
- Host performs multiple decide → act → observe → update → continue iterations, then verifies completion against authoritative synthetic state.
- Persist host objective, evidence, remaining obligations, reservations and original attempts. Reopen with valid native continuation and with unavailable/rejected continuation.
- Cover required journal failure before POST, lost response after POST, known terminal failure, cancellation raced with success, host parsing failure after usage, recorded provider/configuration transitions and repeated delivery/reopen. The Nyx transition fixture permits zero new Google calls; retaining a Google adapter or vector does not enable its use.
- Capture an immutable host operation-route bundle and independent memory/conversation embedding profiles. Demonstrate that settings edits affect new admissions, existing nested/restarted work retains its captured bindings, and current authorization/eligibility still applies. Unrelated text-route edits do not change embedding-profile identity.
- Consume packed `OpenAiEmbeddingClient` through a host-owned admitted/observed HTTP transport. Prove document/query profile, purpose, input order and batch/source-reference mapping; zero requests after admission failure; one physical request under the configured handler chain; safe request identity; and retained observed IDs/status/usage when valid usage accompanies invalid vectors. Missing usage stays unknown; include cancellation/lost-response negatives and no automatic retry.
- Exercise exact profile mismatch and retained-profile negatives without building an index, maintenance worker or backfill scheduler. No paid embedding call is needed for this fixture.
- Demonstrate no duplicate automatic dispatch, no erased unresolved attempt, one retry owner, distinct replacement identities and cumulative accounting through recovery.
- Consume the strengthened search/source contract in a dedicated fixture case.
- Restore/build against Bridge's reviewed Google SDK dependency level to establish package coexistence. Split packages only if a demonstrated conflict requires it.

**Evidence boundary:** deterministic consumer proof establishes client integration and recovery contracts. It does not certify installed Nyx, model judgement or production recovery.

### P09 — Release proof, bounded live OpenAI qualification and handoff

**Outcome:** stable reviewed changes are merged with reproducible evidence and a usable Bridge adapter handoff.

**Current gap:** existing qualification predates these proposed contracts; no fresh release, live-loop or Bridge-consumer proof covers them.

**Primary paths:** existing test/package/CI entrypoints, dated qualification report, current product-status overlay, provider integration example and this story manifest.

**Acceptance:**

- Run the relevant focused provider and Lab tests, impacted aggregate regressions, package verification and existing required Windows/Linux CI at exact PR heads on the pinned SDK. A zero-test or alternate-SDK run cannot stand in for the required proof.
- Run a targeted browser workflow against the isolated candidate for clean-room streaming, search/citations, collapsed traces, settings persistence and failed-turn inspection.
- Complete one bounded real OpenAI Lab harness scenario using existing credentials/settings. Target the owner's GPT-6 Luna family and high reasoning effort only after confirming the actual accessible model/control combination; report requested and effective settings. Preserve any incompatibility as a finding.
- Keep live tests for the other providers, including Ollama, deferred as the owner previously allowed. Their offline adapter seams and conformance matrix remain required.
- Publish a provider-only Bridge adapter example, operation/phase resolved-route mapping checklist, strict-schema fixtures, outcome/usage mapping, native-codec storage example and OpenAI-only reconstruction/recovery guidance. The example preserves Bridge's executor as the attempt owner and links enabling artifacts incrementally through A1–A6.
- Publish A5 separately: the existing embedding client supplies transport/profile/vector validation and success usage; Bridge PM11 owns profile/source/job/query mapping, paid admission, response/usage observation before client validation, failure custody and retry policy. Qualify the injected HTTP seam through P08. A plain exception wrapper does not recover usage discarded by the client.
- Name the external dependencies accurately: PM12–PM14's historical reindex/recall coverage is required for Bridge cutover; PM10 covers media with qualified narrow adapters where needed; PM16 is a required native TypeScript Cloud phase. P09 does not supply every zero-Gemini capability or complete those external gates.
- Publish separate source/offline/package/Lab/live/external-host evidence states. Update current status with a dated overlay; preserve historical snapshots and unrelated open issue acceptance.
- Review each story PR, recheck head/base/checks immediately before merge and merge stable changes to main. Link exact commits, checks and residual gates before closing story issues.

**P09 isolated live-verification budget proposed for approval:** at most six physical inference attempts in this proof, including failures, and eight separately counted native input-count requests; synthetic inputs capped at 8,000 admitted input tokens and configured output ceilings capped at 8,192 per inference. Apply a **US$5 cumulative cost ceiling**, using current verified model/count pricing and conservative pre-dispatch reservations. These are proof-specific limits, not Nyx production settings, historical reindex allowances or production input/output defaults. Unknown usage retains its exposure. If a required price or credential cannot be established, retain that live gate as blocked and continue independent offline work. Do not create a key, alter global credentials, expand the live cohort or replenish this proof budget automatically.

**Concrete live scenario:** seed a new isolated chat's scratch namespace with synthetic JSON containing a fresh marker and two operands, kept out of the initial model context. Bind the existing `scratch.read` and `utility.calculate` tools. Ask the agent to inspect the file, calculate the specified result and report the marker/result. Require observed read and calculation receipts, a subsequent model decision after returned evidence, and host-verified exact final values. Reopen the saved candidate chat and verify its outcome/configuration survived. Keep personal memory disabled. This exercises actual inference-driven action and refinement through existing capabilities, within the shared six-attempt budget.

## 6. Parallel execution and PR integration

P01 settles shared contracts first. Then use three lanes:

| Lane | Sequence | File/merge discipline |
| --- | --- | --- |
| A — adapter correctness | P02 → P05 | Own outcome mapping and native-carrier integration |
| B — prepared calls/search | P03 → P06 | Develop helpers separately; serialize edits to provider files touched by A |
| C — consumer/reporting | P04 plus P08 scaffold | New capability/fixture files first; finalize P08 after its merge dependencies |

P07 integrates the shared client behavior into Lab; P09 owns final evidence and handoff. P08 fixture design can begin early, but its acceptance uses the merged implementations. Root owns shared API decisions and the integration branch. Delegate bounded file sets in isolated Agentica workspaces where concurrent edits/build outputs conflict. Do not share mutable stores, fixed test ports or package output directories between workers.

Deliver exact needed producer contracts/artifacts incrementally. Bridge PM04 can proceed when A1/A2/A3 are qualified; it does not wait for P07, P09 or unrelated roadmap work. A5 reuses an existing client and gains a pinned consumer example: run/publish its isolated P08 case early without waiting for search or full P08 completion. Do not invent a new client prerequisite for Bridge's indexing preparation.

Use `codex/` story branches and focused PRs. Keep a story open through its own acceptance and required CI. Several related commits may share a PR when a public contract would otherwise be unusable between merges; preserve per-story evidence. After merge, rebase dependent work onto the accepted main head, resolving conflicts inside the owning Agentica workspace.

## 7. Acceptance matrix and stopping rules

| Gate | Required observation | Evidence level |
| --- | --- | --- |
| Common lifecycle | All five provider families/modes preserve observed facts through relevant terminal paths | Deterministic provider transport |
| Attempt custody | Required admission precedes POST; uncertain attempts survive restart and rollback without duplicate dispatch | Deterministic durable host/consumer |
| Native continuity | Existing carriers round-trip; rejection triggers evidence-based host recovery or explicit suspension | Offline/process reopen |
| Input fidelity | Admitted request identity matches dispatch; counts and estimates retain their actual basis | Transport/count fixtures |
| Resolved route continuity | Captured operation routes and distinct retrieval profiles survive settings edits/restart; live eligibility remains checked | Synthetic durable host |
| Embedding handoff A5 | Existing client plus admitted/observed host transport retains exact profiles, order, usage/error/uncertainty evidence | Offline packed consumer |
| Search | Existing ambient and explicit paths retain activity, sources and citation placement | Provider fixture plus Lab/browser |
| Agentic behavior | Actual Lab runner and provider-only host each execute the complete multi-call loop | Isolated host/consumer |
| Package adoption | Packed consumer builds and executes under reviewed dependency coexistence | Packed .NET consumer |
| Required repository gates | Pinned SDK, focused checks and Windows/Linux CI pass at the reviewed heads | Local/CI |
| Real OpenAI | Target model/control combination runs a bounded tool-enabled Lab loop within cumulative budget | Live OpenAI, isolated Lab |
| Installed Nyx | Offline OpenAI reindex/recall, required operation/media routes, PM16 Cloud and actual host recovery accepted before return online | **External; pending Bridge PM11–PM18** |

Record PASS, FAIL, UNKNOWN, NOT-RUN or BLOCKED with source/head, fixture/environment, actual command or observation, and residual work. Passing fixtures do not qualify a broader host. CI outages are waiting states; provider failures keep their observed evidence. Budget exhaustion stops additional paid work while authorized offline work continues. Do not mark a blocked live gate passed or close the epic as fully qualified.

Useful existing execution entrypoints, selected after the pinned SDK is available:

```powershell
dotnet restore Agentica.slnx --locked-mode --configfile NuGet.config
dotnet restore Agentica.Clients.Tests/Agentica.Clients.Tests.csproj --locked-mode --configfile NuGet.config
dotnet test Agentica.Clients.Tests/Agentica.Clients.Tests.csproj --configuration Release -- RunConfiguration.TreatNoTestsAsError=true
dotnet test Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj --configuration Release
node --test Agentica.Lab.Web.Tests/chat-ui.test.mjs
```

Follow the current CI and `eng/Verify-Packages.ps1` for required aggregate/package/security/container gates. These commands have not been run for this planning change. Keep deterministic runs free of inherited live-provider opt-in flags.

## 8. Issue publication after approval

1. Re-fetch current refs and re-read live related issue states. Check for newly overlapping stories.
2. Create a successor provider-readiness epic under #12; preserve completed #14.
3. Create one issue for each P01–P09 with its outcome, current gap/source cut, paths, dependencies, acceptance, migration/rollback, evidence boundary and estimate. The issue body must include its complete matching section from this approved `PLAN.md`, the applicable shared scope/invariants/compatibility rules, the Bridge crosswalk where relevant, and metadata from `stories.json`; the manifest alone is not an issue body. If a required section is missing, withhold that incomplete issue, repair its generated body and retry idempotently. Preserve already-created identities and continue independent valid publication/work; a local publication defect does not abandon the program. Retain the stable story key alongside the real issue number.
4. Use the idempotency marker `<!-- agentica-plan:2026-11-06:P01 -->` with the corresponding key for each issue; check for it before creation/retry. Retain known created issue IDs after uncertain network outcomes.
5. Update `stories.json` with returned issue URLs and explicit workflow/claim ownership. Add dependency links and checklists to the epic, then cross-link #10 and completed prerequisites.
6. Use a new bounded publication path for this cohort. Do not rerun the November 3 publisher with its historical candidate set/source pins.
7. Set the execution goal only when the owner requests that step. Track story progress in issues and exact PR/CI evidence; keep the manifest as the repository index.

No issue, epic, label change, PR, goal or implementation is created by this plan document.

## 9. External dependencies and independent follow-ons

| Work | Disposition |
| --- | --- |
| Dynamic guidance/skill loading | Prove reusable selection/load/revalidation through existing frames and tools |
| Participant history across conversations | Start with explicit authorized partition composition; generalize only for demonstrated cost/correctness need |
| Recursive document-engine extraction | First consume strengthened clients through the existing model port; extract after comparative reuse proof |
| Common cognitive episode/autobiography | Nyx-owned historical attribution and canonical-memory admission design |
| Multimodal/vision and other native search providers | Agentica expansion is later work; required Bridge PM10 media uses qualified dedicated adapters where shared support is unavailable, with no hidden Google fallback |
| Historical embedding migration | Required external Bridge PM11–PM14 cutover work using A5; no canonical rewrite or Agentica index/backfill implementation |
| Cloud provider migration | Required external PM16 after principal Bridge/reindex work; native TypeScript in the existing Worker, with no .NET service/library dependency |
| Broader Domain of Domains, JIT cognition and interpretation reuse | Continue their existing plans and evidence gates; no implied completion here |

These are separate ownership/scope entries. Required Bridge/Cloud dependencies remain required in their migration program, while Agentica follow-on features remain independent. Approval of P01–P09 creates no additional issues for these external or later implementation scopes.

## 10. Bridge handoff and external completion gate

The Agentica package changes support this consumer layering:

```text
Nyx context, identity, planning, memory and effects
  → ProviderCallExecutor
  → Bridge-owned ICompanionProvider adapter
  → Agentica.Clients single-attempt transport
  → selected provider
```

Bridge owns installation, the exhaustive operation/phase registry and resolver, health/settings, immutable execution/retrieval bundles, native export encryption/storage and actual-host acceptance. It resolves a specific operation assignment, then task default, then bundle default; Agentica receives and reports the resolved model/request. Existing combined memory construction remains one request. A text-model route change does not silently change historical embedding profiles.

**Initial cutover:** conversational Nyx remains offline while required OpenAI historical indexing and recovery are qualified. Preserve canonical records, accepted memories, original attempt evidence, retained results, keys and existing vectors. Do not resume Google model or embedding calls through retries, rollback, fallback, diagnostics or uncached query encoding. Existing Google-bound obligations recover through a recorded OpenAI boundary after reconciling original effects. Retaining inactive Google adapters/vectors grants no migration use; Drive/Docs source transport remains usable. Library-level Gemini compatibility and offline fixtures continue separately.

**Embedding contract:** A5 pins `Agentica.Clients/Embeddings/OpenAiEmbeddingClient.cs`. It implements one client send with exact profile/input-order validation and nullable success input usage. `EmbeddingClientException` currently carries provider/error/status without retained usage/response identity; malformed vectors can reject a response before its usage is returned. Bridge PM11 supplies an admitted/observed `HttpClient` transport that preserves safely received evidence before validation and owns job/source/purpose mapping and retry accounting. P08 demonstrates that seam; the new LLM interfaces do not automatically cover embedding calls. Bridge PM12–PM14 own maintenance, retained-profile/eligibility rules, actual per-source/version coverage and coherent route/retrieval activation. Terminal failures or aggregate counts do not establish coverage.

**Media and capability gaps:** Bridge PM10 qualifies media planning, vision and image generation with shared clients where delivered or a narrow dedicated OpenAI adapter. Name an unavailable/deferred operation explicitly, block only its dispatch and preserve unaffected capabilities. No deferred operation may secretly call Google. P09 qualification covers its supplied boundaries and does not declare all zero-Gemini migration capabilities delivered.

**Required external sequence:** principal Bridge surfaces and offline reindex/recovery precede PM16's native TypeScript OpenAI implementation in Cloud's existing Worker. Share required versioned schemas/fixtures; introduce no C# service boundary. PM17/PM18 then qualify the actual integrated loops and installed return online. The Bridge migration coordinator retains that cross-repository objective. Any optional coordinated test-project reset in that plan stays with its owners and preservation rules; this Agentica plan performs no reset.

This plan authorizes planning changes inside Agentica only. External Bridge/Cloud engineering, bulk reindex runtime/spend and production effects are excluded from this Agentica estimate and P09 proof allowance. P09 supplies concrete enabling artifacts and pending external gates; full migration completion remains pending actual Bridge/Cloud acceptance. See the [complete A/PM crosswalk](BRIDGE-ALIGNMENT.md).

## 11. Sources and review provenance

- [November 6 Nyx audit](https://drive.google.com/file/d/1ERETeN85uVslhxxzblDthAanG_2T5JTX/view) and [evidence/touchpoint register](https://drive.google.com/file/d/11-CesXevBhc9ta-OAUXKXIqUFjf9J2U7/view); reviewed Agentica `cb9154da`, Bridge `187b203c`. Cloud conclusions remain from that supplied report rather than a new Cloud audit.
- Owner-supplied earlier Lumina review, November 6: native/episode continuity, provider-first migration and independent extraction/autobiographical work. Its Gemini-running rollback recommendation is historical and **superseded** by the subsequently supplied Bridge final packet and Lumina catch-up requiring an offline OpenAI-only first cutover.
- Bridge final plan (7 pages) and story plan (14 pages), both supplied November 6, plus the later Lumina catch-up. The [alignment record](BRIDGE-ALIGNMENT.md) records exact PDF hashes, A1–A6/PM01–PM18 mapping and the resolved ownership boundaries. Their contents are planning inputs; they do not authorize issue creation, provider spending, reset or service activation in this chat.
- [Agentica runtime contracts](../../Agentica.RuntimeContracts.md), [existing planning reconciliation](../../reconciliation/2026-11-03/planning-reconciliation.md), and [existing published issue snapshot](../../reconciliation/2026-11-03/published-issues.json). Older snapshots retain their original dates and do not establish today's implementation state.
- [Shared request](../../../Agentica.Clients/Llm/LlmRequest.cs), [stream events](../../../Agentica.Clients/Llm/LlmStreamEvent.cs), [client exception](../../../Agentica.Clients/Llm/LlmClientException.cs), [native continuation](../../../Agentica.Clients/Llm/LlmNativeContinuation.cs), and [OpenAI client](../../../Agentica.Clients/OpenAI/OpenAiResponsesLlmClient.cs).
- [Lab transport](../../../Agentica.Lab.Web/Chat/ExperimentProviderTransport.cs), [Lab executor](../../../Agentica.Lab.Web/Chat/OpenAiExperimentExecutor.cs), [package gate](../../../eng/Verify-Packages.ps1), [CI](../../../.github/workflows/ci.yml), and [SDK pin](../../../global.json).
- Provider wire changes must recheck current primary vendor documentation during implementation; the research review used the official [OpenAI reasoning](https://developers.openai.com/api/docs/guides/reasoning) and [web search](https://developers.openai.com/api/docs/guides/tools-web-search) guides.

**Planning verification:** source/issue reconciliation and document/manifest structural checks only. No application build, test execution, paid call or installed-runtime qualification is represented by this document.

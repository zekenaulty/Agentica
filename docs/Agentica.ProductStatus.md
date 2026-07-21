# Agentica Product Status And Goal Xref

Canonical status date: 2026-07-21

Lifecycle: Incubating

This is the authoritative high-level status page for Agentica. Detailed goal documents remain useful design records and evidence logs, but they do not independently set current priority. When another document conflicts with this page about product identity, active work, lifecycle, or completion, this page wins.

## Product Truth

Agentica is currently a source-available research repository developing and evaluating an alpha, package-shaped .NET runtime for bounded agent execution. It is not currently a supported or licensed-for-reuse public package.

North star:

```text
Models propose.
Agentica validates and authorizes.
Tools execute.
Receipts preserve every effect across every attempt.
Outcomes claim only what evidence proves.
```

The repository has two distinct identities that must not be conflated:

- `Agentica` is the candidate reusable runtime.
- `Agentica.Lab` is the internal lab executable containing scenarios, probes, benchmarks, Chat experiments, orchestration experiments, and run-inspection surfaces. It is explicitly not the supported product CLI.

The lab's growth was productive: it exposed real planning, continuity, completion, and safety failures while keeping most domain vocabulary out of the runtime. The drift was primarily role, naming, and status-authority drift rather than valueless experimentation.

The hardening gates now support internal research artifacts, not a public release. Public distribution remains deliberately deferred: the current source-available license does not permit reuse, the packages are marked research preview, no supported CLI exists, and one green no-publish CI run does not establish supported-product readiness.

## Project Role And Naming Decision

| Current project or area | Canonical role | Current product status | Intended disposition |
| --- | --- | --- | --- |
| `Agentica` | Bounded single-run planning/execution, validation, authorization, receipts, and outcomes | Alpha runtime candidate | Keep package-shaped; do not call package-ready yet |
| `Agentica/Orchestration` | Task-level adaptive supervision | Bounded proof contract is fail-closed; broader surface remains Incubating | Keep experimental and excluded from general product claims |
| `Agentica.Clients` | Provider SDK isolation and LLM planner adapters | Alpha adapter layer | Keep |
| `Agentica.Lab` | Lab, harness host, benchmark suite, demos, probes, Chat prototype, and run inspection | Internal research surface, explicitly non-packable, not a supported CLI | Keep the boundary explicit and stop routing general product experiments into it |
| `Agentica.Tests` | Runtime/client contracts and selected lab behavior tests | Strong internal verification, including package-consumer and real-process gates | Keep release gates representative and fail closed |
| Future `Agentica.Cli` | Thin supported shell over stable runtime APIs | Does not exist | Reserve the name until productization |
| Future external workspace | Independent host consuming built artifacts rather than source links | Does not exist | Required before reusable-package readiness can be claimed |

`Agentica.Lab` is preferred over `Agentica.Workbench`: WorkbenchQuest and the `workbench` command already use that term, and "workbench" sounds more product-facing than the project's actual experimental role.

Future host experiments should prefer external sibling workspaces once the runtime boundary is stable. The in-repo lab should retain compact deterministic regression fixtures and reference pressure harnesses, not become the default home for every product experiment.

## Status And Percentage Rules

Lifecycle and completion are separate:

| Lifecycle | Meaning |
| --- | --- |
| Implemented | The named slice's acceptance criteria landed and are verified. This does not mean the whole product is ready. |
| Incubating | Substantial implementation exists, but the contract or safety boundary is not ready for product claims. |
| Draft | Primarily design work or a thin substrate; the named feature is not implemented. |
| Superseded | A newer canonical plan owns the remaining work. The old document is retained for rationale. |
| Historical | A closed evidence log or snapshot that no longer sets current priority. |

Completion rubric:

- 0%: absent or contradicted.
- 25%: useful substrate or prototype exists.
- 50%: coherent partial implementation exists.
- 75%: exit criteria are mostly satisfied.
- 100%: the named criteria are verified and enforced.

Percentages are rounded to 5% and may change only with linked implementation, test, operational, or decision evidence. Historical and superseded documents are excluded from the active-program calculation.

## Program Completion

This score measures the newly agreed hardening and product-truth program, not the amount of code already written. Immediate blockers, proof integrity, and secure dispatch carry 60% of the weight.

Before this report, weighted completion was 17.75%, reported as 18%. The canonical page, complete goal xref and lifecycle banners, repository-identity decision, README alignment, and `Agentica.Lab` rename complete the T0 governance baseline.

Current weighted completion:

```text
(10*100 + 15*100 + 25*100 + 20*100 + 10*100 + 10*100 + 10*100) / 100
= 100.00%
```

**Current hardening/product-truth program: 100% complete.**

T0-T6 are complete against their bounded gates. The no-publish GitHub workflow passed on Linux, including the digest-pinned container build and smoke. This score measures the agreed hardening program, not public-product readiness; the repository remains Incubating.

| ID | Workstream | Weight | Before report | Current | Program state | Implemented credit | Remaining exit gate |
| --- | --- | ---: | ---: | ---: | --- | --- | --- |
| T0 | Re-establish one product truth | 10% | 30% | 100% | Governance baseline complete | North star, canonical page, identity decision, active slice, lifecycle taxonomy, complete goal xref, direct lifecycle banners, and `Agentica.Lab` boundary now exist | Maintain this page as the sole status authority |
| T1 | Close immediate release/security blockers | 15% | 5% | 100% | Complete | Patched native SQLite graph; external Chat image classifications; explicit Gemini planner selection; default-deny live approval; honest live-test skips | Maintain the quarantine-by-default posture and exact scoped grants from T3 |
| T2 | Harden the proof spine | 25% | 20% | 100% | Complete | Complete prior envelopes, immutable globally bounded canonical tool results, explicit immutable-input completion, exact completion evidence, high-entropy IDs, isolated bounded event observers, and mutation-safe retry authorization are enforced and adversarially tested | Maintain these invariants as registrations, planners, and hosts evolve |
| T3 | Implement a minimal security vertical slice | 20% | 10% | 100% | Complete | Frozen compiled registrations include planner projection, effect/data/output/approval/retry/provenance declarations; one canonical hash binds planning and dispatch; exact grants and immediate recompile/recheck fail closed; all five Chat/path proofs pass | Keep the slice narrow; defer the broader taint and multi-hash architecture |
| T4 | Fix or quarantine orchestration | 10% | 35% | 100% | Bounded gate complete; component still Incubating | Nonempty valid acceptance, global definition-of-done, concrete evidence, type-safe bounded host-state comparison, supported mutation vocabulary, normalized boundary failures, and failed-child regressions are enforced | Broader host integration and measured orchestration reliability are still required before product claims |
| T5 | Create a measured LLM product proof | 10% | 15% | 100% | Complete for `agentica-product-proof-v1` | Versioned strict schemas/prompts, fixed matrix, durable telemetry, strict offline reaggregation, and zero-false-success gates landed; the measured cohort passed 29/30 overall, 25/25 primary, and 4/5 holdout | Repeat on intentional model/prompt/schema changes; do not generalize from this single fixed cohort |
| T6 | Productize after the gates | 10% | 20% | 100% | Bounded research-repository productization gate complete | Exact SDK and lock files, SHA-pinned no-publish CI, clean audits, clean Release/analyzer gates, 375 passing tests with 2 honest live skips, 84.16% line/67.02% branch coverage, real Lab process tests, package validation plus an external consumer, research metadata/license, a digest-pinned non-root container build/smoke, bounded/redacted resilient Lab logs, and one complete green Linux CI run | No remaining gate in this bounded program; keep CI green while public publishing and a supported CLI remain deferred |

## Active Slice: Preserve The Green Research Baseline

The agreed hardening/product-truth program is complete. Until a successor slice is explicitly selected, active work is to keep the no-publish CI gate green, reconcile regressions against this page, and preserve the research-repository boundary. Do not infer public-package, supported-CLI, or production readiness from 100% completion of this bounded program. New harness families, routers, MCP adapters, learning systems, and broad tool taxonomies remain deferred.

### T1: Immediate Blockers

| Requirement | Completion | Current reality | Closure evidence |
| --- | ---: | --- | --- |
| Resolve the SQLite advisory | 100% | `Microsoft.Data.Sqlite` 10.0.10 plus an explicit `SQLitePCLRaw.bundle_e_sqlite3` 3.0.4 override resolves `SourceGear.sqlite3` 3.53.3; the vulnerable 2.1.11 native graph is absent | Solution-wide transitive vulnerability audit reports no vulnerable packages; Release build and SQLite Chat smoke pass |
| Classify or disable Chat external tools | 100% | Both Gemini image tools are `ExternalSideEffect` and approval-required; LocalOnly rejects them, planner guidance describes the boundary, and Chat no longer auto-selects Gemini from ambient credentials | Actual-catalog and fake-provider tests pin the classifications and deny ungranted transmission |
| Default-deny approval-required execution | 100% | Approval-required registrations cannot dispatch without an exact, unexpired grant bound to manifest hash, tool id, output class, and approved data boundaries; Chat issues no grant by default | Tests prove zero calls without a grant and successful dispatch only for the exact authorized surface |
| Report disabled live tests honestly | 100% | Opt-in Gemini facts carry a discovery-time skip reason unless `AGENTICA_RUN_LIVE_LLM_TESTS=true` | Targeted run reports two skipped tests; the full deterministic suite reports skips rather than false passes |

### T2: Proof Spine

Exit condition: every real invocation across every attempt remains reachable from the final envelope, and no mutation can repeat without explicit retry authorization.

| Requirement | Completion | Current reality | Closure evidence |
| --- | ---: | --- | --- |
| Preserve complete attempt envelopes | 100% | The final `OutcomeEnvelope` retains every complete earlier envelope in chronological `PriorAttempts`; cancellation and timeout after dispatch synthesize canonical terminal receipts, including parallel siblings | `BlockedRetryPolicyTests` and `InvocationProofTests` prove prior receipts/artifacts/observations/events and every dispatched result remain reachable |
| Restrict retries by stop reason and idempotency | 100% | A frozen `BlockedRetryPolicy` defaults to `ToolUnavailable`; `ToolRefused` is distinct; cumulative retry safety comes from validated plan steps and current registrations, never receipt claims | Adversarial tests cover stop reasons, forged identities, frozen inputs, history, retry budget, and unsafe retry classes |
| Default mutation retries off | 100% | Mutation authorization is empty by default; a repeat requires both registration-level `Idempotent` safety and an exact host-authorized tool id | Tests prove either key alone is insufficient and all non-idempotent classes fail closed |
| Validate and normalize tool results | 100% | Runtime-owned IDs, invocation identity, timestamps, canonical evidence links, and source-token aliasing replace raw tool claims; receipt, observation, artifact, and aliases share one depth/node/byte budget and retain only immutable JSON-safe values; malformed/undefined/partial results cannot become success | `ToolResultContractTests` cover forged/duplicate IDs, post-return mutation, identity round trips, deep/large JSON, binary and unknown DTOs, aggregate budgets, cycles, null and invalid results, and incomplete statuses |
| Require explicit completion policy | 100% | `AgenticaRunner` and in-process orchestration require an evaluator; evaluators receive immutable `CompletionContext`; empty evidence definitions are rejected; selected evidence must resolve; evaluator exceptions return a proof-preserving failed envelope | Constructor, evidence-link, exact-evidence, and post-mutation failure tests enforce the contract; plan exhaustion remains an explicit demo opt-in only |
| Increase identifier entropy | 100% | Runtime IDs use a full lowercase UUID-v4 `N` suffix, approximately 122 random bits, rather than eight hex characters | Format and 10,000-ID uniqueness tests pass |
| Define event-sink failure behavior | 100% | The in-memory ledger is authoritative and stores a deep-frozen, 1 MiB-bounded event snapshot; reason projectors and sinks receive separate immutable snapshots. The first observer exception records typed `EventDeliveryFailure`, circuit-breaks that sink for the attempt, and does not alter the business outcome; snapshot-limit, reporter, and projector failures retain bounded proof instead of erasing it | Hostile nested mutation, oversize snapshot, mutation and batch sink failures, throwing reporter/projector, and terminal event regressions pass |

T2 verification remains part of the solution-wide Release gate. The final local verification count is recorded under T6 after all later security, benchmark, logging, package, and process tests are included.

### T3: Minimal Security Vertical

This deliberately narrow slice is complete:

- `ToolManifestCompiler` validates and deep-snapshots planner and security declarations into a frozen compiled registration.
- The declaration includes effect, read and planner-exposure boundaries, external-output classification, approval requirement, retry safety, and provenance.
- One canonical `sha256-v1` manifest hash binds the planner projection and dispatch authority.
- The catalog is recompiled immediately before each dispatch; an invalid, missing, or changed surface is refused.
- Grants are exact, time-bounded capabilities rather than ambient approval.

Fake-provider and path-boundary tests prove all five required cases: LocalOnly rejects image generation; external transmission needs an exact grant; workspace content cannot cross an unapproved boundary; changed registrations fail closed; and junction/symlink/reparse escapes are refused.

## Closed Gates And Remaining Boundary

### T4: Orchestration

The bounded T4 exit gate is complete. Executable tasks require nonempty valid acceptance, success requires concrete child evidence plus the global definition-of-done, and host-state proof uses bounded type-preserving structural comparison rather than string conversion. Unsupported advertised mutations were removed; host projection, context compilation, child execution, acceptance, definition-of-done, planner, graph, and refinement failures normalize to truthful proof-preserving outcomes; failed-child/empty-acceptance regressions fail closed.

That does not promote orchestration to a product claim. The broader adaptive supervisor remains Incubating and experimental pending host integration, durable replay/public-contract decisions, and measured reliability evidence.

### T5: Measured LLM Proof

`agentica-product-proof-v1` fixes five WorkbenchQuest cases at five repetitions each and one MazeQuest unlock holdout at five repetitions. It pins the model, generation settings, prompt/schema/harness versions, policies, retries, timeouts, scenario parameters, and cohort identity.

The 2026-07-21 Gemini 2.5 Flash cohort passed:

| Metric | Overall | Primary Workbench | Maze holdout |
| --- | ---: | ---: | ---: |
| Verified success | 29/30 (96.7%) | 25/25 (100%) | 4/5 (80%) |
| False success | 0/30 | 0/25 | 0/5 |
| Invalid plan | 1/30 (3.3%) | 0/25 | 1/5 (20%) |
| Mean run latency | 51.77 s | 21.60 s | 202.60 s |
| Tokens | 19,529,994 | 1,506,452 | 18,023,542 |
| Estimated paid-standard cost | $5.80573728 | $0.47625259 | $5.32948469 |

Across 210 logical model calls there was one JSON repair, zero provider retries, and zero runtime retries. The immutable run records hash to `sha256-v1:cdc254965780f542c65cddf4e8bfb140cc5e7314b55afd06d6138cc101da9efd`. The first cost aggregation failed closed on implicit cached-token telemetry; strict offline reaggregation over the unchanged records added the reviewed cache-aware price model and passed. See [benchmark evidence](benchmark-results/README.md) and the [aggregate](benchmark-results/20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d/aggregate.json).

This is one fixed measured cohort, not a general reliability guarantee.

### T6: Research-Repository Productization

The repository now pins .NET SDK 10.0.302 and dependency lock files; enables recommended analyzers and Release warnings-as-errors; defines vulnerability, deprecation, format, test, coverage, package, external-consumer, Lab subprocess, broad detected-secret, container-build, and container-smoke gates in SHA-pinned no-publish CI; and marks both packages `0.1.0-research.1` under the existing source-available license. `Agentica.Lab` remains non-packable and is not a .NET tool.

Lab logs no longer retain raw argument values. They recursively redact common credential shapes and sensitive fields; bound record, file, run, root, and retention sizes; reject traversal/reparse storage; and disable themselves without changing the business result on storage failure. Redaction remains best-effort, and log serialization is not a streaming memory bound.

The final local gate on 2026-07-21 used SDK 10.0.302: locked restore, formatting, selected security analyzers, and the Release warnings-as-errors build passed; 375 tests passed, 2 opt-in live Gemini tests were explicitly skipped, and 0 failed; fresh core coverage was 84.16% line and 67.02% branch against 80%/60% floors; vulnerability and deprecation audits were clean; both research-preview packages passed validation plus a fresh temporary external-consumer build; and the Lab executable passed a real-process smoke.

The container uses exact patch tags plus immutable manifest digests and locked restore. On 2026-07-21 it built successfully on Docker Engine 27.4.0, ran `quest list` successfully, and was inspected as a 118.73 MiB image running as non-root UID/GID 1654. The no-publish GitHub workflow [run 29877458089](https://github.com/zekenaulty/Agentica/actions/runs/29877458089) then passed on commit `b934136`, including locked restore, package audits, formatting, the zero-warning Release build, selected analyzers, Lab subprocess tests, the full coverage gate, package validation, the digest-pinned container build/smoke, and detected-secret scanning.

## Goal And Progress Document Xref

Completion below measures each document's own acceptance criteria. A 100% foundational slice is still only a completed slice, not a 100% product.

| Goal or progress document | Lifecycle | Completion | Current interpretation and successor |
| --- | --- | ---: | --- |
| [Agentica CLI Campaign Goal Status](Agentica.CLI.CampaignGoalStatus.md) | Historical | 100% | Closed evidence log for the implemented campaign harness; adaptive orchestration is the successor |
| [Agentica.Clients Goal Status](Agentica.Clients.GoalStatus.md) | Historical | 95% | Closed LLM-client slice log; measured reliability was not established |
| [External Review Xref And Action Plan](Agentica.ExternalReviewXref.ActionPlan.md) | Superseded | 45% | Some May hardening closed; approval, IDs, runner split, result taxonomy, and state discipline roll into T1-T4 here |
| [Agentica Goal Status](Agentica.GoalStatus.md) | Historical | 100% | Closed evidence log for the first executable slice; it is not a live product status page |
| [Original Planning README](../Agentica.ReadMe.md) | Historical | 100% | Original development plan retained for rationale; this page and the primary README replace its status claims |
| [Agentic Harness Host](CodexGoal.Agentica.AgenticHarnessHost.md) | Incubating | 90% | Maze/Chess/Workbench prove the host pattern and the fixed product cohort exercises it; a reusable external host and broader secure promotion remain |
| [ChessQuest Hardening Backlog](CodexGoal.Agentica.Lab.ChessQuestHardeningBacklog.md) | Incubating | 75% | Many honesty, probe, threat, continuity, and opponent passes landed; defer remaining lab expansion behind external release-gate evidence |
| [ChessQuest Harness](CodexGoal.Agentica.Lab.ChessQuestHarness.md) | Incubating | 80% | Referee, probes, strategy, phases, replay, and orchestration exist; measured reliability remains incomplete |
| [MazeQuest Harness](CodexGoal.Agentica.Lab.MazeQuestHarness.md) | Implemented | 95% | Harness criteria substantially landed and the fixed T5 holdout measured 4/5 truthful completions; broader scenario reliability is not claimed |
| [WorkbenchQuest Harness](CodexGoal.Agentica.Lab.WorkbenchQuestHarness.md) | Implemented | 100% | All named harness criteria landed; the fixed T5 primary matrix measured 25/25 truthful completions |
| [Workspace Context Graph Tool](CodexGoal.Agentica.Lab.WorkspaceContextGraphTool.md) | Draft | 5% | Detailed design only; defer until path and data-boundary security exists |
| [LLM Planning](CodexGoal.Agentica.Clients.LlmPlanning.md) | Implemented | 100% | Provider bridge, strict versioned initial/refinement schemas, repair preservation, and measured fixed-cohort use are implemented |
| [Cockpit Surface Engine](CodexGoal.Agentica.CockpitSurfaceEngine.md) | Incubating | 70% | Host-specific Maze/Chess surfaces exist; generic promotion remains outside the minimal completed T3 security slice |
| [Domain Router Binding Harness](CodexGoal.Agentica.DomainRouterBindingHarness.md) | Draft | 35% | Vocabulary/design are substantial; implementation is absent and deferred |
| [Event Intent Surface](CodexGoal.Agentica.EventIntentSurface.md) | Implemented | 100% | Enriched events, intent, reasons, surfaces, frames, diagnostics, and tests landed |
| [First Executable Slice](CodexGoal.Agentica.FirstExecutableSlice.md) | Implemented | 100% | Foundational closed slice; later safety gaps do not erase its original completion |
| [Orchestrated Campaign Runs](CodexGoal.Agentica.OrchestratedCampaignRuns.md) | Implemented | 90% | First graph-shaped host campaign landed; adaptive supervision is the successor |
| [Orchestration Adaptive Supervisor](CodexGoal.Agentica.OrchestrationAdaptiveSupervisor.md) | Incubating | 90% | The bounded T4 proof contract now fails closed; broader integration, durable semantics, and measured reliability still prevent product status |
| [Run Continuity Spine](CodexGoal.Agentica.RunContinuitySpine.md) | Incubating | 85% | GoalSpine and continuity ledgers exist; continuity remains planning context, never proof |
| [Runtime Harness Gaps](CodexGoal.Agentica.RuntimeHarnessGaps.md) | Implemented | 100% | Input schemas, completion seams, continuation, effects, context shaping, and tests landed |
| [Secure Evolving Harness Tool System](CodexGoal.Agentica.SecureEvolvingHarnessToolSystem.md) | Incubating | 40% | The minimal compiled-registration, grant, manifest-recheck, boundary, provenance, and path-security vertical is implemented; broader taint, promotion, registry, and multi-hash architecture remains deferred |
| [Workflow Routing Ontology](CodexGoal.Agentica.WorkflowRoutingOntology.md) | Draft | 15% | Partial substrate only; split or defer rather than running it as a parallel active slice |
| [MazeQuest Item Usage, Puzzle Bindings, And Energy Plan](MazeQuest.ItemUsagePuzzleAndEnergyPlan.md) | Incubating | 50% | Energy and part of the affordance substrate exist; the broader control and puzzle slice remains deferred behind external release-gate evidence |

Lifecycle totals:

- Implemented: 7
- Incubating: 8
- Draft: 3
- Superseded: 1
- Historical: 4

## Deferred Experiments

The following are intentionally outside the active slice:

- additional ChessQuest, MazeQuest, HexQuest, WorkbenchQuest, or campaign feature growth;
- workspace context graph implementation;
- domain router/binding implementation;
- MCP server/client adapters;
- full multi-agent orchestration;
- self-training, skill mining, prompt evolution, or automatic policy promotion;
- the full multi-hash, taint, generated-tool, registry, and automatic-promotion architecture beyond the minimal T3 vertical slice;
- LLM outcome narration as a primary product surface;
- public package/tool/container release; and
- a supported thin `Agentica.Cli`.

## Update Discipline

Update this page:

- after every active-slice merge;
- whenever a goal document is added, superseded, or reactivated;
- whenever a percentage changes because exit evidence landed; and
- at least monthly while implementation is active.

Every percentage change must name evidence. New goal documents must be added to the xref before implementation starts. Historical documents should be retained, not silently rewritten to look current.

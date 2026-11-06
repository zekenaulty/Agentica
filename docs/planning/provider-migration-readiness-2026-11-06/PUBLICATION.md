# Provider-readiness issue publication — 2026-11-06

Owner approved v1.1 and explicitly requested GitHub issue creation. This record covers issue publication only; implementation and an execution goal have not started.

## Approved source

- Repository: `zekenaulty/Agentica`
- Code baseline: `cb9154dacb327880585d518d66ee24773b8cc1b9`
- Approved plan/alignment commit: [`b1b09d8a2d722b9572017cb560de90d0de28ddf1`](https://github.com/zekenaulty/Agentica/commit/b1b09d8a2d722b9572017cb560de90d0de28ddf1)
- Approved PLAN.md SHA-256: `5073ff144965ea79559faeca165c2ab6c6545195e56cb97db135823f6befd206`
- The current plan/manifest add administrative publication links. The immutable commit above retains the approved source used to generate issue bodies.

## Tracking hierarchy

[Program #12](https://github.com/zekenaulty/Agentica/issues/12) → [provider-readiness epic #47](https://github.com/zekenaulty/Agentica/issues/47) → nine native sub-issues below. Completed epic #14 remains closed.

| Stable story | Issue | Outcome | Native blocked-by dependencies |
| --- | --- | --- | --- |
| P01 | [#48](https://github.com/zekenaulty/Agentica/issues/48) | Define retained provider-attempt evidence and compatibility contracts | None |
| P02 | [#49](https://github.com/zekenaulty/Agentica/issues/49) | Preserve outcomes and single-attempt semantics across provider adapters | [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48) |
| P03 | [#50](https://github.com/zekenaulty/Agentica/issues/50) | Add immutable request preparation and host-owned admission | [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48) |
| P04 | [#51](https://github.com/zekenaulty/Agentica/issues/51) | Report effective provider capabilities and controls | [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48) |
| P05 | [#52](https://github.com/zekenaulty/Agentica/issues/52) | Add bounded durable exchange for existing native-continuation carriers | [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48), [P02 / #49](https://github.com/zekenaulty/Agentica/issues/49) |
| P06 | [#53](https://github.com/zekenaulty/Agentica/issues/53) | Promote OpenAI native search and citation streaming from Lab | [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48), [P03 / #50](https://github.com/zekenaulty/Agentica/issues/50) |
| P07 | [#54](https://github.com/zekenaulty/Agentica/issues/54) | Adopt shared provider contracts in Lab chat and harness paths | [P02 / #49](https://github.com/zekenaulty/Agentica/issues/49), [P03 / #50](https://github.com/zekenaulty/Agentica/issues/50), [P04 / #51](https://github.com/zekenaulty/Agentica/issues/51), [P05 / #52](https://github.com/zekenaulty/Agentica/issues/52), [P06 / #53](https://github.com/zekenaulty/Agentica/issues/53) |
| P08 | [#55](https://github.com/zekenaulty/Agentica/issues/55) | Qualify a packed provider-only host through the complete recovery loop | [P02 / #49](https://github.com/zekenaulty/Agentica/issues/49), [P03 / #50](https://github.com/zekenaulty/Agentica/issues/50), [P04 / #51](https://github.com/zekenaulty/Agentica/issues/51), [P05 / #52](https://github.com/zekenaulty/Agentica/issues/52), [P06 / #53](https://github.com/zekenaulty/Agentica/issues/53) |
| P09 | [#56](https://github.com/zekenaulty/Agentica/issues/56) | Complete release proof, bounded live OpenAI proof and Bridge handoff | [P07 / #54](https://github.com/zekenaulty/Agentica/issues/54), [P08 / #55](https://github.com/zekenaulty/Agentica/issues/55) |

## Verification

- Ten unique open issues found by exact publication markers: one epic plus nine stories.
- Native program/epic hierarchy and all nine native story children verified through GitHub API reads.
- All 19 native blocked-by edges exactly match the approved dependency DAG.
- Story bodies include complete acceptance checkboxes, source/approval/estimate, dependency URLs, shared scope/invariants/rollback and relevant Bridge references.
- A1–A6 crosswalk links are populated in [stories.json](stories.json) and [BRIDGE-ALIGNMENT.md](BRIDGE-ALIGNMENT.md).
- P08 explicitly retains the embedding known-usage/invalid-vector case and captured route/retrieval-profile recovery.
- P09 preserves the isolated US$5 proof allowance and separate required Bridge reindex/native TypeScript Cloud/installed gates.
- Existing #10/#24/#25/#26/#31 acceptance remains independent; completed #21/#22/#23/#45 was not recreated.

No application build/test, paid inference, production operation, external Bridge/Cloud mutation or goal creation was performed by publication. The primary checkout's pre-existing dirty work was preserved.

---
name: agentica-test-suites
description: Select, qualify, or mechanically separate Agentica capability test suites while preserving the complete aggregate and existing coverage gate. Use for focused test orchestration or lift-and-shift test-project work, not runtime rewrites or live-provider activation.
---

# Agentica Test Suites

Use the existing `test-orchestration` skill when available for planning, execution and compact evidence handoffs; use `agentica-contract-work` only for an unresolved runtime/host contract. Select one relevant branch and return here without repeatedly loading the chain. This repository guide remains usable without either personal skill.

Read [capability suite guidance](../../../docs/testing-capability-suites.md) for the current project map, commands and lift-and-shift rules. Confirm the checkout, source/delta, SDK pin, selected capability and required evidence level before running tests.

- `Agentica.Clients.Tests` is a focused deterministic provider suite. `Agentica.Tests` is the complete aggregate and remains the root solution's test entrypoint.
- The aggregate links the same physical provider test sources. Keep the overlapping focused project outside the solution; an explicit run of both must be reported as overlapping validation, not extra unique tests.
- Preserve the aggregate coverage gate, discovery population and live-provider opt-ins. Focused green evidence cannot clear an unresolved full-suite failure.
- When a capability split has a concrete build, fixture or ownership benefit within the current authorized task, use an isolated worktree and PR. Move unchanged files and only the project wiring required to preserve their behavior. If the split requires test/helper redesign, choose a smaller boundary or retain the existing assembly.
- Compare source hashes, discovery identities and selected outcomes before/after, then verify the complete aggregate. Keep baseline failures separate from regressions and state any unexecuted gate.

For a handoff, retain only objective/acceptance, exact source and fixture identity, selected project/filter, environment, test counts/outcomes, evidence location and next unresolved check. Recheck relevant changes before reusing a pass. These instructions provide no production or paid-provider authority.

# Agentica Deferred Work

This file names work that is intentionally outside the current Agentica core stabilization slice.

## Higher-Level Orchestration

Status: incubating in core.

The reusable orchestration contracts were migrated into `Agentica/Orchestration` to discover and stabilize the generic task-level supervision shape beside the bounded run runtime. This is intentional incubation, not a final package boundary.

Long-horizon orchestration should still become a separate `Agentica.Orchestration` package if the contracts stabilize as reusable API surface, durable state/replay becomes real, or the orchestration layer starts changing independently from the bounded run runtime.

Current host campaign code in `Agentica.CLI/Scenarios/Campaign` is a harness and design probe, not core runtime identity.

Deferred:

- separate orchestration package split
- campaign persistence
- campaign resume
- task routing/classification
- scheduler/queue/background jobs

## Planner Routing And Classification

Classification should happen at run construction time, outside `AgenticaRunner`.

Deferred until multiple real harnesses provide empirical decision boundaries:

- `ITaskRouter`
- `TaskRoute`
- automatic planner selection
- automatic planning mode selection
- complexity classification

## Working Context

Agentica may later need a generic structured working-context seam.

Deferred from core:

- runtime-owned `WorkingContext`
- LLM summarizer for context compaction
- note proposal schema
- note acceptance policy

Do not add a general `create_note` tool yet. System-curated context should come first.

## MCP Adapters

Agentica remains MCP-adapter-ready but MCP-independent.

Deferred:

- expose Agentica as an MCP tool
- consume remote MCP tools as `ToolDescriptor` / `ToolResult`
- JSON-RPC transport concerns

## Persistence

Persistence belongs at the host boundary for now.

Deferred:

- run store
- resume store
- artifact persistence provider
- event sink persistence provider beyond CLI run logs

## Prompt Optimization

`Agentica.Clients` owns provider-specific LLM planning adaptation.

Deferred:

- model-specific planner profiles
- LLM task classifier
- LLM working-context summarizer
- prompt evaluation suite
- prompt versioning

## Training And Adaptation Lab

Future self-refinement, skill mining, prompt evolution, descriptor improvement, and risk-profile drafting belong in a separate sandboxed lifecycle, not inside live `AgenticaRunner` execution.

See `docs/CodexGoal.Agentica.SecureEvolvingHarnessToolSystem.md` for the current planning boundary.

Deferred:

- trace intake and sanitization pipeline
- LLM-assisted descriptor/risk/prompt candidate generation
- deterministic and adversarial evaluation matrix
- human or trusted maintainer promotion gate
- signed or pinned manifest promotion
- canary/shadow-mode rollout

An LLM may draft risk classifications, but runtime enforcement needs a deterministic taxonomy or trusted pinned manifest. If neither exists, external or unknown-risk tools must fail closed.

## Harnesses

Harnesses are host pressure tests.

Deferred:

- procedural dungeon crawler
- procedural campaign graph generator
- CLUE-style multi-agent deduction
- multi-agent orchestration
- richer Workbench campaign flows

## Runtime Feature Restraint

Do not add new core runtime concepts until real use cases force them.

Before promoting anything from host harness to core runtime, require:

- at least two host scenarios needing the same abstraction
- tests proving the abstraction is generic
- no domain vocabulary leakage
- no weakening of the proof envelope

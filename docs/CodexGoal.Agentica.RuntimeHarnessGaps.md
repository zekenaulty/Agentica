# Codex Goal: Agentica Runtime Harness Gaps

## Mission

Close the generic runtime gaps needed for richer host-provided test harnesses such as maze-backed quest scenarios.

Agentica must remain a planner/executor runtime. It must not absorb maze, quest, game, storage, MCP, or product vocabulary.

## Required Runtime Slices

1. Add generic tool input contracts to `ToolDescriptor`.
2. Validate planned tool inputs before execution.
3. Add a completion evaluator seam so success is not only plan exhaustion.
4. Add a bounded continuation/replanning hook when a plan is exhausted but completion is not proven.
5. Add a generic tool effect policy gate.
6. Add planner context shaping so planners can receive bounded recent observations/receipts while the full envelope remains intact.

## Non-Negotiable Boundaries

- Do not add maze, quest, CYOA, game, room, cell, hazard, reward, fog, pathfinding, or board concepts to `Agentica`.
- Do not add runtime projects.
- Do not change `Agentica` to reference `Agentica.Clients`.
- Host-specific reasons stay in receipt/observation data.
- Narrative reports remain narrative. Receipts, artifacts, observations, validation issues, blockers, and stop reasons remain proof.

## Acceptance Criteria

- `ToolDescriptor` can describe required inputs, allowed values, examples, and compact type/range constraints.
- `AgenticaRunner.ValidatePlan` rejects missing required inputs, unknown inputs when forbidden, invalid enum values, invalid types, and out-of-range numeric values.
- Mutation-capable execution remains blocked unless tool kind/effect/schema/policy validation passes.
- A host can require evidence, such as an artifact kind, before success is allowed.
- If completion is not proven, a host can request bounded continuation planning instead of false success.
- Default deterministic proof slices remain backward compatible.
- `ExecutionPolicy` can restrict allowed `ToolEffect` values.
- `PlanningRequest` receives bounded recent observations/receipts when configured.

## Verification

Run before completion:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
dotnet run --project Agentica.CLI -- quest run sun_gate --route blocked --planning-mode stepwise
```

## Completion Condition

This goal is complete when all acceptance criteria are implemented, tests cover the new behavior, and the verification commands pass.

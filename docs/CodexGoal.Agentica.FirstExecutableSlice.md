# Codex Goal: Agentica First Executable Proof Slice

## Mission

Implement Agentica's first executable proof slice.

Agentica is a compact .NET planner/executor runtime package for bounded tool-using workflows. It is package-first and in-process-first. It is not MCP-first, storage-first, microservice-first, or project-sprawl-first.

The core runtime shape is:

```text
RunRequest
  -> WorkflowPlan
  -> query/read tool step
  -> Observation
  -> explicit PlanRefinement
  -> action tool step
  -> Receipts
  -> OutcomeEnvelope
```

The host application owns domain state, domain tools, policy, approvals, storage, and presentation. Agentica owns the run contract, planning/execution loop, validation, observations, receipts, events, and terminal outcome envelope.

## Non-negotiable architecture constraints

1. Keep the solution small.
2. The central runtime package remains `Agentica`.
3. The console host remains `Agentica.CLI`.
4. Do not create `Agentica.Core`, `Agentica.Runtime`, `Agentica.Abstractions`, `Agentica.Storage`, `Agentica.Persistence`, `Agentica.Tools`, or other project-per-concern layers.
5. You may add exactly one test project, `Agentica.Tests`, only if needed for automated coverage. A test project is acceptable; runtime-layer project sprawl is not.
6. Do not implement MCP support yet.
7. Do not implement real LLM-backed planning yet.
8. Do not implement database recording yet.
9. Do not implement vector memory, adaptive learning, multi-agent planning, queues, auth, deployment, or storage ownership.
10. Do not import BookForge, Nanda, MazeQuest, authoring, book, scene, branch, or product-specific vocabulary into Agentica contracts.

## Required source alignment

Before coding, read and align with:

- `Agentica.ReadMe.md`
- `docs/Agentica.ObjectGraph.md`
- `docs/Agentica.Design.Decisions.md`

Use those docs as the source of truth. If the code reveals a needed correction, update the docs narrowly and explain why in the goal status log.

## Required runtime concepts

Implement only the first-slice version of these concepts:

- Requests: `RunRequest`, `RequestOrigin`
- Runs: `AgenticaRun`, run status/outcome status, options if needed
- Planning: `WorkflowPlan`, `PlanStep`, `IWorkflowPlanner`, `PlanningRequest`, `PlanRefinement`
- Tools: `ToolCatalog`, `ToolDescriptor`, `ToolKind`, `ToolEffect`, `ITool`, `ToolInvocation`, `ToolResult`
- Observations: `Observation`, `ObservationKind`, `EvidenceRef`
- Artifacts/receipts: `Artifact`, `Receipt`, `ReceiptStatus`
- Events: `ExecutionEvent`, `ExecutionEventType`, `IEventSink`, `ConsoleEventSink`, `InMemoryEventSink`
- Execution: `AgenticaRunner`, `ExecutionPolicy`
- Outcomes: `OutcomeEnvelope`, `RunOutcome`, `OutcomeReport`, `ReceiptEnvelope`, `DetailEnvelope`, deterministic reporter

## Required first console behavior

This command must work:

```powershell
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

It must emit an operator-visible event stream with this logical order:

```text
run.created
request.accepted
plan.created
step.started
observation.made
receipt.emitted
plan.refined
step.started
receipt.emitted
outcome.reported
run.succeeded
```

After the event stream, it must print a serialized `OutcomeEnvelope` JSON object.

## Required validation behavior

Before executing any step, Agentica must validate:

1. The tool exists in the `ToolCatalog`.
2. The step kind matches the tool descriptor.
3. The step effect matches the tool descriptor.
4. Mutation-capable tools are not executed as hidden query/read behavior.
5. Unknown tools fail before execution.
6. Every executed tool invocation emits a receipt.
7. Runs may stop as blocked or failed without synthesizing false success.

## Required verification commands

Run these before considering the goal complete:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

## Milestones

### Milestone 0: Orientation and baseline

- Read alignment docs.
- Inspect current project layout.
- Run `dotnet build Agentica.slnx`.
- Create or update `docs/Agentica.GoalStatus.md`.
- Record baseline project shape, build result, and existing placeholders.

### Milestone 1: Contract skeleton

Implement first-slice contracts in focused folders/namespaces under `Agentica`.

### Milestone 2: Deterministic planner and tool catalog

Implement deterministic planner, tool catalog, query/action descriptors, and fake/local query/action tools.

### Milestone 3: Runner loop

Implement `AgenticaRunner` so it can create a run, emit events, validate plans, execute query/action steps, record observations/receipts/artifacts, explicitly refine, and complete with an outcome envelope.

### Milestone 4: Console slice

Update `Agentica.CLI` so `run "<objective>"` emits the event stream and full JSON `OutcomeEnvelope`.

### Milestone 5: Outcome envelope and deterministic report

Implement final envelope shape and evidence-cited deterministic report.

### Milestone 6: Boundary tests

Add or update tests for validation, receipts, refinement, blocked stops, and JSON envelope consumption.

### Milestone 7: Steelman review and stress test

Before declaring completion, answer drift-control questions in `docs/Agentica.GoalStatus.md`.

### Milestone 8: Final verification and completion report

Run all required verification commands and update the status log with final evidence.

## Completion condition

This goal is complete only when:

1. `dotnet build Agentica.slnx` passes.
2. `dotnet test Agentica.slnx` passes.
3. The required CLI command runs successfully.
4. The CLI emits the required event sequence.
5. The CLI prints a full `OutcomeEnvelope` JSON object.
6. The outcome envelope contains outcome, report, receipts, and details.
7. The report claims are grounded in evidence refs.
8. Unknown tools fail before execution.
9. Query observation causes explicit plan refinement.
10. Every executed tool invocation emits a receipt.
11. No forbidden runtime project sprawl was introduced.
12. No MCP, storage, database, queue, auth, deployment, vector memory, or real LLM implementation was added.
13. `docs/Agentica.GoalStatus.md` contains milestone logs and final verification evidence.

Do not stop after producing a plan. Do not stop after docs-only edits. Do not stop after partial scaffolding. Do not declare success without running verification commands.

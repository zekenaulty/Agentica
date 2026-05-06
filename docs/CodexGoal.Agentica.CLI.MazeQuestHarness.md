# Codex Goal: Agentica.CLI MazeQuest Harness

## Mission

Build a host-provided MazeQuest test harness inside `Agentica.CLI`.

The goal is to create a small, deterministic, user-watchable game stage that gives Agentica and an LLM planner a higher-value reasoning surface than the static quest path:

```text
Quest Board objective
  -> deterministic generated maze stage
  -> fog-of-war observations
  -> hot/cold objective sensing
  -> local move evaluation
  -> tool-backed traversal
  -> receipts and artifacts
  -> OutcomeEnvelope
```

This is not an Agentica runtime feature. It is a CLI host scenario that supplies domain state, tools, rendering, and optional narration around Agentica's existing planner/executor loop.

Core distinction:

```text
Agentica is not the game.
Agentica navigates the game through tools.
```

## Non-Negotiable Boundary

This work must stay host-side.

Allowed write scope:

```text
Agentica.CLI/
docs/
```

Do not modify:

```text
Agentica/
Agentica.Clients/
Agentica.Tests/
Agentica.slnx
```

The other Agentica implementation thread owns runtime changes. If the MazeQuest harness reveals missing runtime capabilities, document the need and work around it in the CLI where reasonable.

The CLI may reference existing Agentica runtime and client APIs, but MazeQuest concepts must not become runtime vocabulary.

## Why Do This

The current static quest is useful, but it is too small:

```text
get state -> list legal actions -> move east -> take key -> move west -> unlock gate -> move north
```

A competent LLM can often one-shot that path after seeing one observation.

MazeQuest adds useful planning pressure without building a real game:

- The full map is hidden.
- The agent sees only local fog-of-war state.
- Objectives can be sensed but not directly routed.
- Legal moves have costs, hazards, rewards, and frontier value.
- The planner has to decide whether to scan, move, take, use, rest, or complete.
- Wrong moves produce refused receipts or hazard receipts, not narrative success.
- The user can watch the run unfold.

This tests real behaviors:

- Partial observability.
- State grounding.
- Local search.
- Risk/reward tradeoffs.
- Plan refinement after observations.
- Recovery from blockers.
- Receipt-backed outcome reporting.
- LLM usefulness beyond a short static path.

## Why Not To Do This

The risks are scope and signal quality:

- It can become a game engine.
- Maze generation can grow until prompts and runs become unbounded.
- Pathfinding help can accidentally become a route oracle.
- Too little guidance can degrade into random walking.
- Too much narration can obscure execution truth.
- Real LLM smoke runs can be flaky if completion requires optimal play.
- Console interaction can complicate cancellation if it is wired too deeply into runtime control.

Mitigation:

- Keep the first stage small and deterministic.
- Cap maze size, steps, scans, and narration calls.
- Expose guidance, not a path.
- Treat narration as display-only.
- Keep receipts/artifacts as the only proof.
- Implement pause/stop in the CLI host loop, not in Agentica runtime.

## First MVP

Create a new scenario folder:

```text
Agentica.CLI/Scenarios/MazeQuest/
```

The first maze should be intentionally small:

```text
size: 11x11
seeded generation: yes
visibility radius: 2
start: one cell
objectives:
  1. find sun_key
  2. unlock sun_gate
  3. reach exit
  4. complete objective
hazards: 3
rewards: 2
health: small integer
energy: small integer
combat: no
async/GCD timing: no in MVP
```

The current static quest should remain as the baseline smoke test. MazeQuest is the higher-value LLM reasoning harness.

## Quest And Maze Relationship

Keep quests as the user's task surface:

```text
Quest Board:
  The Sun Gate Maze
    Find the sun key.
    Open the sun gate.
    Reach the exit.
```

The maze is the stage that contains objective objects:

```text
Maze owns:
  topology
  fog of war
  cell weights
  hazards
  rewards
  objective placement
  player state

Quest owns:
  objective chain
  completion rules
  terminal artifact shape
```

The agent should never receive the hidden full route.

## Maze Cell Model

Use simple deterministic cell data:

```text
Cell
  x
  y
  terrain: wall | floor | door | exit
  traversalCost: 1-5
  hazard: none | spike | darkness | trap
  hazardRisk: 0.0-1.0
  reward: none | health | energy | clue
  objectiveItem: none | sun_key
  lockId: optional
  discovered: bool
  visible: bool
```

The generator may use weighted placement, but it must remain cheap and deterministic.

Avoid borrowing large systems from `D:\maze-battle` directly. It can be mined for ideas such as generation, hazard seeding, depth scaling, or cooldown cadence, but the first CLI harness should be fresh, small, and easy to reason about.

## Fog-Of-War ASCII

Expose an ASCII map to the agent and user as a grounding aid.

Example:

```text
???????
??#..??
??#@..?
??!.#??
???G???
```

Legend:

```text
@ agent
? unknown
# wall
. floor
! visible hazard
+ visible reward
K visible key
G visible gate
E visible exit
```

Rules:

- Show explored and currently visible cells.
- Do not show hidden key/gate/exit until discovered.
- Keep render dimensions bounded.
- Include current position, health, energy, step count, and active objective beside the map.
- The ASCII render is an observation/display artifact, not proof by itself.

## Tool Surface

Keep the tool catalog small:

```text
maze.get_state
  Query, ReadOnly.
  Returns position, health, energy, inventory, active objective, discovered counts, and run limits.

maze.render_map
  Query, ReadOnly.
  Returns fog-of-war ASCII and legend.

maze.scan
  Query, ReadOnly or Action/WritesLocalState depending on runtime support.
  Reveals cells in local radius. If scan changes discovered state, it must be an action.

maze.sense_objective
  Query, ReadOnly.
  Returns hot/cold, bearing, distance band, delta, and confidence for the active objective.

maze.evaluate_moves
  Query, ReadOnly.
  Scores currently legal moves by visible cost, visible risk, frontier gain, and objective signal delta.

maze.move
  Action, WritesLocalState.
  Moves one cell if legal; applies cost, hazards, discoveries, and emits a receipt.

maze.take
  Action, WritesLocalState.
  Takes visible rewards or objective items from the current cell.

maze.use
  Action, WritesLocalState.
  Uses inventory on a visible target, such as sun_key on sun_gate.

maze.rest
  Action, WritesLocalState.
  Recovers limited energy/health if the current cell permits it.

maze.complete_objective
  Action, WritesLocalState.
  Emits terminal quest artifact only when the objective chain is satisfied.
```

If `ToolDescriptor` input schemas are not available yet, make tool receipts and observations extremely explicit so LLM refinements can recover from bad arguments.

## Guidance Without Rails

Do not expose the actual path.

`maze.sense_objective` should return a weak objective signal:

```json
{
  "objectiveId": "sun_key",
  "bearing": "north_east",
  "distanceBand": "medium",
  "warmth": 0.62,
  "deltaFromPrevious": "warmer",
  "confidence": 0.8
}
```

`maze.evaluate_moves` should explain tradeoffs, not choose for the agent:

```json
{
  "legalMoves": [
    {
      "direction": "north",
      "terrainCost": 1,
      "visibleRisk": 0.1,
      "objectiveDelta": "warmer",
      "frontierGain": 2
    },
    {
      "direction": "east",
      "terrainCost": 1,
      "visibleRisk": 0.7,
      "objectiveDelta": "warmer",
      "frontierGain": 1
    },
    {
      "direction": "south",
      "terrainCost": 2,
      "visibleRisk": 0.0,
      "objectiveDelta": "colder",
      "frontierGain": 3
    }
  ]
}
```

This avoids both failure modes:

```text
Too much help:
  The harness becomes a route follower.

Too little help:
  The agent randomly walks based on visible hazards.
```

## Console Watch Mode

The CLI should let the user watch the agent play.

Add command shape:

```powershell
dotnet run --project Agentica.CLI -- mazequest list
dotnet run --project Agentica.CLI -- mazequest run --watch
dotnet run --project Agentica.CLI -- mazequest run sun_gate_maze --planner deterministic
dotnet run --project Agentica.CLI -- mazequest run sun_gate_maze --planner gemini --watch
```

Watch mode should show:

```text
quest accepted
current map
agent step
tool call
receipt status
state delta
short narration
outcome
```

The watch renderer should be host-owned. It must not become proof.

The `OutcomeEnvelope` remains the machine-readable result at the end.

Current implementation:

- `mazequest run` may omit the quest id; the CLI defaults to the first board quest and builds the host `RunRequest` from the generated quest objective.
- If `--planner` is omitted and console input is interactive, the CLI asks `Run Gemini-backed MazeQuest planner test? [y/N]:` before the run starts.
- `y` routes to the Gemini planner, `n` or Enter routes to the deterministic host planner.
- Non-interactive or redirected runs do not prompt and keep the deterministic planner unless `--planner gemini` is explicit.
- `--watch` uses a MazeQuest-specific event sink instead of parsing normal console output.
- Watch mode prints the host self-prompted RunRequest, current visible map, step/tool, receipt status, state summary, objective signal, legal moves, and narration.
- `--turn-json` emits a display-only `MazeQuestTurnEnvelope` after each receipt. This is for operator/debug visibility only; the final `OutcomeEnvelope` remains the proof surface.
- `--watch-delay-ms <ms>` can slow deterministic runs enough for a human to follow.

## Narration Layer

Add a thin optional narration layer that makes turns readable for the user.

It should take only public execution data:

- Current objective.
- Fog-of-war ASCII.
- Last plan step.
- Last observation.
- Last receipt.
- Current visible state.

It should not receive hidden map data.

It should not invent success.

It should output concise display text such as:

```text
The agent scans the nearby corridor and sees a risky eastern route.
It chooses north because the key signal is warmer and the visible risk is lower.
```

Preferred implementation:

```text
IMazeQuestTurnNarrator
  DeterministicMazeQuestTurnNarrator
  LlmMazeQuestTurnNarrator
```

The LLM narrator is separate from the planner. It is display-only and must be disabled or fall back to deterministic narration if credentials are missing.

Recommended flags:

```powershell
--watch
--narrator deterministic|gemini|off
--narration-model <model-id>
--turn-json
```

Hard limits:

- One short narration call per visible turn at most.
- Max narration tokens should be small.
- Narration failures should not fail the run.
- Narration should never be used as evidence in `OutcomeEnvelope`.

## Pause And Stop

The user needs to be able to pause or stop the agent.

Since Agentica runtime control is owned by the other implementation thread, implement CLI-side control first.

Recommended first behavior:

```text
p or pause
  Pause before the next tool step or before printing the next watched turn.

r or resume
  Resume a paused run.

s or stop
  Cancel the current run through CancellationToken.

q or quit
  Alias for stop in watch mode.
```

If the current Agentica runner does not expose step-by-step host control, the initial implementation can run in small bounded segments only if runtime support exists. Otherwise document the limitation and support cancellation through `CancellationToken` while the run is active.

Do not block non-watch mode on console input.

Do not put pause/stop semantics into MazeQuest tools unless needed as a temporary host workaround.

Current implementation status:

- `mazequest run` has a bounded `ExecutionPolicy.Timeout`.
- `--timeout-seconds <seconds>` lets smoke runs cap long LLM calls.
- Ctrl+C is wired to the runner `CancellationToken` and should produce a cancelled/blocked envelope instead of leaving the host run uncontrolled.
- In watch mode, `p` pauses before the next tool step, `r` resumes, and `s` or `q` cancels the run through the host `CancellationToken`.
- If console input is redirected, watch mode prints that interactive controls are unavailable and still supports timeout/Ctrl+C cancellation.

## Maze Growth And Safety

Be careful because `D:\maze-battle` uses depth-scaled maze generation. MazeQuest should not inherit unbounded growth.

MVP hard caps:

```text
max width: 11
max height: 11
max steps: 120
max refinements: 120
max scans: 30
max narrator calls: 80
max prompt map size: local/fog bounded only
```

Future growth can be staged:

```text
stage 1: 11x11 fixed
stage 2: 15x15 fixed
stage 3: depth-scaled sections with hard cap
stage 4: cooldown/GCD simulation
```

Do not add combat, procedural story, inventory sprawl, graphics, persistence, or MCP in the MVP.

## Runtime Readiness Requests For The Agentica Thread

MazeQuest can start in CLI-only form, but the useful real-LLM maze smoke depends on generic runtime and client improvements owned by the other Agentica thread.

Current readiness:

- Agentica can already run a maze harness if the CLI host supplies tools and a deterministic planner.
- The runner already has tool catalogs, validation, observations, blocker refinement, receipts, events, planning modes, and outcome envelopes.
- The runtime gap slice has now landed in the Agentica thread: tool input contracts, pre-execution input validation, effect policy, evidence-gated completion, continuation on incomplete plans, bounded planner context, and `RunRequest.Context`.
- The remaining weak points for LLM maze runs are now prompt quality, planner behavior, and watch-mode ergonomics, not terminal correctness.

Generic runtime slices requested from the Agentica thread:

1. Tool input contracts.
   Add generic input metadata to `ToolDescriptor`: descriptions, required fields, allowed enum values, examples, and maybe a compact schema object. Maze tools such as `maze.move`, `maze.sense_objective`, `maze.take`, and `maze.use` need exact inputs without model guessing.

2. Pre-execution input validation.
   Once descriptors include input contracts, Agentica should validate planned step inputs before execution. Missing `direction`, invalid enum values, malformed coordinates, and illegal extra inputs should fail or block before mutation-capable execution.

3. Completion evidence gate.
   Add a generic completion evaluator seam so success is not just "no more plan steps." MazeQuest should succeed only when receipt/artifact-backed completion evidence exists, such as `mazequest.objective_completed`.

4. Continue or replan on incomplete.
   After plan exhaustion, the completion evaluator should be able to return `Complete`, `Continue`, `Blocked`, or `Partial`. If it returns `Continue`, Agentica asks the planner for another bounded slice instead of declaring success.

5. Effect policy gate.
   Add generic policy for allowed effects: `ReadOnly`, `WritesLocalState`, `ExternalSideEffect`, and `Destructive`. Agentica should not know what "burn gate" means, but it can refuse `ToolEffect.Destructive` unless policy allows it.

6. Planner context shaping.
   Keep full run details in the envelope, but let planner/client code select recent receipts, recent observations, tool descriptors, and host-provided state summaries. MazeQuest histories should not become unbounded prompts.

7. Result classification polish.
   `ReceiptStatus.Refused` and `Unavailable` are enough for the first maze. Later, generic blocker classification may help: invalid input, policy refusal, environmental blocker, cooldown, or missing precondition. Host-specific reasons stay in `Receipt.Data`.

Additional host-facing runtime support:

1. Host cancellation.
   A running CLI watch mode needs reliable cancellation through `CancellationToken`.

2. Optional step callback or event hook.
   The CLI should be able to render after each event/tool result without parsing console text.

3. Partial outcome semantics.
   If the agent finds the key but fails to reach the exit, the envelope should represent partial completion honestly.

4. Approval/wait semantics.
   Later hazards or destructive actions may require approval or policy stops.

These runtime-facing needs are now mostly satisfied for deterministic and first Gemini smoke runs. Remaining watch controls and narration are CLI host work, not runtime blockers.

## Alignment With Agentica Runtime Work

The MazeQuest host must not compensate for missing generic runtime concepts by leaking maze concepts into Agentica.

Keep out of Agentica:

- Rooms.
- Fog of war.
- ASCII maps.
- Hazards.
- Rewards.
- Pathfinding.
- Objective placement.
- Hot/cold signals.
- Quest boards.
- Maze generation.
- GCD or cooldown vocabulary.

Safe host observation data:

- `visibleMapAscii`
- `visibleCells`
- `legalActions`
- `objectiveSignals`
- `moveEvaluations`
- `health`
- `energy`
- `inventory`
- `knownBlockers`
- `objectiveProgress`

Host implementation can proceed in two lanes:

1. Deterministic lane now.
   Build `mazequest list`, deterministic generation, fog render, tools, watch mode, deterministic narrator, and deterministic planner fixtures.

2. LLM lane after runtime slices 1-4.
   Add Gemini planner smoke once tool input contracts, pre-execution input validation, completion evidence gating, and continue-on-incomplete behavior exist.

Slices 1-4 are now available, so Gemini failures should be higher signal: bad tool choice, bad exploration strategy, invalid structured output, or genuine inability to complete under the bounded maze policy.

## Acceptance Criteria

The first MazeQuest harness is complete when:

1. `mazequest list` shows at least one quest.
2. `mazequest run sun_gate_maze --planner deterministic --watch` shows the agent playing through a small maze.
3. The map render uses fog of war and never prints the hidden full route.
4. The agent can use tools to scan, sense, evaluate, move, take, use, and complete.
5. Every state-changing tool emits a receipt.
6. The terminal objective emits a `mazequest.objective_completed` artifact only when state proves completion.
7. The final `OutcomeEnvelope` can be consumed as JSON without parsing console text.
8. Watch narration is display-only and not used as proof.
9. Pause/stop is available in watch mode, or the limitation is clearly documented if the current runner does not allow true step-level pause yet.
10. Hard caps prevent unbounded maze growth or runaway narration calls.

## Implementation Progress

Current CLI-only generation slice:

- Added `Agentica.CLI/Scenarios/MazeQuest/`.
- Added `mazequest list`.
- Added `mazequest preview` / `mazequest generate`.
- Added `--type` to force quest archetypes for coverage checks.
- Added seeded 7x7 to 11x11 odd-sized maze generation.
- Added deterministic objective placement along a solvable path.
- Added generated non-kill quest archetypes:
  - `Unlock`: dependency order, blocker recovery, inventory use.
  - `Collect`: repeated collection, inventory count, route planning.
  - `Delivery`: pickup/dropoff, destination memory, backtracking.
  - `Explore`: fog coverage, frontier selection, location discovery.
  - `Activate`: object interaction, multi-target state changes.
  - `PuzzleSequence`: ordered interactions and state memory.
  - `Rescue`: retrieval, return route, partial-success pressure.
  - `ResourceRoute`: resource management, risk/reward, hazard avoidance.
- Added generated quest objects with object ids, object kinds, target points, required items, and coverage tags.
- Added weighted hazard and reward placement.
- Added limited affix/suffix decoration for objective items and small reward flavor.
- Added fog-of-war ASCII rendering.
- Added optional `--reveal` developer map that is intentionally not part of the agent-facing surface.
- Added hot/cold objective sensing with bearing, distance band, warmth, and confidence.
- Added range-scoped local move evaluation with legal/blocker state, visible risk, cost, objective delta, and frontier gain.
- Added public snapshot JSON for future tool-surface wiring, including quest type, coverage tags, objective chain, visible quest objects, local weights, objective signal, and move evaluations.
- Added `mazequest run`.
- Added a MazeQuest host tool catalog:
  - `maze.get_state`
  - `maze.render_map`
  - `maze.scan`
  - `maze.sense_objective`
  - `maze.evaluate_moves`
  - `maze.move`
  - `maze.take`
  - `maze.use`
  - `maze.rest`
  - `maze.complete_objective`
- Added tool input schemas for mutation tools, including allowed movement directions and required object/target ids.
- Added a runtime `MazeQuestSession` that owns mutable host state, receipts, observations, visible snapshots, legal actions, hazard effects, inventory, objective progress, and the terminal `mazequest.objective_completed` artifact.
- Added a deterministic MazeQuest planner that produces bounded one-step plan slices against the generated stage. This is a host-side oracle for smoke and regression checks, not the LLM test.
- Wired `mazequest run` through `AgenticaRunner` with evidence-gated completion, bounded planner context, and continuation support.
- Added a MazeQuest outcome reporter that reports success only from receipts/artifacts, or honest blockage if the run cannot complete.
- Added `--timeout-seconds` for `mazequest run`.
- Wired Ctrl+C cancellation for MazeQuest runs.
- Added richer `--watch` playback using a host-owned MazeQuest event sink.
- Added display-only deterministic narration with `--narrator deterministic`.
- Added display-only Gemini narration with `--narrator gemini`; narration failures fall back to deterministic text and do not fail the run.
- Added `--narrator off`, `--narration-model`, `--turn-json`, and `--watch-delay-ms`.
- Added interactive watch controls: `p`, `r`, `s`, `q`.
- Added no-quest-id self-start for `mazequest run`; the CLI picks the first quest board entry and creates the RunRequest itself.
- Verified deterministic completion for all generated non-kill archetypes:
  - `unlock`
  - `collect`
  - `delivery`
  - `explore`
  - `activate`
  - `puzzle`
  - `rescue`
  - `resource`

Remaining CLI host work:

- Gemini planner smoke stabilization. Credentials are available locally and the normal two-step Gemini planner smoke passes, but MazeQuest Gemini planner calls are still too slow/noisy for a green traversal smoke. A short MazeQuest Gemini timeout smoke returns an honest `PlannerUnavailable` blocker when the LLM call is cancelled.
- Optional polish: persist watch transcripts or turn envelopes to a file if later analysis needs them.

## Prompt Engineering Pass

MazeQuest host prompts and tool descriptors should reduce avoidable first-test failures without hiding useful planner failures.

Current mitigations:

- LLM input guessing:
  - Mutation tool schemas require exact fields: `direction`, `objectId`, `targetId`, optional `item`.
  - `maze.move` only accepts lowercase `north`, `east`, `south`, `west`.
  - Tool descriptions now tell the model to copy exact values from `legalActions`, `currentObject`, or `requiredItem`.

- Over-planning:
  - The host RunRequest says to produce only the next safe step or very small safe slice.
  - It explicitly says not to produce a full route from fog-of-war data.

- Premature completion:
  - MazeQuest configures `EvidenceCompletionEvaluator.ForArtifactKind("mazequest.objective_completed")`.
  - The host RunRequest says to call `maze.complete_objective` only when legalActions includes it or all non-complete objectives are receipt-backed.

- Weak tool descriptions:
  - Query tools distinguish map rendering, scanning, objective sensing, and move evaluation.
  - `maze.sense_objective` is described as guidance, not a path oracle.
  - `maze.evaluate_moves` is described as local legality/risk/frontier/blocker analysis before `maze.move`.

- Context overload or underload:
  - MazeQuest currently uses bounded planner context: eight recent observations and eight recent receipts.
  - Each state snapshot repeats active objective, visible map, legal actions, move evaluations, and current inventory so the planner can recover from bounded history.

- Local optimum behavior:
  - Move evaluations expose risk, frontier gain, cost, and objective delta. The prompt does not require always chasing the warmest signal.
  - Bad tradeoff choices remain useful LLM-quality signal rather than runtime failure.

- Refinement churn:
  - Stepwise mode remains the default for watchability.
  - `query-blocker` and `blocker` planning modes remain available for less noisy smoke runs.

- Tool refusal recovery:
  - Refused move receipts include the blocker reason and legal move alternatives.
  - Refused take/use receipts include guidance to copy current-cell ids from `legalActions`.

Pass condition remains intentionally conservative:

```text
No false success.
No mutation without validation.
Every tool call has a receipt.
Final status is either evidence-backed success or truthful blockage.
```

## Verification Commands

Run before calling the host harness complete:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
dotnet run --project Agentica.CLI -- mazequest list
dotnet run --project Agentica.CLI -- mazequest preview sun_gate_maze --type unlock --seed 173 --reveal
dotnet run --project Agentica.CLI -- mazequest run sun_gate_maze --planner deterministic --type unlock --seed 173 --planning-mode stepwise --timeout-seconds 120
dotnet run --project Agentica.CLI -- mazequest run sun_gate_maze --planner deterministic --type collect --seed 321 --planning-mode stepwise --timeout-seconds 120
dotnet run --project Agentica.CLI -- mazequest run --watch --narrator deterministic --turn-json --type unlock --seed 173 --width 7 --height 7 --planning-mode stepwise --timeout-seconds 60
dotnet run --project Agentica.CLI -- mazequest run --watch --type unlock --seed 173
```

If Gemini credentials are present:

```powershell
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts" --planner gemini --thinking-budget off
dotnet run --project Agentica.CLI -- mazequest run sun_gate_maze --planner gemini --type unlock --seed 173 --width 7 --height 7 --planning-mode plan-only --thinking-budget off --timeout-seconds 60
```

If Gemini credentials are missing, Gemini paths should fail clearly or fall back only when explicitly configured to do so. If the MazeQuest Gemini call is too slow, the bounded timeout smoke should produce an honest blocked envelope instead of hanging the console. Secret values must never be printed.

## Completion Discipline

Do not judge success by whether the console story sounds good.

Judge success by:

- Receipts.
- Observations.
- State transitions.
- Objective artifacts.
- Honest terminal outcome.
- Bounded execution.

Narration helps the user watch the run. It does not prove the run.

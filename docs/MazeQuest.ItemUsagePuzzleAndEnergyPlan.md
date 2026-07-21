# MazeQuest Item Usage, Puzzle Bindings, And Energy Plan

> Lifecycle: **Incubating** · Completion: **50%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)

## Purpose

This document pins the next MazeQuest host-harness enhancements without turning the test game into a full game engine.

The goal is to improve the test surface for Agentica planning, execution, and adaptation:

- Use structured affordances instead of narrative hints.
- Keep every valid action host-generated and auditable.
- Make puzzle/object state changes receipt-backed.
- Make resource pressure real but not accidentally impossible.
- Preserve truthful blocked outcomes when the host intentionally starves resources.

MazeQuest remains a Lab host scenario. These concepts must not move into the Agentica runtime vocabulary.

## Item And Puzzle Scope

Build dynamic control bindings, not a puzzle engine.

The host should expose generated object controls through existing Agentica tool descriptor language and dynamic `legalActions`.
The agent may only execute exact bindings that are currently legal.

Do not add:

- Hints tool.
- Arbitrary JSON patch.
- Generated code or scripts.
- Cell-trigger engine.
- Combat.
- Actor classes.
- Cooldowns.
- Dynamic tool catalog per object.
- Procedural riddle text.
- Multi-puzzle chains.

The slice is one static control tool plus generated legal bindings.

## Descriptor And Binding Model

Static tool descriptor:

```text
maze.set_control
  Action, WritesLocalState
  controlId: required string copied exactly from legalActions
  state: required string copied exactly from legalActions
```

Generated host data:

```text
ControlDefinition
  controlId
  objectId
  allowedStates
  currentCellOnly
  requiredInventoryItem?
  agentMutable
```

Session state:

```text
ObjectStates
  Dictionary<string, string>
  objectId -> state
```

Dynamic legal action example:

```json
{
  "toolId": "maze.set_control",
  "input": {
    "controlId": "amber_rune",
    "state": "lit"
  },
  "reason": "Set Amber Rune from dark to lit.",
  "effectPreview": {
    "controlId": "amber_rune",
    "previousState": "dark",
    "nextState": "lit"
  }
}
```

The LLM does not invent mutation syntax. It copies the binding that the generated host state exposes.

## Valid Gameplay Rules

Expose a `maze.set_control` legal action only when all are true:

- The control object exists.
- The object is discovered or visible according to the current public state rule.
- The agent is on the same cell when `currentCellOnly` is true.
- The control is `agentMutable`.
- The requested state is in `allowedStates`.
- The requested state differs from current state.
- The required inventory item is present, if one is configured.

Execution must recompute current `legalActions` and require an exact `toolId + input` match before mutation.
If no exact match exists, refuse with `control_action_not_legal`.

Receipt data should include:

```json
{
  "controlId": "amber_rune",
  "previousState": "dark",
  "newState": "lit",
  "puzzleState": {}
}
```

## Minimal Puzzle Shape

Add one explicit logic gate scenario under the existing `PuzzleSequence` archetype.

Example:

```text
amber_rune starts dark
blue_rune starts lit
sun_gate opens when amber_rune is lit and blue_rune is dark
```

The rule is not hidden. This is not a riddle test. It is a structured reasoning test:

```text
condition mismatch -> legal mutation -> receipt -> updated condition -> next action
```

Snapshot surface:

```json
{
  "puzzleState": {
    "puzzleId": "sun_gate_seals",
    "rule": "The sun gate opens when amber_rune is lit and blue_rune is dark.",
    "conditions": [
      {
        "controlId": "amber_rune",
        "requiredState": "lit",
        "actualState": "dark",
        "satisfied": false
      },
      {
        "controlId": "blue_rune",
        "requiredState": "dark",
        "actualState": "lit",
        "satisfied": false
      }
    ],
    "satisfied": false
  }
}
```

Gate behavior:

- `maze.use sun_gate` succeeds only when `puzzleState.satisfied` is true.
- Otherwise it refuses with `puzzle_not_satisfied` and includes the condition table.

## Energy Findings From Latest Gemini Run

Log inspected:

```text
C:\Users\Zythis\source\repos\Agentica\.agentica\runs\20260506T011056220Z_mazequest_d94b8eb5
```

Command metadata:

```text
mazequest run --planner gemini --planning-mode stepwise --watch --narrator deterministic --turn-json --type unlock --seed 173 --width 7 --height 7 --timeout-seconds 180 --thinking-budget off --max-blocked-retries 2 --log-run
```

Important observations:

- The run directory has `events.jsonl`, `turns.jsonl`, stage JSON, and initial public snapshot.
- The copied local directory does not contain `outcome.json`; terminal judgment must be reconstructed from turn and event logs.
- The agent made meaningful progress:
  - reached the key,
  - made one invalid `maze.take` attempt,
  - recovered by moving onto the key,
  - took `sun_key`,
  - reached and used `sun_gate`,
  - continued toward the exit.
- The agent used `maze.rest` twice.
- The last copied turns show the agent still moving with `energy = 0`.

Turn summary:

```text
1  move east   -> (2,1), energy 7
2  move east   -> (3,1), energy 6
3  move south  -> (3,2), energy 5
4  move south  -> (3,3), energy 4
5  take key    -> refused, wrong cell
6  move east   -> (4,3), energy 3
7  take key    -> success, objective unlock_sun_gate
8  move east   -> (5,3), energy 2
9  move south  -> (5,4), energy 1, spike damage
10 move south  -> (5,5), energy 0
11 rest        -> energy 2
12 move west   -> (4,5), energy 0
13 use gate    -> success, objective reach_exit
14 move west   -> (3,5), energy 0
15 rest        -> energy 2
16 move west   -> (2,5), energy 1
17 move west   -> (1,5), energy 0
18 move north  -> (1,4), energy 0, trap damage
```

The current host implementation does not enforce sufficient energy before movement.
`maze.move` subtracts energy after movement and clamps at zero. That means energy is displayed as pressure, but it is not a legality constraint yet.

## Perfect Route Energy Budget

For seed `173`, `unlock`, `7x7`, placements are:

```text
start = (1,1)
key   = (4,3)
gate  = (4,5)
exit  = (1,3)
```

Perfect route shape:

```text
start -> key:
  east, east, south, south, east

key -> gate:
  east, south, south, west

gate -> exit:
  west, west, west, north, north
```

Observed terrain costs imply:

```text
start -> key: 5 energy
key -> gate: 5 energy
gate -> exit: 6 energy
darkness extra drain on route: +1 energy
total expected perfect-route pressure: about 17 energy
```

Initial energy is currently `8`. Two rests restore `+4`, for `12` total available recovery budget.
That is too tight if energy becomes a real legality constraint.

## Energy Design Decision

Energy should be real, but the default completion smoke should not be accidentally impossible.

Backtracking must remain possible. A dead end is a planning problem, not a silent terminal state.
The host must expose reverse movement through the same `maze.evaluate_moves` and `legalActions` surface whenever the adjacent cell is physically enterable and the resource policy allows the move.
If resource policy blocks backtracking, the refusal must be explicit and must expose the recovery surface, such as available rest charges or insufficient energy details.

Default solvable mode:

```text
initialEnergy = perfectRouteCost + pad
pad = 4 to 6
rest remains available but limited
```

Pressure/failure mode:

```text
initialEnergy = perfectRouteCost - starvationAmount
or restCharges = 0
expected outcome = honest blocked/partial
```

The harness should support both:

- normal mode tests planning and completion,
- starvation mode tests valid failure and recovery reasoning.

## Proposed Energy Mechanics

Add generated/session resource policy:

```text
EnergyPolicy
  initialEnergy
  maxEnergy
  restEnergyGain
  restHealthGain
  restCharges
  enforceMoveEnergy
```

Default for normal MazeQuest:

```text
enforceMoveEnergy = true
initialEnergy = perfectRouteCost + 5
maxEnergy = initialEnergy
restEnergyGain = 2
restHealthGain = 1
restCharges = 2
```

Move legality:

```text
move is legal only when energy >= terrainCost + visible required hazard energy drain
```

If not legal:

```text
receiptStatus = Refused
reason = insufficient_energy
data includes:
  currentEnergy
  requiredEnergy
  restAvailable
  legalAlternatives
```

Rest legality:

```text
rest is legal only when restCharges > 0 and energy < maxEnergy or health < maxHealth
```

Rest receipt:

```json
{
  "previousEnergy": 0,
  "newEnergy": 2,
  "restChargesBefore": 2,
  "restChargesAfter": 1
}
```

Snapshots should expose:

```json
{
  "resources": {
    "health": 8,
    "maxHealth": 8,
    "energy": 3,
    "maxEnergy": 22,
    "restCharges": 2,
    "restEnergyGain": 2,
    "enforceMoveEnergy": true
  }
}
```

## Acceptance Criteria

Item/puzzle binding slice is complete when:

- Exact legal `maze.set_control` bindings appear only when valid.
- Invented control mutations are refused.
- Valid control mutations update in-memory state.
- Receipts include before/after state.
- Puzzle condition table updates after each mutation.
- Gate refuses until puzzle conditions are satisfied.
- Gate opens once puzzle conditions are satisfied.

Energy slice is complete when:

- Default generated runs have enough energy for a perfect route plus padding.
- Legal backtracking moves are exposed from dead ends and non-terminal branches.
- Resource enforcement does not strand the agent without an explicit refused receipt and recovery facts.
- `maze.move` refuses insufficient-energy moves when enforcement is on.
- `maze.rest` has limited charges and receipt-backed before/after resource state.
- `maze.get_state`, `maze.evaluate_moves`, and refused receipts expose enough resource facts for recovery planning.
- A starvation configuration can intentionally produce a truthful blocked outcome.

## Scope Stop

Do not implement puzzles and energy starvation at the same time.

Recommended order:

1. Make energy accounting real and fair.
2. Add starvation mode only after default completion is stable.
3. Add dynamic control bindings for one explicit puzzle.
4. Only then consider additional adversity.

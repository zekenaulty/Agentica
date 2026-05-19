# Codex Goal: Agentica.CLI ChessQuest Harness

## Goal

Build ChessQuest as a chess-domain pressure test for Agentica planning, execution, public intent traces, and higher-level orchestration.

ChessQuest is not a chess tutor. It is a host-owned referee and opponent loop that asks an agent to act as a player under public game rules.

Core rule:

```text
The model acts.
The harness verifies.
Receipts prove mutation.
The chess engine owns legal truth.
Narration and strategy claims are public intent, not proof.
```

## Current Position

Implemented slices:

- `StrictRefereeProjected` chess surface.
- Gera.Chess behind `IChessRulesEngine`.
- `chess.project_line` as read-only self-authored line projection.
- turn cockpit summaries with public intent and receipts.
- heuristic and agent-backed opponents.
- replay/resume game records.
- bounded phase strategy mode.
- board parsing probe with persisted prompt/response logs.
- LLM strategy projection mode with a visible orchestration tier and active run tier.

The next ChessQuest work should keep the model in the role of an actor, not merely a classifier.

## Probe Doctrine

Probe design must match the harness role.

Bad default shape:

```text
Host proposes a move.
Model labels whether the move is legal/good/bad.
Host checks the label.
```

That treats the model as a linter. It measures classification more than agency.

Better default shape:

```text
Host provides public board state and an objective.
Model proposes an action.
Host verifies the action.
```

That treats the model as a solver/actor, which is the actual ChessQuest contract.

Classifier probes are still allowed when they answer a narrow diagnostic question, but they should not be mistaken for gameplay capability.

## Existing Board Probe

`chessquest board-probe` measures whether the model can read board coordinates and piece identity from a supplied representation.

It currently supports:

- `--presentation ascii|fen|both`
- `--target occupied|empty|mixed`
- persisted logs with prompt, raw response, parsed answer, expected answer, finish reason, usage, and response metadata

This proves board literacy only. It does not prove move selection, tactical reasoning, strategy, or state transition tracking.

## Actor Probes To Add

### Action Probe

Command direction:

```powershell
dotnet run --project .\Agentica.CLI -- chessquest action-probe --trials 25 --scramble-plies 24 --objective any-legal --presentation ascii --log-run
```

Input:

```text
Here is the public board.
You are the side to move.
Return one legal UCI move and a short public reason.
```

Response shape:

```json
{
  "agentColor": "white",
  "move": "g1f3",
  "publicReason": "Develop a knight toward the center."
}
```

Host validation:

- UCI syntax is valid.
- The requested side/color matches the board.
- The move is legal from the current FEN.
- The move is accepted by the rules engine.
- The move satisfies the selected objective where applicable.

The model must not receive the legal move list in this probe. It must act from board state and rules knowledge.

### Constrained Action Probe

Same action shape, but the host asks for one action satisfying a public constraint:

- `any-legal`
- `make-capture`
- `escape-check`
- `give-check`
- `castle`
- `promote`
- `mate-in-one`
- `develop-opening`

The host should generate or curate positions where the constraint is satisfiable, then verify the submitted action.

### Claim Action Probe

Input:

```text
Choose one legal move and declare whether it gives check and/or checkmate.
```

Response shape:

```json
{
  "agentColor": "white",
  "move": "e1e4",
  "claims": {
    "check": true,
    "checkmate": false
  },
  "publicReason": "Move the rook onto the king's file."
}
```

Host validation:

- The action is legal.
- The engine verifies `check`.
- The engine verifies `checkmate`.
- False checkmate claims are recorded distinctly from illegal moves.

This targets the observed failure mode where the agent claimed mate when the position was not mate.

### Tactical Action Probe

Input:

```text
Here is a tactical position.
Find the move that satisfies the objective.
```

Objectives:

- mate in one
- only legal move
- win an exposed queen
- escape check
- promote a pawn
- stop immediate mate

Validation should be engine-backed and solution-set based. Free-text rationale is trace only.

## Orchestration Doctrine

ChessQuest orchestration should use the existing Agentica layering:

```text
Game = campaign boundary
Phase / strategy = orchestration task boundary
Move = receipt-bearing transaction
Tool call = implementation detail
```

The mistake to avoid:

```text
one orchestration task = one move
```

That makes orchestration a turn scheduler. It is useful for wiring tests, but it is too narrow as the target architecture.

The intended shape:

```text
Orchestrator assigns a bounded phase objective.
Agentica runner may play multiple agent turns inside that phase.
The phase ends when satisfied, invalidated, terminal, tactically unsafe, or budget exhausted.
The orchestrator evaluates the phase report and chooses continue/replan/new phase.
```

## Current Phase Slice

Current `--strategy-mode phase` is host-authored. It injects:

- `ChessQuestStrategyFrame`
- `ChessQuestPhaseObjective`
- `ChessQuestPhaseProgress`

The phase report is deterministic and receipt-backed. It is deliberately not full adaptive orchestration yet.

## Implemented Slice: LLM Strategy Projection

`--strategy-mode projected` adds an LLM-authored strategy projection before the active phase run. This is not full task-graph orchestration.

Purpose:

```text
Let the model propose a durable public strategy frame from current board truth.
Keep chess truth with the engine.
Keep completion proof with receipts/verifier.
```

Projection contract:

```csharp
public sealed record ChessQuestStrategyProjection(
    string ProjectionId,
    ChessQuestColor AgentColor,
    string Phase,
    string StrategyName,
    string StrategyIntent,
    IReadOnlyList<string> ActiveObjectives,
    IReadOnlyList<string> StopTriggers,
    IReadOnlyList<string> ProgressSignals,
    IReadOnlyList<string> VerificationRules);
```

Example projection for a new standard game:

```json
{
  "projectionId": "strategy_001",
  "agentColor": "White",
  "phase": "opening",
  "strategyName": "center-control development",
  "strategyIntent": "Develop minor pieces, contest the center, improve king safety, and avoid early material loss.",
  "activeObjectives": [
    "occupy or pressure central squares",
    "develop knights and bishops before repeated pawn moves",
    "prepare king safety",
    "verify any checkmate or check claim with chess.project_line"
  ],
  "stopTriggers": [
    "terminal game state",
    "agent king is in check",
    "major material swing",
    "phase budget exhausted",
    "strategy no longer fits legal board state"
  ],
  "progressSignals": [
    "minor piece developed",
    "king safety improved",
    "center contested",
    "no illegal move retries"
  ],
  "verificationRules": [
    "chessFrame is authoritative board truth",
    "legal move receipts override strategy claims",
    "project_line must verify check and checkmate claims",
    "strategy does not prove completion"
  ]
}
```

This projection enters the planner context as public strategic guidance. It must not contain engine scores, best-move rankings, principal variations, hidden objective data, or opponent policy.

## Slice After That: ChessQuest LLM Task Planning

After strategy projection is stable, add a ChessQuest-specific orchestration command that uses the existing `ITaskPlanner`/`TaskOrchestrator` shape.

This is the first slice that should use LLM task planning.

Initial task graph should be small:

```text
create_or_revise_strategy_projection
execute_bounded_phase
evaluate_phase_report
```

The phase task may play multiple moves. Acceptance is not "won the game"; acceptance is that the phase envelope ended with a valid `ChessQuestPhaseReport`.

The orchestrator then decides:

- continue the same strategy
- revise strategy
- switch phase
- stop because terminal
- stop because blocked/invalid

LLM task planning should supervise the phase strategy, not replace the move loop and not call chess tools directly.

## Guardrails

- Strategy projections are hypotheses/guidance, not proof.
- Phase reports are host-compiled from receipts and session state.
- The orchestrator never sees hidden engine opponent internals as planner context.
- The model never gets a best-move tool in strict benchmark mode.
- The model may use `project_line` only for self-authored lines.
- Probe success does not imply game skill; probes identify capability gaps.

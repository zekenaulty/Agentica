using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class ChessQuestSession
{
    private readonly IChessRulesEngine _rules;
    private readonly IChessOpponent _opponent;
    private readonly List<ChessQuestToolTurn> _turns = [];
    private int _projectedLinesThisTurn;

    public ChessQuestSession(
        ChessQuestScenario scenario,
        IChessRulesEngine? rules = null,
        IChessOpponent? opponent = null)
    {
        Scenario = scenario;
        _rules = rules ?? new GeraChessRulesEngine(scenario.InitialFen);
        _opponent = opponent ?? new RandomLegalMoveOpponent(seed: 1);
    }

    public ChessQuestScenario Scenario { get; }

    public IReadOnlyList<ChessQuestToolTurn> Turns => _turns;

    public ChessPublicState CurrentState => _rules.GetState();

    public ChessQuestSessionContext SessionContext => BuildSessionContext();

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = invocation.ToolId switch
        {
            ChessQuestToolIds.GetState => GetState(invocation),
            ChessQuestToolIds.RenderBoard => RenderBoard(invocation),
            ChessQuestToolIds.ListLegalMoves => ListLegalMoves(invocation),
            ChessQuestToolIds.ProjectLine => ProjectLine(invocation),
            ChessQuestToolIds.PlayMove => await PlayMoveAsync(invocation, cancellationToken).ConfigureAwait(false),
            ChessQuestToolIds.CompleteObjective => CompleteObjective(invocation),
            _ => Refused(invocation, "unknown_chess_tool", $"Unknown ChessQuest tool '{invocation.ToolId}'.")
        };

        _turns.Add(new ChessQuestToolTurn(invocation, result, _rules.GetState()));
        return result;
    }

    private ToolResult GetState(ToolInvocation invocation)
    {
        var data = Snapshot("get_state");
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "ChessQuest state returned.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Current ChessQuest state observed.", data));
    }

    private ToolResult RenderBoard(ToolInvocation invocation)
    {
        var data = Snapshot("render_board");
        data["boardAscii"] = _rules.RenderAscii();
        data["boardLines"] = _rules.RenderAsciiLines();

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "ChessQuest board rendered.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Current ChessQuest board observed.", data));
    }

    private ToolResult ListLegalMoves(ToolInvocation invocation)
    {
        var data = Snapshot("list_legal_moves");
        data["notation"] = "uci";
        data["legalMoves"] = _rules.ListLegalMoves().Select(move => move.Uci).ToArray();

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Legal UCI moves returned.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Legal UCI moves observed.", data));
    }

    private ToolResult ProjectLine(ToolInvocation invocation)
    {
        if (!Scenario.DisclosurePolicy.AllowLineProjection)
        {
            return Refused(invocation, "line_projection_unavailable", "Line projection is not available for this ChessQuest surface.");
        }

        if (_projectedLinesThisTurn >= Scenario.DisclosurePolicy.MaxProjectedLinesPerTurn)
        {
            return Refused(
                invocation,
                "line_projection_budget_exhausted",
                "Line projection budget is exhausted for the current agent turn.",
                new Dictionary<string, object?>
                {
                    ["maxProjectedLinesPerTurn"] = Scenario.DisclosurePolicy.MaxProjectedLinesPerTurn,
                    ["projectedLinesThisTurn"] = _projectedLinesThisTurn
                });
        }

        var line = ReadStringArray(invocation, "line");
        if (line.Count == 0)
        {
            return Refused(invocation, "missing_line", "Project line requires at least one UCI move.");
        }

        _projectedLinesThisTurn++;
        var requestedMaxPlies = ReadInt(invocation, "maxPlies");
        var maxPlies = Math.Min(
            requestedMaxPlies ?? Scenario.DisclosurePolicy.MaxProjectedPliesPerLine,
            Scenario.DisclosurePolicy.MaxProjectedPliesPerLine);

        var claims = ReadStringArray(invocation, "claims");
        var projection = _rules.ProjectLine(new ChessLineProjectionRequest(line, maxPlies, claims));
        var data = Snapshot("project_line");
        data["projection"] = ProjectionPayload(projection);
        data["claimVerification"] = projection.ClaimVerification;
        data["projectedLinesThisTurn"] = _projectedLinesThisTurn;
        data["maxProjectedLinesPerTurn"] = Scenario.DisclosurePolicy.MaxProjectedLinesPerTurn;
        data["maxProjectedPliesPerLine"] = Scenario.DisclosurePolicy.MaxProjectedPliesPerLine;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Agent-authored chess line projected.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Agent-authored chess line projection observed.", data));
    }

    private async Task<ToolResult> PlayMoveAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!SessionContext.AgentToMove)
        {
            return Refused(invocation, "agent_not_to_move", "The agent cannot move because it is not the agent side's turn.");
        }

        if (CurrentState.IsTerminal)
        {
            return Refused(invocation, "game_is_terminal", "The game is terminal; no move can be played.");
        }

        var move = ReadString(invocation, "move");
        if (string.IsNullOrWhiteSpace(move))
        {
            return Refused(invocation, "missing_move", "Play move requires a UCI move.");
        }

        var turnIntent = ReadTurnIntent(invocation, move);
        if (turnIntent is null)
        {
            return Refused(invocation, "missing_turn_intent", "Play move requires a public turnIntent matching the selected UCI move.");
        }

        var before = CurrentState;
        var agentResult = _rules.TryPlayMove(move);
        if (!agentResult.Accepted || agentResult.Move is null)
        {
            return Refused(
                invocation,
                agentResult.RefusalReason ?? "illegal_move",
                "The submitted move is not legal in the current position.",
                new Dictionary<string, object?>
                {
                    ["agentMoveAccepted"] = false,
                    ["requestedMove"] = move.Trim().ToLowerInvariant(),
                    ["fenUnchanged"] = string.Equals(before.Fen, _rules.GetFen(), StringComparison.Ordinal),
                    ["agentToMove"] = SessionContext.AgentToMove
                });
        }

        var stateAfterAgentMove = CurrentState;
        var sideToMoveInCheckAfterAgentMove = !stateAfterAgentMove.IsTerminal &&
            _rules.IsKingInCheck(stateAfterAgentMove.SideToMove);
        var agentMoveGivesCheckmate = MoveGivesCheckmate(stateAfterAgentMove, Scenario.AgentColor);
        var agentMoveGivesCheck = sideToMoveInCheckAfterAgentMove || agentMoveGivesCheckmate;

        var opponentMoveApplied = false;
        string? opponentMove = null;
        string? opponentPolicy = null;
        ChessMoveResult? opponentResult = null;

        if (!CurrentState.IsTerminal && CurrentState.SideToMove == OpponentColor())
        {
            var legalMoves = _rules.ListLegalMoves().Select(item => item.Uci).ToArray();
            var chosen = await _opponent.ChooseMoveAsync(
                    new ChessOpponentRequest(
                        _rules.GetFen(),
                        OpponentColor(),
                        Scenario.Difficulty.Opponent,
                        CurrentState.Ply,
                        Seed: 1,
                        LegalMoves: legalMoves),
                    cancellationToken)
                .ConfigureAwait(false);

            if (chosen is not null)
            {
                opponentResult = _rules.TryPlayMove(chosen.Move);
                if (opponentResult.Accepted && opponentResult.Move is not null)
                {
                    opponentMoveApplied = true;
                    opponentMove = opponentResult.Move;
                    opponentPolicy = chosen.Policy;
                }
            }
        }

        _projectedLinesThisTurn = 0;
        var state = CurrentState;
        var data = Snapshot("play_move");
        data["turnIntent"] = turnIntent;
        data["agentMoveAccepted"] = true;
        data["agentMove"] = agentResult.Move;
        data["agentMoveProjection"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["fenAfterAgentMove"] = agentResult.FenAfter,
            ["sideToMoveAfterAgentMove"] = stateAfterAgentMove.SideToMove.ToString().ToLowerInvariant(),
            ["sideToMoveInCheckAfterAgentMove"] = sideToMoveInCheckAfterAgentMove,
            ["agentMoveGivesCheck"] = agentMoveGivesCheck,
            ["agentMoveGivesCheckmate"] = agentMoveGivesCheckmate,
            ["terminalAfterAgentMove"] = stateAfterAgentMove.IsTerminal,
            ["terminalStateAfterAgentMove"] = stateAfterAgentMove.TerminalState
        };
        data["opponentMoveApplied"] = opponentMoveApplied;
        data["opponentMove"] = opponentMove;
        data["opponentPolicyPublic"] = opponentPolicy is null ? null : "host_controlled";
        data["sideToMove"] = state.SideToMove.ToString().ToLowerInvariant();
        data["agentToMove"] = SessionContext.AgentToMove;
        data["terminal"] = state.IsTerminal;
        data["terminalState"] = state.TerminalState;
        data["fenAfter"] = state.Fen;
        data["captures"] = agentResult.Captures
            .Concat(opponentResult?.Captures ?? [])
            .ToArray();

        var message = opponentMoveApplied
            ? $"Agent move {agentResult.Move} accepted; opponent move applied."
            : $"Agent move {agentResult.Move} accepted.";
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, message, data);
        return new ToolResult(receipt, Observation(invocation, receipt, "ChessQuest move turn executed.", data));
    }

    private ToolResult CompleteObjective(ToolInvocation invocation)
    {
        var state = CurrentState;
        var agentWon = state.TerminalState?.Winner == Scenario.AgentColor;
        if (!state.IsTerminal || !agentWon)
        {
            return Refused(
                invocation,
                "objective_not_satisfied",
                "ChessQuest objective is not satisfied.",
                new Dictionary<string, object?>
                {
                    ["terminal"] = state.IsTerminal,
                    ["terminalState"] = state.TerminalState,
                    ["winRequired"] = true
                });
        }

        var data = Snapshot("complete_objective");
        data["objectiveCompleted"] = true;
        data["terminalState"] = state.TerminalState;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "ChessQuest objective completed.", data);
        var artifact = new Artifact(
            ArtifactId: AgenticaIds.New("artifact"),
            Kind: "chessquest.objective_completed",
            Payload: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

        return new ToolResult(receipt, Artifact: artifact);
    }

    private ToolResult Refused(
        ToolInvocation invocation,
        string reason,
        string message,
        IReadOnlyDictionary<string, object?>? extraData = null)
    {
        var data = Snapshot("refused");
        data["reason"] = reason;
        data["blocker"] = reason;
        if (extraData is not null)
        {
            foreach (var pair in extraData)
            {
                data[pair.Key] = pair.Value;
            }
        }

        var receipt = Receipt(invocation, ReceiptStatus.Refused, message, data);
        return new ToolResult(receipt, Observation(invocation, receipt, message, data));
    }

    private Dictionary<string, object?> Snapshot(string operation)
    {
        var state = CurrentState;
        var context = BuildSessionContext();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = operation,
            ["sessionContext"] = context,
            ["agenticHarness"] = ChessQuestCapabilitySurfaceCompiler.BuildHarnessContext(this),
            ["fen"] = state.Fen,
            ["ply"] = state.Ply,
            ["recentMovesUci"] = state.RecentMovesUci,
            ["sideToMove"] = state.SideToMove.ToString().ToLowerInvariant(),
            ["agentToMove"] = context.AgentToMove,
            ["terminal"] = state.IsTerminal,
            ["terminalState"] = state.TerminalState
        };
    }

    private ChessQuestSessionContext BuildSessionContext()
    {
        var state = CurrentState;
        return new ChessQuestSessionContext(
            Kind: "ChessSessionContext",
            SessionId: Scenario.ScenarioId,
            AgentColor: Scenario.AgentColor,
            OpponentColor: OpponentColor(),
            SideToMove: state.SideToMove,
            AgentToMove: state.SideToMove == Scenario.AgentColor && !state.IsTerminal,
            SurfaceMode: Scenario.DisclosurePolicy.Mode.ToString(),
            Objective: new ChessQuestObjective(
                Scenario.ObjectiveKind.ToString(),
                Scenario.PublicObjective),
            ResultPolicy: new ChessQuestResultPolicy(
                WinRequired: true,
                DrawCountsAs: "not_complete",
                LossCountsAs: "failed",
                ResignationAllowed: false),
            Difficulty: Scenario.Difficulty,
            Ply: state.Ply,
            Terminal: state.IsTerminal);
    }

    private ChessQuestColor OpponentColor() =>
        Scenario.AgentColor == ChessQuestColor.White ? ChessQuestColor.Black : ChessQuestColor.White;

    private static bool MoveGivesCheckmate(
        ChessPublicState stateAfterMove,
        ChessQuestColor moverColor) =>
        stateAfterMove.TerminalState is { Winner: not null } terminal &&
        terminal.Winner == moverColor &&
        terminal.Reason.Contains("Checkmate", StringComparison.OrdinalIgnoreCase);

    private object ProjectionPayload(ChessLineProjection projection)
    {
        if (Scenario.DisclosurePolicy.IncludeProjectionCaptures)
        {
            return projection;
        }

        return projection with
        {
            Captures = []
        };
    }

    private static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        string message,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            ReceiptId: AgenticaIds.New("receipt"),
            StepId: invocation.StepId,
            ToolId: invocation.ToolId,
            Status: status,
            Message: message,
            At: DateTimeOffset.UtcNow,
            Data: data);

    private static Observation Observation(
        ToolInvocation invocation,
        Receipt receipt,
        string summary,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            ObservationId: AgenticaIds.New("observation"),
            StepId: invocation.StepId,
            Kind: ObservationKind.StateQuery,
            Summary: summary,
            Data: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

    private static string? ReadString(ToolInvocation invocation, string key) =>
        invocation.Input.TryGetValue(key, out var value)
            ? Convert.ToString(value)
            : null;

    private static int? ReadInt(ToolInvocation invocation, string key)
    {
        if (!invocation.Input.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is int i)
        {
            return i;
        }

        return int.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(ToolInvocation invocation, string key)
    {
        if (!invocation.Input.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is IEnumerable<string> strings)
        {
            return strings
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToLowerInvariant())
                .ToArray();
        }

        if (value is IEnumerable<object> objects)
        {
            return objects
                .Select(Convert.ToString)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim().ToLowerInvariant())
                .ToArray();
        }

        return [];
    }

    private static IReadOnlyDictionary<string, object?>? ReadTurnIntent(
        ToolInvocation invocation,
        string move)
    {
        if (!invocation.Input.TryGetValue("turnIntent", out var value) ||
            value is not IReadOnlyDictionary<string, object?> intent)
        {
            return null;
        }

        var selected = intent.TryGetValue("selectedMove", out var selectedValue)
            ? Convert.ToString(selectedValue)
            : null;
        if (!string.Equals(selected?.Trim(), move.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in intent)
        {
            sanitized[pair.Key] = pair.Value is string text && text.Length > 320
                ? text[..320]
                : pair.Value;
        }

        return sanitized;
    }
}

using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class ChessQuestSession
{
    private readonly IChessRulesEngine _rules;
    private readonly IChessOpponent _opponent;
    private readonly List<ChessQuestToolTurn> _turns = [];
    private readonly List<ChessQuestRecordedPly> _committedPlies = [];
    private readonly Dictionary<string, ChessQuestLegalMoveObservation> _legalMoveObservations = new(StringComparer.Ordinal);
    private ChessQuestLegalMoveObservation? _latestLegalMoveObservation;
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

    public IReadOnlyList<ChessQuestRecordedPly> CommittedPlies => _committedPlies;

    public int ProjectedLinesThisTurn => _projectedLinesThisTurn;

    public ChessPublicState CurrentState => _rules.GetState();

    public ChessQuestSessionContext SessionContext => BuildSessionContext();

    public bool IsSideToMoveInCheck()
    {
        var state = CurrentState;
        return !state.IsTerminal && _rules.IsKingInCheck(state.SideToMove);
    }

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
            ChessQuestToolIds.InspectAttacks => InspectAttacks(invocation),
            ChessQuestToolIds.PlayMove => await PlayMoveAsync(invocation, cancellationToken).ConfigureAwait(false),
            ChessQuestToolIds.CompleteObjective => CompleteObjective(invocation),
            _ => Refused(invocation, "unknown_chess_tool", $"Unknown ChessQuest tool '{invocation.ToolId}'.")
        };

        _turns.Add(new ChessQuestToolTurn(invocation, result, _rules.GetState()));
        return result;
    }

    public void ReplayCommittedPlies(IEnumerable<ChessQuestRecordedPly> plies)
    {
        foreach (var ply in plies.OrderBy(item => item.Ply))
        {
            ReplayCommittedPly(ply);
        }
    }

    private void ReplayCommittedPly(ChessQuestRecordedPly ply)
    {
        var before = CurrentState;
        if (!string.IsNullOrWhiteSpace(ply.FenBefore) &&
            !string.Equals(before.Fen, ply.FenBefore, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot replay ChessQuest ply {ply.Ply}: expected FEN '{ply.FenBefore}', but current FEN is '{before.Fen}'.");
        }

        var result = _rules.TryPlayMove(ply.Move);
        if (!result.Accepted || result.Move is null)
        {
            throw new InvalidOperationException(
                $"Cannot replay ChessQuest ply {ply.Ply}: move '{ply.Move}' was refused as {result.RefusalReason ?? "illegal_move"}.");
        }

        if (!string.IsNullOrWhiteSpace(ply.FenAfter) &&
            !string.Equals(result.FenAfter, ply.FenAfter, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot replay ChessQuest ply {ply.Ply}: expected resulting FEN '{ply.FenAfter}', but got '{result.FenAfter}'.");
        }

        _committedPlies.Add(ply with
        {
            Ply = _committedPlies.Count + 1,
            Move = result.Move,
            Color = before.SideToMove,
            FenBefore = result.FenBefore,
            FenAfter = result.FenAfter
        });
        _projectedLinesThisTurn = 0;
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
        var legalMoves = _rules.ListLegalMoves().Select(move => move.Uci).ToArray();
        data["legalMoves"] = legalMoves;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Legal UCI moves returned.", data);
        var observation = Observation(invocation, receipt, "Legal UCI moves observed.", data);
        data["legalMoveObservationId"] = observation.ObservationId;
        data["legalMoveReceiptId"] = receipt.ReceiptId;
        StoreLegalMoveObservation(observation, receipt, legalMoves);
        return new ToolResult(receipt, observation);
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
        data["legalProjectionOnly"] = projection.LegalProjectionOnly;
        data["moveQualityKnown"] = projection.MoveQualityKnown;
        data["safetyKnown"] = projection.SafetyKnown;
        data["opponentReplyModeled"] = projection.OpponentReplyModeled;
        data["projectedLinesThisTurn"] = _projectedLinesThisTurn;
        data["maxProjectedLinesPerTurn"] = Scenario.DisclosurePolicy.MaxProjectedLinesPerTurn;
        data["maxProjectedPliesPerLine"] = Scenario.DisclosurePolicy.MaxProjectedPliesPerLine;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Agent-authored chess line projected.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Agent-authored chess line projection observed.", data));
    }

    private ToolResult InspectAttacks(ToolInvocation invocation)
    {
        if (!Scenario.DisclosurePolicy.AllowAttackInspection)
        {
            return Refused(invocation, "attack_inspection_unavailable", "Attack inspection is not available for this ChessQuest surface.");
        }

        var inspection = _rules.InspectAttacks(Scenario.AgentColor);
        var data = Snapshot("inspect_attacks");
        data["inspection"] = inspection;
        data["opponentLegalCaptures"] = inspection.OpponentLegalCaptures;
        data["attackedAgentPieces"] = inspection.AttackedAgentPieces;
        data["agentKingInCheck"] = inspection.AgentKingInCheck;
        data["evaluationIncluded"] = inspection.EvaluationIncluded;
        data["guidanceIncluded"] = inspection.GuidanceIncluded;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Public attack inspection returned.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Public attack inspection observed.", data));
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
        if (ValidateLegalMoveObservation(invocation, turnIntent, move, before) is { } refusal)
        {
            return refusal;
        }

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
                    ["agentToMove"] = SessionContext.AgentToMove,
                    ["currentLegalMoves"] = _rules.ListLegalMoves().Select(item => item.Uci).ToArray()
                });
        }

        var stateAfterAgentMove = CurrentState;
        _committedPlies.Add(new ChessQuestRecordedPly(
            Ply: _committedPlies.Count + 1,
            Move: agentResult.Move,
            Color: before.SideToMove,
            Source: "agent",
            FenBefore: agentResult.FenBefore,
            FenAfter: agentResult.FenAfter,
            At: DateTimeOffset.UtcNow));

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
                    _committedPlies.Add(new ChessQuestRecordedPly(
                        Ply: _committedPlies.Count + 1,
                        Move: opponentResult.Move,
                        Color: stateAfterAgentMove.SideToMove,
                        Source: "opponent",
                        FenBefore: opponentResult.FenBefore,
                        FenAfter: opponentResult.FenAfter,
                        At: DateTimeOffset.UtcNow));
                }
            }
        }

        _projectedLinesThisTurn = 0;
        var state = CurrentState;
        var data = Snapshot("play_move");
        data["turnIntent"] = turnIntent;
        data["legalMoveObservationId"] = ReadString(invocation, "legalMoveObservationId");
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
            ["sideToMoveInCheck"] = !state.IsTerminal && _rules.IsKingInCheck(state.SideToMove),
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
            SideToMoveInCheck: !state.IsTerminal && _rules.IsKingInCheck(state.SideToMove),
            Terminal: state.IsTerminal);
    }

    private void StoreLegalMoveObservation(
        Observation observation,
        Receipt receipt,
        IReadOnlyList<string> legalMoves)
    {
        var state = CurrentState;
        var snapshot = new ChessQuestLegalMoveObservation(
            observation.ObservationId,
            receipt.ReceiptId,
            state.Fen,
            state.Ply,
            state.SideToMove,
            legalMoves);
        _legalMoveObservations[observation.ObservationId] = snapshot;
        _legalMoveObservations[receipt.ReceiptId] = snapshot;
        _latestLegalMoveObservation = snapshot;
    }

    private ToolResult? ValidateLegalMoveObservation(
        ToolInvocation invocation,
        IReadOnlyDictionary<string, object?> turnIntent,
        string move,
        ChessPublicState currentState)
    {
        var normalizedMove = move.Trim().ToLowerInvariant();
        var observationId = ReadString(invocation, "legalMoveObservationId");
        var currentLegalMoveSnapshot = _latestLegalMoveObservation is { } latest &&
            string.Equals(latest.Fen, currentState.Fen, StringComparison.Ordinal) &&
            latest.Ply == currentState.Ply &&
            latest.SideToMove == currentState.SideToMove
                ? latest
                : null;

        if (string.IsNullOrWhiteSpace(observationId))
        {
            return Scenario.DisclosurePolicy.RequireLegalMoveObservationForPlay
                ? Refused(
                    invocation,
                    "missing_legal_move_observation_id",
                    "Strict gameplay requires legalMoveObservationId from the current chess.list_legal_moves observation before chess.play_move. Actor probes are the only surface that may bypass this binding.",
                    LegalMoveRefusalData(currentState, normalizedMove, currentLegalMoveSnapshot))
                : null;
        }

        if (!_legalMoveObservations.TryGetValue(observationId, out var snapshot))
        {
            return Refused(
                invocation,
                "unknown_legal_move_observation_id",
                "The supplied legalMoveObservationId is not known in this ChessQuest session.",
                LegalMoveRefusalData(currentState, normalizedMove, legalMoveObservationId: observationId));
        }

        if (!string.Equals(snapshot.Fen, currentState.Fen, StringComparison.Ordinal) ||
            snapshot.Ply != currentState.Ply ||
            snapshot.SideToMove != currentState.SideToMove)
        {
            return Refused(
                invocation,
                "stale_legal_move_observation",
                "The supplied legalMoveObservationId was produced for a previous board state. Refresh chess.list_legal_moves before playing.",
                LegalMoveRefusalData(currentState, normalizedMove, snapshot));
        }

        if (!snapshot.LegalMoves.Contains(normalizedMove, StringComparer.Ordinal))
        {
            return Refused(
                invocation,
                "move_not_in_legal_move_observation",
                $"The selected move '{normalizedMove}' was not present in the supplied legal move observation.",
                LegalMoveRefusalData(currentState, normalizedMove, snapshot));
        }

        return null;
    }

    private Dictionary<string, object?> LegalMoveRefusalData(
        ChessPublicState currentState,
        string requestedMove,
        ChessQuestLegalMoveObservation? observation = null,
        string? legalMoveObservationId = null) =>
        new(StringComparer.Ordinal)
        {
            ["agentMoveAccepted"] = false,
            ["requestedMove"] = requestedMove,
            ["fenUnchanged"] = true,
            ["agentToMove"] = SessionContext.AgentToMove,
            ["legalMoveObservationId"] = observation?.ObservationId ?? legalMoveObservationId,
            ["legalMoveObservationFen"] = observation?.Fen,
            ["legalMoveObservationPly"] = observation?.Ply,
            ["legalMoveObservationSideToMove"] = observation?.SideToMove.ToString().ToLowerInvariant(),
            ["legalMovesFromObservation"] = observation?.LegalMoves,
            ["currentFen"] = currentState.Fen,
            ["currentPly"] = currentState.Ply,
            ["currentSideToMove"] = currentState.SideToMove.ToString().ToLowerInvariant(),
            ["currentLegalMoves"] = _rules.ListLegalMoves().Select(item => item.Uci).ToArray()
        };

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

    private static string? ReadString(IReadOnlyDictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value)
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

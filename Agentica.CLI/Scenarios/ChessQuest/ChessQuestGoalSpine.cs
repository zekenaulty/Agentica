using Agentica.Artifacts;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed record ChessQuestGoalSpine(
    string Kind,
    string Version,
    DateTimeOffset UpdatedAt,
    string RootGoal,
    string CurrentReality,
    string ActivePriority,
    string? KnownDivergence,
    string NextDecisionPressure,
    IReadOnlyList<string> ActiveConstraints,
    IReadOnlyList<string> RecentLessons,
    IReadOnlyList<string> EvidenceRefs);

public static class ChessQuestGoalSpineCompiler
{
    public static ChessQuestGoalSpine Compile(
        ChessQuestSession session,
        ChessQuestPhaseTracker? phaseTracker = null,
        ChessQuestPhaseReport? latestPhaseReport = null)
    {
        var state = session.CurrentState;
        var currentPhase = phaseTracker?.Snapshot(session);
        var materialBalance = latestPhaseReport?.MaterialBalanceAfter ??
            MaterialBalance(state.Fen, session.Scenario.AgentColor);
        var recentRefusal = session.Turns
            .LastOrDefault(turn => turn.Result.Receipt.Status == ReceiptStatus.Refused);

        var currentReality = CurrentReality(
            session,
            state,
            materialBalance,
            currentPhase,
            latestPhaseReport);
        var activePriority = ActivePriority(
            session,
            state,
            materialBalance,
            currentPhase,
            latestPhaseReport);
        var knownDivergence = KnownDivergence(
            state,
            latestPhaseReport,
            recentRefusal);

        return new ChessQuestGoalSpine(
            Kind: "ChessQuestGoalSpine",
            Version: "1.0",
            UpdatedAt: DateTimeOffset.UtcNow,
            RootGoal: session.Scenario.PublicObjective,
            CurrentReality: currentReality,
            ActivePriority: activePriority,
            KnownDivergence: knownDivergence,
            NextDecisionPressure: NextDecisionPressure(
                session,
                state,
                materialBalance,
                currentPhase,
                latestPhaseReport,
                recentRefusal),
            ActiveConstraints: ActiveConstraints(session),
            RecentLessons: RecentLessons(latestPhaseReport, recentRefusal),
            EvidenceRefs: EvidenceRefs(state, latestPhaseReport, recentRefusal));
    }

    private static string CurrentReality(
        ChessQuestSession session,
        ChessPublicState state,
        int materialBalance,
        ChessQuestPhaseContext? currentPhase,
        ChessQuestPhaseReport? latestPhaseReport)
    {
        if (state.IsTerminal)
        {
            var result = state.TerminalState?.Result ?? "terminal";
            var winner = state.TerminalState?.Winner?.ToString() ?? "none";
            return $"Game is terminal: {result}; winner={winner}; verifier state is authoritative.";
        }

        if (latestPhaseReport is not null)
        {
            return
                $"Latest completed phase '{latestPhaseReport.Phase}' ended as {latestPhaseReport.Status} via {latestPhaseReport.StopReason}; " +
                $"agent material balance is {latestPhaseReport.MaterialBalanceAfter} after delta {latestPhaseReport.MaterialBalanceDelta}.";
        }

        if (currentPhase is not null)
        {
            return
                $"Active phase '{currentPhase.PhaseObjective.Phase}' is in progress; " +
                $"{currentPhase.Progress.AgentTurnsPlayed}/{currentPhase.PhaseObjective.MaxAgentTurns} agent turns used; " +
                $"agent material balance is {materialBalance}.";
        }

        return
            $"Game is in progress at ply {state.Ply}; agent plays {session.Scenario.AgentColor}; " +
            $"agent material balance is {materialBalance}.";
    }

    private static string ActivePriority(
        ChessQuestSession session,
        ChessPublicState state,
        int materialBalance,
        ChessQuestPhaseContext? currentPhase,
        ChessQuestPhaseReport? latestPhaseReport)
    {
        if (state.IsTerminal)
        {
            return state.TerminalState?.Winner == session.Scenario.AgentColor
                ? "Use the verifier completion path; the terminal win must still be proven by the objective artifact."
                : "Stop active play; terminal non-win is verifier truth and should be handled as outcome/postmortem, not more move planning.";
        }

        if (session.IsSideToMoveInCheck() && state.SideToMove == session.Scenario.AgentColor)
        {
            return "Resolve the current check with a legal move before pursuing any phase plan.";
        }

        if (latestPhaseReport is not null && latestPhaseReport.MaterialBalanceDelta < 0)
        {
            return "Stabilize and avoid further material loss before speculative tactical or conversion plans.";
        }

        if (materialBalance < 0)
        {
            return "Recover/stabilize while playing for a win; do not frame the position as conversion without verified compensation.";
        }

        if (currentPhase is not null)
        {
            return currentPhase.PhaseObjective.Goal;
        }

        return "Pursue the win objective through legal moves, verified claims, and receipt-backed board updates.";
    }

    private static string? KnownDivergence(
        ChessPublicState state,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestToolTurn? recentRefusal)
    {
        if (state.IsTerminal && state.TerminalState?.Winner is null)
        {
            return "The campaign goal requires a win, but the verifier reports a terminal draw.";
        }

        if (state.IsTerminal && state.TerminalState?.Winner is not null)
        {
            return "The game reached terminal state; further planning must respect the verifier outcome.";
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 })
        {
            return
                $"Expected phase progress diverged from receipts: phase '{latestPhaseReport.Phase}' lost material " +
                $"(delta {latestPhaseReport.MaterialBalanceDelta}).";
        }

        if (recentRefusal is not null)
        {
            var reason = ReadString(recentRefusal.Result.Receipt.Data, "reason") ??
                recentRefusal.Result.Receipt.Status.ToString();
            return $"Recent tool attempt was refused ({reason}); do not treat the attempted action as committed.";
        }

        return null;
    }

    private static string NextDecisionPressure(
        ChessQuestSession session,
        ChessPublicState state,
        int materialBalance,
        ChessQuestPhaseContext? currentPhase,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestToolTurn? recentRefusal)
    {
        if (state.IsTerminal)
        {
            return "Do not request another move; classify the terminal result and use completion only when the verifier conditions are met.";
        }

        if (session.IsSideToMoveInCheck() && state.SideToMove == session.Scenario.AgentColor)
        {
            return "Refresh legal moves, choose a legal check response, and keep claims limited to verifier-backed facts.";
        }

        if (recentRefusal is not null)
        {
            return "Refresh current state and legal move observation before retrying; refused or stale actions are not board truth.";
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 } || materialBalance < 0)
        {
            return "Model opponent replies before tactical/material/safety claims and keep uncertainty explicit when replies are unmodeled.";
        }

        if (currentPhase is not null)
        {
            return $"Advance the current '{currentPhase.PhaseObjective.Phase}' phase only through legal moves and evidence-backed turnIntent.";
        }

        return "Keep role, side-to-move, goal, and legal affordances bound to the current board before committing a move.";
    }

    private static IReadOnlyList<string> ActiveConstraints(ChessQuestSession session)
    {
        var constraints = new List<string>
        {
            "GoalSpine shapes continuity only; it is not proof and not a hidden solution source.",
            "Board state, legal move receipts, play_move receipts, and verifier artifacts override strategy narration.",
            "A legal move is not necessarily good or safe.",
            "A one-ply project_line result is rule projection only and does not prove move quality or tactical safety.",
            "Do not claim checkmate, safety, material gain, or forced results without verifier-backed evidence or explicit uncertainty."
        };

        if (session.Scenario.DisclosurePolicy.RequireLegalMoveObservationForPlay)
        {
            constraints.Add("Strict gameplay requires a fresh legalMoveObservationId before play_move.");
        }

        return constraints;
    }

    private static IReadOnlyList<string> RecentLessons(
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestToolTurn? recentRefusal)
    {
        var lessons = new List<string>();

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 })
        {
            lessons.Add("Latest receipt-backed phase report shows material loss; do not continue conversion framing until stabilized or compensated.");
            lessons.Add("Material loss after a tactical phase is evidence to tighten opponent-reply modeling, not evidence of tactical success.");
        }

        if (latestPhaseReport is { Terminal: true })
        {
            lessons.Add("Terminal state ends gameplay; outcome classification should dominate further planning.");
        }

        if (recentRefusal is not null)
        {
            lessons.Add("A refused tool result is a failed attempt, not a committed board mutation.");
        }

        if (lessons.Count == 0)
        {
            lessons.Add("Preserve legal-not-safe and projection-not-evaluation discipline across planning scopes.");
        }

        return lessons;
    }

    private static IReadOnlyList<string> EvidenceRefs(
        ChessPublicState state,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestToolTurn? recentRefusal)
    {
        var refs = new List<string> { $"board:ply:{state.Ply}" };

        if (latestPhaseReport is not null)
        {
            refs.Add($"phase:{latestPhaseReport.PhaseRunId}");
            refs.AddRange(latestPhaseReport.Evidence.Take(8).Select(item => $"phaseEvidence:{item}"));
        }

        if (recentRefusal is not null)
        {
            refs.Add($"receipt:{recentRefusal.Result.Receipt.ReceiptId}");
        }

        return refs;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value)
            : null;

    private static int MaterialBalance(string fen, ChessQuestColor agentColor)
    {
        var board = fen.Split(' ', 2)[0];
        var score = 0;
        foreach (var character in board)
        {
            var value = char.ToLowerInvariant(character) switch
            {
                'p' => 1,
                'n' => 3,
                'b' => 3,
                'r' => 5,
                'q' => 9,
                _ => 0
            };
            if (value == 0)
            {
                continue;
            }

            var isWhite = char.IsUpper(character);
            var isAgentPiece = agentColor == ChessQuestColor.White ? isWhite : !isWhite;
            score += isAgentPiece ? value : -value;
        }

        return score;
    }
}

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed record ChessQuestContinuityCapsule(
    string Kind,
    string Version,
    DateTimeOffset UpdatedAt,
    string ScenarioId,
    ChessQuestColor AgentColor,
    string CurrentFen,
    int Ply,
    string StrategicIntent,
    IReadOnlyList<string> PressurePoints,
    IReadOnlyList<string> ThreatHypotheses,
    IReadOnlyList<string> Uncertainties,
    string Confidence,
    string ConfidenceRationale,
    string RecommendedNextBias,
    IReadOnlyList<string> RecentAgentMoves,
    IReadOnlyList<string> RecentOpponentMoves,
    IReadOnlyList<string> EvidenceRefs,
    ChessQuestContinuityCapsule? PriorRunCarryover = null);

public static class ChessQuestContinuityCapsuleCompiler
{
    public static ChessQuestContinuityCapsule Compile(
        ChessQuestSession session,
        ChessQuestPhaseReport? latestPhaseReport = null,
        ChessQuestStrategyProjection? latestStrategyProjection = null)
    {
        var state = session.CurrentState;
        var materialBalance = MaterialBalance(state.Fen, session.Scenario.AgentColor);
        var goalSpine = ChessQuestGoalSpineCompiler.Compile(session, latestPhaseReport: latestPhaseReport);
        var terminal = state.TerminalState;
        var agentWon = terminal?.Winner == session.Scenario.AgentColor;
        var recentAgentMoves = session.CommittedPlies
            .Where(ply => string.Equals(ply.Source, "agent", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .Select(ply => ply.Move)
            .ToArray();
        var recentOpponentMoves = session.CommittedPlies
            .Where(ply => string.Equals(ply.Source, "opponent", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .Select(ply => ply.Move)
            .ToArray();

        var pressures = PressurePoints(session, latestPhaseReport, goalSpine, materialBalance).ToArray();
        var uncertainties = Uncertainties(session, latestPhaseReport, goalSpine).ToArray();
        var threats = ThreatHypotheses(session, latestPhaseReport, materialBalance).ToArray();
        var confidence = Confidence(state, latestPhaseReport, materialBalance, goalSpine);
        var strategicIntent = latestStrategyProjection is not null
            ? $"{latestStrategyProjection.Phase}: {latestStrategyProjection.StrategyIntent}"
            : "Play for a verified win while keeping claims bound to public chess evidence.";

        return new ChessQuestContinuityCapsule(
            Kind: "chessquest.continuity_capsule",
            Version: "1.0",
            UpdatedAt: DateTimeOffset.UtcNow,
            ScenarioId: session.Scenario.ScenarioId,
            AgentColor: session.Scenario.AgentColor,
            CurrentFen: state.Fen,
            Ply: state.Ply,
            StrategicIntent: Compact(strategicIntent),
            PressurePoints: Limit(pressures, 8),
            ThreatHypotheses: Limit(threats, 6),
            Uncertainties: Limit(uncertainties, 6),
            Confidence: confidence.Level,
            ConfidenceRationale: confidence.Rationale,
            RecommendedNextBias: RecommendedNextBias(session, state, latestPhaseReport, materialBalance, goalSpine),
            RecentAgentMoves: recentAgentMoves,
            RecentOpponentMoves: recentOpponentMoves,
            EvidenceRefs: EvidenceRefs(state, latestPhaseReport, goalSpine)
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToArray(),
            PriorRunCarryover: session.ImportedContinuityCapsule is null
                ? null
                : session.ImportedContinuityCapsule with { PriorRunCarryover = null });
    }

    private static IEnumerable<string> PressurePoints(
        ChessQuestSession session,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestGoalSpine goalSpine,
        int materialBalance)
    {
        var state = session.CurrentState;
        if (state.IsTerminal)
        {
            yield return $"Terminal state: {state.TerminalState?.Result ?? "terminal"}; winner={state.TerminalState?.Winner?.ToString() ?? "none"}.";
        }

        if (session.IsSideToMoveInCheck() && state.SideToMove == session.Scenario.AgentColor)
        {
            yield return "Agent king is currently in check; legal check response has priority.";
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 })
        {
            yield return $"Latest phase lost material (delta {latestPhaseReport.MaterialBalanceDelta}).";
        }

        if (materialBalance < 0)
        {
            yield return $"Agent material balance is negative ({materialBalance}).";
        }

        if (!string.IsNullOrWhiteSpace(goalSpine.KnownDivergence))
        {
            yield return goalSpine.KnownDivergence!;
        }

        yield return goalSpine.ActivePriority;
    }

    private static IEnumerable<string> ThreatHypotheses(
        ChessQuestSession session,
        ChessQuestPhaseReport? latestPhaseReport,
        int materialBalance)
    {
        if (session.IsSideToMoveInCheck() && session.CurrentState.SideToMove == session.Scenario.AgentColor)
        {
            yield return "Immediate king-safety threat is verified by public check status.";
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 })
        {
            yield return "Prior phase result suggests opponent replies or exchanges were underestimated.";
        }

        if (materialBalance < 0)
        {
            yield return "Material deficit may make speculative conversion framing unreliable.";
        }

        yield return "Any tactical safety claim remains a hypothesis until opponent replies or verifier facts support it.";
    }

    private static IEnumerable<string> Uncertainties(
        ChessQuestSession session,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestGoalSpine goalSpine)
    {
        if (!session.CurrentState.IsTerminal)
        {
            yield return "Opponent reply quality is not predicted by the referee surface.";
            yield return "The next useful line, threat, or refutation may still need explicit public-rule verification.";
        }

        if (latestPhaseReport is null)
        {
            yield return "No completed phase report is available for this continuity capsule.";
        }

        if (!string.IsNullOrWhiteSpace(goalSpine.KnownDivergence))
        {
            yield return "Latest divergence should be treated as pressure, not proof of the next best move.";
        }

        yield return "Legal and projection evidence does not by itself prove move quality or full safety.";
    }

    private static (string Level, string Rationale) Confidence(
        ChessPublicState state,
        ChessQuestPhaseReport? latestPhaseReport,
        int materialBalance,
        ChessQuestGoalSpine goalSpine)
    {
        if (state.IsTerminal)
        {
            return ("high", "Terminal verifier state is authoritative.");
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 } ||
            materialBalance < 0)
        {
            return ("low", "Recent evidence contains material loss or material deficit.");
        }

        if (!string.IsNullOrWhiteSpace(goalSpine.KnownDivergence))
        {
            return ("low", "Recent evidence contains claim, refusal, or continuity divergence.");
        }

        if (latestPhaseReport is not null)
        {
            return ("medium", "A phase report exists, but strategic claims remain soft unless receipt-backed.");
        }

        return ("medium", "Board state is public, but no phase-level continuity report has been completed yet.");
    }

    private static string RecommendedNextBias(
        ChessQuestSession session,
        ChessPublicState state,
        ChessQuestPhaseReport? latestPhaseReport,
        int materialBalance,
        ChessQuestGoalSpine goalSpine)
    {
        if (state.IsTerminal)
        {
            return state.TerminalState?.Winner == session.Scenario.AgentColor
                ? "Use verifier completion path; no more move planning."
                : "Stop gameplay and produce postmortem; terminal non-win is verifier truth.";
        }

        if (session.IsSideToMoveInCheck() && state.SideToMove == session.Scenario.AgentColor)
        {
            return "Prioritize legal check resolution before any strategic plan.";
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 } || materialBalance < 0)
        {
            return "Prefer stabilization/recovery and explicit opponent-reply modeling before tactical or conversion claims.";
        }

        if (!string.IsNullOrWhiteSpace(goalSpine.KnownDivergence))
        {
            return goalSpine.NextDecisionPressure;
        }

        return "Continue the current strategic phase while binding action to fresh legal-move evidence.";
    }

    private static IEnumerable<string> EvidenceRefs(
        ChessPublicState state,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestGoalSpine goalSpine)
    {
        yield return $"board:ply:{state.Ply}";
        foreach (var evidence in goalSpine.EvidenceRefs.Take(8))
        {
            yield return evidence;
        }

        if (latestPhaseReport is not null)
        {
            yield return $"phase:{latestPhaseReport.PhaseRunId}";
        }
    }

    private static IReadOnlyList<string> Limit(IEnumerable<string> values, int limit) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Compact)
            .Distinct(StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 280 ? compact : compact[..277] + "...";
    }

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

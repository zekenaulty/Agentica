using Agentica.Artifacts;
using System.Text.RegularExpressions;

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
        var latestClaimDrift = LatestClaimDrift(session);

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
            recentRefusal,
            latestClaimDrift);

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
                recentRefusal,
                latestClaimDrift),
            ActiveConstraints: ActiveConstraints(session),
            RecentLessons: RecentLessons(session, latestPhaseReport, recentRefusal, latestClaimDrift),
            EvidenceRefs: EvidenceRefs(state, latestPhaseReport, recentRefusal, latestClaimDrift));
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
        ChessQuestToolTurn? recentRefusal,
        ChessQuestClaimDrift? latestClaimDrift)
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

        if (latestClaimDrift is not null)
        {
            return latestClaimDrift.Summary;
        }

        return null;
    }

    private static string NextDecisionPressure(
        ChessQuestSession session,
        ChessPublicState state,
        int materialBalance,
        ChessQuestPhaseContext? currentPhase,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestToolTurn? recentRefusal,
        ChessQuestClaimDrift? latestClaimDrift)
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
            var reason = ReadString(recentRefusal.Result.Receipt.Data, "reason");
            return reason switch
            {
                "stale_legal_move_observation" =>
                    "Refresh current state and legal move observation before retrying; stale legal-affordance IDs are not valid after board mutation.",
                "move_not_in_legal_move_observation" =>
                    "Reselect from the current legal move observation; do not claim a move is listed unless it appears in that exact observation.",
                "missing_legal_move_observation_id" =>
                    "Call chess.list_legal_moves first and bind the selected move to its current legalMoveObservationId before retrying.",
                _ =>
                    "Refresh current state and legal move observation before retrying; refused actions are not board truth."
            };
        }

        if (latestClaimDrift is not null)
        {
            return latestClaimDrift.NextDecisionPressure;
        }

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 } || materialBalance < 0)
        {
            return session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection
                ? "Use available evidence depth before tactical/material/safety claims: candidate scans can invalidate candidates, opponent replies may still be unmodeled, and uncertainty must remain explicit."
                : "Use available evidence depth before tactical/material/safety claims: model opponent replies when needed and keep uncertainty explicit when replies are unmodeled.";
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

        if (session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection)
        {
            constraints.Add("A candidate scan reports neutral opponent capture facts after your submitted move; bad scan results should invalidate or weaken the candidate, and quiet scans still do not prove full safety.");
        }

        if (session.Scenario.DisclosurePolicy.RequireLegalMoveObservationForPlay)
        {
            constraints.Add("Strict gameplay requires a fresh legalMoveObservationId before play_move.");
        }

        return constraints;
    }

    private static IReadOnlyList<string> RecentLessons(
        ChessQuestSession session,
        ChessQuestPhaseReport? latestPhaseReport,
        ChessQuestToolTurn? recentRefusal,
        ChessQuestClaimDrift? latestClaimDrift)
    {
        var lessons = new List<string>();

        if (latestPhaseReport is { MaterialBalanceDelta: < 0 })
        {
            lessons.Add("Latest receipt-backed phase report shows material loss; do not continue conversion framing until stabilized or compensated.");
            lessons.Add("Material loss after a tactical phase is evidence to tighten opponent-reply modeling, not evidence of tactical success.");
            lessons.Add(session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection
                ? "Current attack facts and one-ply projections can be invalid signal for candidate safety; use candidate after-state facts to disprove candidates, not to prove full safety."
                : "Current attack facts and one-ply projections can be invalid signal for candidate safety; model opponent replies or keep uncertainty explicit.");
        }

        if (latestPhaseReport is { Terminal: true })
        {
            lessons.Add("Terminal state ends gameplay; outcome classification should dominate further planning.");
        }

        if (recentRefusal is not null)
        {
            var reason = ReadString(recentRefusal.Result.Receipt.Data, "reason");
            lessons.Add(reason switch
            {
                "move_not_in_legal_move_observation" =>
                    "A move absent from the bound legal observation is a selection/binding failure; choose from the current legal list instead of just refreshing by habit.",
                "stale_legal_move_observation" =>
                    "A stale legal observation means the board changed; refresh legal moves before selecting again.",
                _ => "A refused tool result is a failed attempt, not a committed board mutation."
            });
        }

        if (latestClaimDrift is not null)
        {
            lessons.Add(latestClaimDrift.Lesson);
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
        ChessQuestToolTurn? recentRefusal,
        ChessQuestClaimDrift? latestClaimDrift)
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

        if (latestClaimDrift is not null)
        {
            refs.Add(latestClaimDrift.EvidenceRef);
        }

        return refs;
    }

    private static ChessQuestClaimDrift? LatestClaimDrift(ChessQuestSession session)
    {
        var playTurn = session.Turns.LastOrDefault(turn =>
            turn.Invocation.ToolId == ChessQuestToolIds.PlayMove &&
            turn.Result.Receipt.Data.TryGetValue("turnIntent", out _));
        if (playTurn is null)
        {
            return null;
        }

        var data = playTurn.Result.Receipt.Data;
        var turnIntent = ReadDictionary(playTurn.Invocation.Input, "turnIntent") ??
            ReadDictionary(data, "turnIntent");
        var projection = ReadDictionary(data, "agentMoveProjection");
        var declaredText = string.Join(
            " ",
            new[]
            {
                ReadString(turnIntent, "goal"),
                ReadString(turnIntent, "hypothesis"),
                ReadString(turnIntent, "riskCheck"),
                ReadString(turnIntent, "claimLevel"),
                ReadString(turnIntent, "publicReason")
            }.Where(item => !string.IsNullOrWhiteSpace(item)));
        var riskCheck = ReadString(turnIntent, "riskCheck");
        var agentMoveGivesCheck = ReadBool(projection, "agentMoveGivesCheck");
        var agentMoveGivesCheckmate = ReadBool(projection, "agentMoveGivesCheckmate");
        var terminal = ReadBool(data, "terminal");
        var completionClaim = ReadBool(turnIntent, "completionClaim");
        var evidenceRef = $"receipt:{playTurn.Result.Receipt.ReceiptId}";

        if (ChessQuestGoalShapingPolicy.HasAffirmativeMateClaim(declaredText) && !agentMoveGivesCheckmate)
        {
            return new ChessQuestClaimDrift(
                "Turn intent claimed mate/checkmate, but the committed move was not terminal checkmate.",
                "Checkmate claims require terminal verifier or agent-move projection evidence; wording alone is not proof.",
                evidenceRef,
                "Before any mate/checkmate claim, project the submitted line to terminal state or leave the claim as an unverified hypothesis.");
        }

        if (MentionsCheck(declaredText) && !agentMoveGivesCheck)
        {
            return new ChessQuestClaimDrift(
                "Turn intent claimed check, but the committed move did not give check.",
                "Check claims must be bound to move projection or resulting board facts.",
                evidenceRef,
                "Treat check claims as hypotheses until the committed move projection says agentMoveGivesCheck=true.");
        }

        if (completionClaim && !terminal)
        {
            return new ChessQuestClaimDrift(
                "Turn intent claimed completion, but the verifier state was not terminal.",
                "Completion claims require verifier artifact/terminal evidence; turnIntent cannot complete the objective.",
                evidenceRef,
                "Do not claim completion until the verifier path emits the objective artifact or terminal win evidence.");
        }

        if (HasSafetyClaim(declaredText) && !AdmitsUnmodeledRisk(riskCheck))
        {
            return new ChessQuestClaimDrift(
                "Turn intent made a safety claim without explicit unmodeled-risk discipline.",
                "Legal moves, attack facts, one-ply projection, and quiet candidate scans are limited evidence; they do not prove full tactical safety.",
                evidenceRef,
                session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection
                    ? "Use available candidate/opponent-reply evidence to invalidate risky candidates, and state uncertainty unless opponent replies are modeled."
                    : "Model plausible opponent replies or state explicit uncertainty before calling a move safe.");
        }

        return null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?>? source, string key) =>
        source is not null &&
        source.TryGetValue(key, out var value) &&
        value is not null
            ? Convert.ToString(value)
            : null;

    private static IReadOnlyDictionary<string, object?>? ReadDictionary(
        IReadOnlyDictionary<string, object?> source,
        string key) =>
        source.TryGetValue(key, out var value) &&
        value is IReadOnlyDictionary<string, object?> dictionary
            ? dictionary
            : null;

    private static bool ReadBool(IReadOnlyDictionary<string, object?>? source, string key) =>
        source is not null &&
        source.TryGetValue(key, out var value) &&
        value switch
        {
            bool boolValue => boolValue,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            _ => false
        };

    private static bool MentionsCheck(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(
            text,
            @"\b(?:give|gives|giving|deliver|delivers|delivering)\s+check\b|\bcheck(?:s|ing)?\s+the\s+king\b|\bwith\s+check\b|\bis\s+check\b",
            RegexOptions.IgnoreCase);

    private static bool HasSafetyClaim(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"\b(?:safe|safety|secure|harmless|no risk)\b", RegexOptions.IgnoreCase);

    private static bool AdmitsUnmodeledRisk(string? riskCheck) =>
        !string.IsNullOrWhiteSpace(riskCheck) &&
        (riskCheck.Contains("unverified", StringComparison.OrdinalIgnoreCase) ||
         riskCheck.Contains("not modeled", StringComparison.OrdinalIgnoreCase) ||
         riskCheck.Contains("unmodeled", StringComparison.OrdinalIgnoreCase) ||
         riskCheck.Contains("not fully modeled", StringComparison.OrdinalIgnoreCase));

    private sealed record ChessQuestClaimDrift(
        string Summary,
        string Lesson,
        string EvidenceRef,
        string NextDecisionPressure);

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

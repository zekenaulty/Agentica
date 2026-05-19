using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentica.Events;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class ChessQuestCockpitEventSink : IEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ChessQuestSession _session;
    private readonly bool _turnJson;
    private readonly Action<ChessQuestCockpitTurnEnvelope>? _turnRecorder;
    private readonly Dictionary<string, ExecutionIntent> _stepIntents = new(StringComparer.Ordinal);
    private readonly List<ChessQuestProjectedLineSummary> _pendingProjections = [];
    private int? _lastLegalMoveCount;
    private int _turnNumber;

    static ChessQuestCockpitEventSink()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public ChessQuestCockpitEventSink(
        ChessQuestSession session,
        bool turnJson = false,
        Action<ChessQuestCockpitTurnEnvelope>? turnRecorder = null)
    {
        _session = session;
        _turnJson = turnJson;
        _turnRecorder = turnRecorder;
    }

    public void Emit(ExecutionEvent executionEvent)
    {
        if (executionEvent.Type == "step.started" &&
            executionEvent.Context?.StepId is { } stepId &&
            executionEvent.Intent is not null)
        {
            _stepIntents[stepId] = executionEvent.Intent;
            return;
        }

        if (executionEvent.Type == "receipt.emitted")
        {
            PrintToolReceipt(executionEvent);
            return;
        }

        switch (executionEvent.Type)
        {
            case "run.created":
                Console.WriteLine($"[run] started {Data(executionEvent, "run")}");
                break;
            case "request.accepted":
                Console.WriteLine("[request] accepted");
                break;
            case "outcome.reported":
                Console.WriteLine($"[outcome] {executionEvent.UserFacingReason?.Summary ?? "reported"}");
                if (!string.IsNullOrWhiteSpace(executionEvent.UserFacingReason?.Detail))
                {
                    Console.WriteLine($"  detail: {Compact(executionEvent.UserFacingReason.Detail)}");
                }

                break;
            case "run.succeeded":
                Console.WriteLine("[run] succeeded");
                break;
            case "run.blocked":
                Console.WriteLine("[run] blocked");
                break;
            case "run.failed":
                Console.WriteLine("[run] failed");
                if (!string.IsNullOrWhiteSpace(executionEvent.UserFacingReason?.Detail))
                {
                    Console.WriteLine($"  detail: {Compact(executionEvent.UserFacingReason.Detail)}");
                }

                break;
        }
    }

    private void PrintToolReceipt(ExecutionEvent executionEvent)
    {
        var receiptId = executionEvent.Context?.ReceiptId;
        var turn = string.IsNullOrWhiteSpace(receiptId)
            ? _session.Turns.LastOrDefault()
            : _session.Turns.LastOrDefault(item =>
                string.Equals(item.Result.Receipt.ReceiptId, receiptId, StringComparison.Ordinal));
        if (turn is null)
        {
            return;
        }

        switch (turn.Invocation.ToolId)
        {
            case ChessQuestToolIds.GetState:
                PrintState(turn);
                break;
            case ChessQuestToolIds.ListLegalMoves:
                PrintLegalMoves(turn);
                break;
            case ChessQuestToolIds.ProjectLine:
                PrintProjection(turn);
                break;
            case ChessQuestToolIds.InspectAttacks:
                PrintAttackInspection(turn);
                break;
            case ChessQuestToolIds.PlayMove:
                PrintMoveTurn(turn);
                break;
            case ChessQuestToolIds.CompleteObjective:
                PrintCompletion(turn);
                break;
        }
    }

    private static void PrintAttackInspection(ChessQuestToolTurn turn)
    {
        if (!turn.Result.Receipt.Data.TryGetValue("inspection", out var value) ||
            value is not ChessAttackInspection inspection)
        {
            return;
        }

        Console.WriteLine(
            $"[attacks] opponent legal captures={inspection.OpponentLegalCaptures.Count}; capturable agent pieces={inspection.AttackedAgentPieces.Count}; agentKingInCheck={inspection.AgentKingInCheck}");
        foreach (var attacked in inspection.AttackedAgentPieces.Take(8))
        {
            Console.WriteLine($"  - {attacked.Piece} on {attacked.Square}: {string.Join(", ", attacked.CaptureMoves)}");
        }
    }

    private void PrintState(ChessQuestToolTurn turn)
    {
        var context = _session.SessionContext;
        Console.WriteLine();
        Console.WriteLine($"[state] role={context.AgentColor} sideToMove={context.SideToMove} agentToMove={context.AgentToMove} ply={context.Ply}");
        Console.WriteLine($"  goal: {context.Objective.PublicDescription}");
    }

    private void PrintLegalMoves(ChessQuestToolTurn turn)
    {
        var legalMoves = ReadStringList(turn.Result.Receipt.Data, "legalMoves");
        _lastLegalMoveCount = legalMoves.Count;
        Console.WriteLine($"[context] {_session.CurrentState.SideToMove} to move; {legalMoves.Count} legal UCI moves available.");
    }

    private void PrintProjection(ChessQuestToolTurn turn)
    {
        if (!turn.Result.Receipt.Data.TryGetValue("projection", out var value) ||
            value is not ChessLineProjection projection)
        {
            return;
        }

        var requestedLine = ReadInputLine(turn.Invocation.Input, "line");
        var summary = new ChessQuestProjectedLineSummary(
            RequestedLine: requestedLine,
            AcceptedPrefix: projection.AcceptedPrefix,
            RejectedMove: projection.RejectedAt?.Move,
            RejectionReason: projection.RejectedAt?.Reason,
            Terminal: projection.Terminal,
            TerminalResult: projection.TerminalState?.Result,
            SideToMoveAfter: projection.SideToMoveAfter,
            FenAfter: projection.FenAfter,
            Note: projection.Note);
        _pendingProjections.Add(summary);

        Console.WriteLine("[projection] agent-authored line");
        Console.WriteLine($"  requested: {FormatLine(summary.RequestedLine)}");
        Console.WriteLine($"  accepted:  {FormatLine(summary.AcceptedPrefix)}");
        if (summary.RejectedMove is not null)
        {
            Console.WriteLine($"  rejected:  {summary.RejectedMove} ({summary.RejectionReason})");
        }

        Console.WriteLine($"  result: sideToMove={summary.SideToMoveAfter} terminal={summary.Terminal}");
        if (!string.IsNullOrWhiteSpace(summary.Note))
        {
            Console.WriteLine($"  note: {Compact(summary.Note)}");
        }
    }

    private void PrintMoveTurn(ChessQuestToolTurn turn)
    {
        var data = turn.Result.Receipt.Data;
        var stepId = turn.Invocation.StepId;
        _stepIntents.TryGetValue(stepId, out var intent);
        var turnIntent = ReadDictionary(turn.Invocation.Input, "turnIntent") ??
            ReadDictionary(data, "turnIntent");
        var publicReason = ReadString(turnIntent, "publicReason");
        var turnGoal = ReadString(turnIntent, "goal");
        var turnEvidence = ReadStringList(turnIntent, "evidence");
        var turnHypothesis = ReadString(turnIntent, "hypothesis");
        var turnRiskCheck = ReadString(turnIntent, "riskCheck");
        var turnClaimLevel = ReadString(turnIntent, "claimLevel");
        var selectedMove = ReadString(data, "agentMove") ??
            ReadString(data, "requestedMove") ??
            ReadString(turn.Invocation.Input, "move") ??
            "unknown";
        var agentMoveAccepted = turn.Result.Receipt.Status == Agentica.Artifacts.ReceiptStatus.Succeeded &&
            ReadBool(data, "agentMoveAccepted");
        var opponentMove = ReadString(data, "opponentMove");
        var opponentMoveApplied = ReadBool(data, "opponentMoveApplied");
        var fenUnchanged = ReadBool(data, "fenUnchanged");
        var fenAfter = ReadString(data, "fenAfter") ?? turn.StateAfter.Fen;
        var terminal = ReadBool(data, "terminal") || turn.StateAfter.IsTerminal;
        var terminalResult = turn.StateAfter.TerminalState?.Result;
        var completionClaim = ReadOptionalBool(turnIntent, "completionClaim");
        var agentMoveProjection = ReadDictionary(data, "agentMoveProjection");
        var agentMoveGivesCheck = ReadOptionalBool(agentMoveProjection, "agentMoveGivesCheck");
        var agentMoveGivesCheckmate = ReadOptionalBool(agentMoveProjection, "agentMoveGivesCheckmate");

        var envelope = new ChessQuestCockpitTurnEnvelope(
            TurnNumber: ++_turnNumber,
            StepId: stepId,
            ReceiptId: turn.Result.Receipt.ReceiptId,
            ReceiptStatus: turn.Result.Receipt.Status,
            ReceiptMessage: turn.Result.Receipt.Message,
            AgentColor: _session.Scenario.AgentColor,
            SideToMoveAfter: turn.StateAfter.SideToMove,
            PlyAfter: turn.StateAfter.Ply,
            SelectedMove: selectedMove,
            OpponentMove: opponentMove,
            OpponentMoveApplied: opponentMoveApplied,
            Terminal: terminal,
            TerminalResult: terminalResult,
            FenAfter: fenAfter,
            PublicIntentAction: intent?.Action,
            PublicIntentRationale: intent?.Rationale,
            PublicIntentExpectedOutcome: intent?.ExpectedOutcome,
            TurnPublicReason: publicReason,
            LegalMoveCountBeforeMove: _lastLegalMoveCount,
            CandidateLinesExplored: _pendingProjections.ToArray())
        {
            AgentMoveAccepted = agentMoveAccepted,
            FenUnchanged = fenUnchanged,
            CommittedAgentTurnNumber = agentMoveAccepted
                ? _session.CommittedPlies.Count(ply => string.Equals(ply.Source, "agent", StringComparison.Ordinal))
                : null,
            TurnGoal = turnGoal,
            TurnEvidence = turnEvidence,
            TurnHypothesis = turnHypothesis,
            TurnRiskCheck = turnRiskCheck,
            TurnClaimLevel = turnClaimLevel,
            Warnings = BuildWarnings(
                intent,
                turnIntent,
                _pendingProjections,
                publicReason,
                completionClaim,
                agentMoveGivesCheck,
                agentMoveGivesCheckmate,
                terminal,
                agentMoveAccepted,
                fenUnchanged)
        };

        _turnRecorder?.Invoke(envelope);
        PrintMoveEnvelope(envelope);
        _pendingProjections.Clear();
        _lastLegalMoveCount = null;
    }

    private void PrintMoveEnvelope(ChessQuestCockpitTurnEnvelope envelope)
    {
        Console.WriteLine();
        Console.WriteLine($"=== ChessQuest Move Attempt {envelope.TurnNumber:000} | role={envelope.AgentColor} ===");
        if (envelope.CommittedAgentTurnNumber is not null)
        {
            Console.WriteLine($"Committed agent turn: {envelope.CommittedAgentTurnNumber}");
        }

        if (envelope.LegalMoveCountBeforeMove is not null)
        {
            Console.WriteLine($"Legal moves considered: {envelope.LegalMoveCountBeforeMove}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.PublicIntentAction))
        {
            Console.WriteLine($"Intent: {Compact(envelope.PublicIntentAction)}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.PublicIntentRationale))
        {
            Console.WriteLine($"Rationale: {Compact(envelope.PublicIntentRationale)}");
        }

        if (envelope.CandidateLinesExplored.Count > 0)
        {
            Console.WriteLine("Candidate lines explored:");
            foreach (var projection in envelope.CandidateLinesExplored)
            {
                var suffix = projection.RejectedMove is null
                    ? $"sideAfter={projection.SideToMoveAfter} terminal={projection.Terminal}"
                    : $"rejected={projection.RejectedMove}:{projection.RejectionReason}";
                Console.WriteLine($"  - {FormatLine(projection.RequestedLine)} -> {FormatLine(projection.AcceptedPrefix)} ({suffix})");
            }
        }
        else
        {
            Console.WriteLine("Candidate lines explored: none this turn");
        }

        Console.WriteLine($"Selected move: {envelope.SelectedMove}");
        if (!string.IsNullOrWhiteSpace(envelope.TurnPublicReason))
        {
            Console.WriteLine($"Public reason: {Compact(envelope.TurnPublicReason)}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.TurnGoal))
        {
            Console.WriteLine($"Turn goal: {Compact(envelope.TurnGoal)}");
        }

        if (envelope.TurnEvidence.Count > 0)
        {
            Console.WriteLine("Evidence declared:");
            foreach (var evidence in envelope.TurnEvidence)
            {
                Console.WriteLine($"  - {Compact(evidence)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(envelope.TurnHypothesis))
        {
            Console.WriteLine($"Hypothesis: {Compact(envelope.TurnHypothesis)}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.TurnRiskCheck))
        {
            Console.WriteLine($"Risk check: {Compact(envelope.TurnRiskCheck)}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.TurnClaimLevel))
        {
            Console.WriteLine($"Claim level: {Compact(envelope.TurnClaimLevel)}");
        }

        foreach (var warning in envelope.Warnings)
        {
            Console.WriteLine($"Trace warning: {warning}");
        }

        Console.WriteLine(FormatOutcome(envelope));
        if (!envelope.AgentMoveAccepted)
        {
            Console.WriteLine($"FEN unchanged: {envelope.FenUnchanged}");
            Console.WriteLine("Opponent move: none because the agent move was not committed.");
        }

        Console.WriteLine($"State: ply={envelope.PlyAfter} sideToMove={envelope.SideToMoveAfter} terminal={envelope.Terminal}");
        Console.WriteLine($"FEN: {envelope.FenAfter}");

        if (_turnJson)
        {
            Console.WriteLine();
            Console.WriteLine("--- ChessQuest TurnEnvelope ---");
            Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
        }
    }

    private void PrintCompletion(ChessQuestToolTurn turn)
    {
        var status = turn.Result.Receipt.Status;
        var artifact = turn.Result.Artifact;
        Console.WriteLine();
        Console.WriteLine($"[completion] {status}: {turn.Result.Receipt.Message}");
        if (artifact is not null)
        {
            Console.WriteLine($"  artifact: {artifact.ArtifactId} kind={artifact.Kind}");
        }
    }

    private static IReadOnlyDictionary<string, object?>? ReadDictionary(
        IReadOnlyDictionary<string, object?> source,
        string key) =>
        source.TryGetValue(key, out var value)
            ? value as IReadOnlyDictionary<string, object?> ??
              (value as IDictionary<string, object?>)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : null;

    private static string? ReadString(
        IReadOnlyDictionary<string, object?>? source,
        string key)
    {
        if (source is null || !source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return Convert.ToString(value);
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value is bool boolean
            ? boolean
            : bool.TryParse(Convert.ToString(value), out var parsed) && parsed;
    }

    private static bool ReadOptionalBool(
        IReadOnlyDictionary<string, object?>? source,
        string key) =>
        source is not null && ReadBool(source, key);

    private static IReadOnlyList<string> ReadStringList(
        IReadOnlyDictionary<string, object?>? source,
        string key)
    {
        if (source is null || !source.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object> objects => objects
                .Select(Convert.ToString)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray(),
            _ => []
        };
    }

    private static IReadOnlyList<string> ReadInputLine(
        IReadOnlyDictionary<string, object?> source,
        string key) =>
        ReadStringList(source, key)
            .Select(item => item.Trim().ToLowerInvariant())
            .ToArray();

    private static string FormatLine(IReadOnlyList<string> line) =>
        line.Count == 0 ? "(none)" : string.Join(" ", line);

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 260 ? compact : compact[..257] + "...";
    }

    private static IReadOnlyList<string> BuildWarnings(
        ExecutionIntent? intent,
        IReadOnlyDictionary<string, object?>? turnIntent,
        IReadOnlyList<ChessQuestProjectedLineSummary> projections,
        string? publicReason,
        bool completionClaim,
        bool agentMoveGivesCheck,
        bool agentMoveGivesCheckmate,
        bool terminal,
        bool agentMoveAccepted,
        bool fenUnchanged)
    {
        var warnings = new List<string>();
        var declaredText = string.Join(
            " ",
            new[]
            {
                intent?.Action,
                intent?.Rationale,
                intent?.ExpectedOutcome,
                ReadString(turnIntent, "goal"),
                ReadString(turnIntent, "hypothesis"),
                ReadString(turnIntent, "riskCheck"),
                ReadString(turnIntent, "claimLevel"),
                publicReason
            }.Where(item => !string.IsNullOrWhiteSpace(item)));
        var riskCheck = ReadString(turnIntent, "riskCheck");
        var claimLevel = ReadString(turnIntent, "claimLevel");
        var evidence = ReadStringList(turnIntent, "evidence");

        if (!agentMoveAccepted)
        {
            warnings.Add(fenUnchanged
                ? "selected move was refused and did not mutate the board."
                : "selected move was refused; verify board state before retrying.");
        }

        if (ChessQuestGoalShapingPolicy.HasAffirmativeMateClaim(declaredText) && !agentMoveGivesCheckmate)
        {
            warnings.Add("declared mate/checkmate, but the committed agent move was not checkmate.");
        }
        else if (MentionsCheck(declaredText) && !agentMoveGivesCheck)
        {
            warnings.Add("declared check, but the committed agent move did not give check.");
        }

        if (completionClaim && !terminal)
        {
            warnings.Add("turnIntent.completionClaim was true, but the committed result is not terminal.");
        }

        if (ChessQuestGoalShapingPolicy.HasRiskyClaimLanguage(declaredText) &&
            !HasAdequateClaimDiscipline(evidence, riskCheck, claimLevel))
        {
            warnings.Add("intent uses strong claim language without enough public evidence/risk discipline.");
        }

        if (HasSafetyClaim(declaredText) &&
            !HasModeledOpponentReply(projections) &&
            !AdmitsUnmodeledRisk(riskCheck))
        {
            warnings.Add("safety claim is unsupported: legal moves and one-ply projections do not prove safety.");
        }

        return warnings;
    }

    private static string FormatOutcome(ChessQuestCockpitTurnEnvelope envelope)
    {
        if (!envelope.AgentMoveAccepted)
        {
            return $"Outcome: move refused; reason: {Compact(envelope.ReceiptMessage)}";
        }

        if (envelope.Terminal)
        {
            return $"Outcome: move accepted; game is terminal ({envelope.TerminalResult ?? "terminal"}).";
        }

        return envelope.OpponentMoveApplied
            ? $"Outcome: move accepted; opponent replied {envelope.OpponentMove}."
            : "Outcome: move accepted; no opponent reply applied.";
    }

    private static bool MentionsCheck(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(
            text,
            @"\b(?:give|gives|giving|deliver|delivers|delivering)\s+check\b|\bcheck(?:s|ing)?\s+the\s+king\b|\bwith\s+check\b|\bis\s+check\b",
            RegexOptions.IgnoreCase);

    private static bool HasSafetyClaim(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"\b(?:safe|safety|secure|harmless|no risk)\b", RegexOptions.IgnoreCase);

    private static bool HasAdequateClaimDiscipline(
        IReadOnlyList<string> evidence,
        string? riskCheck,
        string? claimLevel)
    {
        if (evidence.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(riskCheck) &&
            !string.IsNullOrWhiteSpace(claimLevel))
        {
            return true;
        }

        return evidence.Any(item =>
            item.Contains("project", StringComparison.OrdinalIgnoreCase) ||
            item.Contains("receipt", StringComparison.OrdinalIgnoreCase) ||
            item.Contains("legal", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasModeledOpponentReply(IReadOnlyList<ChessQuestProjectedLineSummary> projections) =>
        projections.Any(projection => projection.AcceptedPrefix.Count >= 2);

    private static bool AdmitsUnmodeledRisk(string? riskCheck) =>
        !string.IsNullOrWhiteSpace(riskCheck) &&
        (riskCheck.Contains("unverified", StringComparison.OrdinalIgnoreCase) ||
         riskCheck.Contains("not modeled", StringComparison.OrdinalIgnoreCase) ||
         riskCheck.Contains("unmodeled", StringComparison.OrdinalIgnoreCase) ||
         riskCheck.Contains("not fully modeled", StringComparison.OrdinalIgnoreCase));

    private static string? Data(ExecutionEvent executionEvent, string key) =>
        executionEvent.Data.TryGetValue(key, out var value) ? value : null;
}

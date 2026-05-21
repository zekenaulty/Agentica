using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.CLI.Scenarios.ChessQuest;

public enum ChessQuestCapabilityBindingState
{
    Preferred,
    Available,
    Hidden,
    Unavailable
}

public sealed record ChessQuestHarnessContext(
    string Kind,
    string Version,
    ChessQuestActiveCapabilitySurface ActiveCapabilitySurface,
    ChessQuestContextSurfaceReceipt ContextSurfaceReceipt);

public sealed record ChessQuestActiveCapabilitySurface(
    string SurfaceId,
    string ContextSurfaceReceiptId,
    string ManifestId,
    string ManifestVersion,
    string SessionId,
    string SurfaceMode,
    ChessQuestColor AgentColor,
    ChessQuestColor OpponentColor,
    ChessQuestColor SideToMove,
    bool AgentToMove,
    string Objective,
    ChessQuestDifficulty Difficulty,
    IReadOnlyList<ChessQuestCapabilityBinding> Bindings,
    IReadOnlyDictionary<string, int> BindingCounts,
    IReadOnlyDictionary<string, object?> TurnContract,
    string SurfaceHash);

public sealed record ChessQuestCapabilityBinding(
    string CapabilityId,
    string Label,
    ChessQuestCapabilityBindingState State,
    string Reason,
    string? ToolId = null,
    int Priority = 0);

public sealed record ChessQuestContextSurfaceReceipt(
    string ReceiptId,
    string SurfaceId,
    string ManifestId,
    string ManifestVersion,
    DateTimeOffset CreatedAt,
    string SurfaceHash,
    IReadOnlyList<string> ExposedToolIds,
    IReadOnlyList<string> PreferredCapabilityIds,
    int HiddenCapabilityCount,
    IReadOnlyList<string> HiddenCapabilityCategories,
    IReadOnlyList<string> AntiLeakRules);

public sealed record ChessQuestPlanningFrame(
    string Kind,
    string Version,
    DateTimeOffset CreatedAt,
    ChessQuestSessionContext Session,
    IReadOnlyDictionary<string, object?> Board,
    IReadOnlyDictionary<string, object?> TurnContract,
    ChessQuestGoalSpine GoalSpine,
    ChessQuestContinuityCapsule ContinuityCapsule,
    ChessQuestPlayingDoctrine PlayingDoctrine,
    ChessQuestDecisionProtocol DecisionProtocol);

public sealed class ChessQuestPlanningFrameProjector : IPlanningFrameProjector
{
    private readonly ChessQuestSession _session;
    private readonly ChessQuestPhaseTracker? _phaseTracker;
    private readonly ChessQuestPhaseReport? _latestPhaseReport;

    public ChessQuestPlanningFrameProjector(
        ChessQuestSession session,
        ChessQuestPhaseTracker? phaseTracker = null,
        ChessQuestPhaseReport? latestPhaseReport = null)
    {
        _session = session;
        _phaseTracker = phaseTracker;
        _latestPhaseReport = latestPhaseReport;
    }

    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        var frame = ChessQuestCapabilitySurfaceCompiler.BuildPlanningFrame(_session, _phaseTracker, _latestPhaseReport);
        var harnessContext = ChessQuestCapabilitySurfaceCompiler.BuildHarnessContext(_session);

        return
        [
            new PlanningFrame(
                FrameId: AgenticaIds.New("frame"),
                Kind: "chessquest.cockpit",
                Version: "1.0",
                CreatedAt: DateTimeOffset.UtcNow,
                Payload: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["chessFrame"] = frame,
                    ["strategyProjection"] = _phaseTracker?.Snapshot(_session).StrategyProjection,
                    ["strategyFrame"] = _phaseTracker?.Snapshot(_session).StrategyFrame,
                    ["phaseObjective"] = _phaseTracker?.Snapshot(_session).PhaseObjective,
                    ["phaseProgress"] = _phaseTracker?.Snapshot(_session).Progress,
                    ["goalSpine"] = frame.GoalSpine,
                    ["continuityCapsule"] = frame.ContinuityCapsule,
                    ["playingDoctrine"] = frame.PlayingDoctrine,
                    ["decisionProtocol"] = frame.DecisionProtocol,
                    ["agenticHarness"] = harnessContext,
                    ["activeCapabilitySurface"] = harnessContext.ActiveCapabilitySurface,
                    ["contextSurfaceReceipt"] = harnessContext.ContextSurfaceReceipt,
                    ["promptTemplateShape"] = ChessQuestCapabilitySurfaceCompiler.PromptTemplateShape
                },
                EvidenceRefs: request.Observations
                    .Select(observation => new EvidenceRef("observation", observation.ObservationId))
                    .Concat(request.Receipts.Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId)))
                    .ToArray())
            {
                ToolSurfaceId = request.ToolSurface?.SurfaceId
            }
        ];
    }
}

public static class ChessQuestCapabilitySurfaceCompiler
{
    public const string ContextKey = "agenticHarness";

    private const string ManifestId = "chessquest.harness";
    private const string ManifestVersion = "1.0";

    public const string PromptTemplateShape =
        """
        ChessQuest StrictRefereeProjected frame instructions:
        - Treat chessFrame.session as the authoritative public role, turn, goal, and difficulty context.
        - The agent always plays chessFrame.session.agentColor and must play for the stated win condition.
        - Use coordinate UCI notation only for legal moves and submitted lines: origin square followed by destination square, plus a promotion letter only when promoting.
        - Do not use SAN, piece letters, capture markers, check symbols, or checkmate symbols in move fields.
        - chess.project_line may be used only for self-authored hypothetical lines; it never chooses moves and never generates opponent replies.
        - chess.play_move requires a concise public turnIntent matching the selected move.
        - Strict gameplay requires chess.play_move to include the current chess.list_legal_moves legalMoveObservationId. Actor probes are the only bypass surface.
        - Do not claim completion unless chess.complete_objective emits chessquest.objective_completed.
        - Use chessFrame.decisionProtocol as the current operating grammar for goals, claim discipline, evidence, and risk checks.
        - Use chessFrame.goalSpine as compact evidence-backed continuity. It preserves current reality, active priority, known divergences, and next decision pressure; it is not proof and not a move hint.
        - Use chessFrame.continuityCapsule as bounded chess-native handoff context. It may preserve strategic intent, pressures, uncertainties, confidence, and next bias; it is not proof, not raw reasoning, and not a solution line.
        - A legal move is not necessarily good or safe. A one-ply project_line result does not prove tactical safety or move quality.
        - Evidence sources have limits: legal lists prove legality, project_line proves submitted rule projection, and modeled opponent replies cover only the replies you supplied.
        - Prefer turnIntent fields goal, evidence, hypothesis, riskCheck, and claimLevel when making a move.
        - If strategyProjection, strategyFrame, and phaseObjective are present, treat them as public strategic guidance, not board truth.
        - If strategyProjection, strategyFrame, or phaseObjective conflicts with chessFrame, prefer chessFrame and legal tool receipts.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static ChessQuestCapabilitySurfaceCompiler()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static IReadOnlyDictionary<string, object?> BuildPlannerContext(
        ChessQuestSession session,
        ChessQuestPhaseTracker? phaseTracker = null,
        ChessQuestPhaseReport? latestPhaseReport = null)
    {
        var frame = BuildPlanningFrame(session, phaseTracker, latestPhaseReport);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ContextKey] = BuildHarnessContext(session),
            ["chessFrame"] = frame,
            ["playingDoctrine"] = frame.PlayingDoctrine,
            ["decisionProtocol"] = frame.DecisionProtocol,
            ["goalSpine"] = frame.GoalSpine,
            ["continuityCapsule"] = frame.ContinuityCapsule,
            ["strategyProjection"] = phaseTracker?.Snapshot(session).StrategyProjection,
            ["strategyFrame"] = phaseTracker?.Snapshot(session).StrategyFrame,
            ["phaseObjective"] = phaseTracker?.Snapshot(session).PhaseObjective,
            ["phaseProgress"] = phaseTracker?.Snapshot(session).Progress
        };
    }

    public static ChessQuestHarnessContext BuildHarnessContext(ChessQuestSession session)
    {
        var surfaceId = $"chess_surface_{Guid.NewGuid():N}"[..22];
        var receiptId = $"chess_context_surface_{Guid.NewGuid():N}"[..30];
        var bindings = BuildBindings(session).ToArray();
        var bindingCounts = bindings
            .GroupBy(binding => binding.State.ToString())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var turnContract = TurnContract(session);
        var state = session.CurrentState;
        var context = session.SessionContext;
        var surfaceHash = HashObject(new
        {
            ManifestId,
            ManifestVersion,
            session.Scenario.ScenarioId,
            context.AgentColor,
            context.OpponentColor,
            context.SideToMove,
            context.AgentToMove,
            context.SurfaceMode,
            state.Fen,
            bindings,
            turnContract
        });

        var surface = new ChessQuestActiveCapabilitySurface(
            SurfaceId: surfaceId,
            ContextSurfaceReceiptId: receiptId,
            ManifestId: ManifestId,
            ManifestVersion: ManifestVersion,
            SessionId: session.Scenario.ScenarioId,
            SurfaceMode: context.SurfaceMode,
            AgentColor: context.AgentColor,
            OpponentColor: context.OpponentColor,
            SideToMove: context.SideToMove,
            AgentToMove: context.AgentToMove,
            Objective: session.Scenario.PublicObjective,
            Difficulty: session.Scenario.Difficulty,
            Bindings: bindings,
            BindingCounts: bindingCounts,
            TurnContract: turnContract,
            SurfaceHash: surfaceHash);

        var receipt = new ChessQuestContextSurfaceReceipt(
            ReceiptId: receiptId,
            SurfaceId: surfaceId,
            ManifestId: ManifestId,
            ManifestVersion: ManifestVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            SurfaceHash: surfaceHash,
            ExposedToolIds: bindings
                .Where(binding => binding.ToolId is not null)
                .Select(binding => binding.ToolId!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PreferredCapabilityIds: bindings
                .Where(binding => binding.State == ChessQuestCapabilityBindingState.Preferred)
                .Select(binding => binding.CapabilityId)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            HiddenCapabilityCount: bindings.Count(binding => binding.State == ChessQuestCapabilityBindingState.Hidden),
            HiddenCapabilityCategories:
            [
                "non_public_oracle_data"
            ],
            AntiLeakRules: AntiLeakRulesFor(session));

        return new ChessQuestHarnessContext(
            Kind: "ChessQuestHarnessContext",
            Version: "1.0",
            ActiveCapabilitySurface: surface,
            ContextSurfaceReceipt: receipt);
    }

    public static ChessQuestPlanningFrame BuildPlanningFrame(
        ChessQuestSession session,
        ChessQuestPhaseTracker? phaseTracker = null,
        ChessQuestPhaseReport? latestPhaseReport = null)
    {
        var state = session.CurrentState;
        var decisionProtocol = ChessQuestGoalShapingPolicy.BuildDecisionProtocol(session, phaseTracker);
        var goalSpine = ChessQuestGoalSpineCompiler.Compile(session, phaseTracker, latestPhaseReport);
        var continuityCapsule = ChessQuestContinuityCapsuleCompiler.Compile(
            session,
            latestPhaseReport,
            phaseTracker?.Snapshot(session).StrategyProjection);
        return new ChessQuestPlanningFrame(
            Kind: "ChessQuestPlanningFrame",
            Version: "1.0",
            CreatedAt: DateTimeOffset.UtcNow,
            Session: session.SessionContext,
            Board: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fen"] = state.Fen,
                ["ply"] = state.Ply,
                ["sideToMoveInCheck"] = !state.IsTerminal && session.IsSideToMoveInCheck(),
                ["recentMovesUci"] = state.RecentMovesUci
            },
            TurnContract: TurnContract(session),
            GoalSpine: goalSpine,
            ContinuityCapsule: continuityCapsule,
            PlayingDoctrine: decisionProtocol.PlayingDoctrine,
            DecisionProtocol: decisionProtocol);
    }

    private static IEnumerable<ChessQuestCapabilityBinding> BuildBindings(ChessQuestSession session)
    {
        yield return new ChessQuestCapabilityBinding(
            "inspect_public_state",
            "Inspect current public chess state",
            ChessQuestCapabilityBindingState.Available,
            "Current role, turn, goal, and FEN are public.",
            ChessQuestToolIds.GetState,
            Priority: 50);
        yield return new ChessQuestCapabilityBinding(
            "render_public_board",
            "Render current board",
            ChessQuestCapabilityBindingState.Available,
            "A plain board view is public state.",
            ChessQuestToolIds.RenderBoard,
            Priority: 45);
        yield return new ChessQuestCapabilityBinding(
            "list_legal_uci_moves",
            "List legal UCI moves",
            ChessQuestCapabilityBindingState.Preferred,
            "Legal moves are public affordances; no SAN or candidate metadata is exposed.",
            ChessQuestToolIds.ListLegalMoves,
            Priority: 80);
        if (session.Scenario.DisclosurePolicy.AllowLineProjection)
        {
            yield return new ChessQuestCapabilityBinding(
                "project_agent_authored_line",
                "Project self-authored UCI line",
                ChessQuestCapabilityBindingState.Available,
                "The host may project submitted moves under public rules without selecting candidates.",
                ChessQuestToolIds.ProjectLine,
                Priority: 70);
        }
        else
        {
            yield return new ChessQuestCapabilityBinding(
                "project_agent_authored_line",
                "Project self-authored UCI line",
                ChessQuestCapabilityBindingState.Unavailable,
                "Line projection is disabled for this surface mode.");
        }

        if (session.Scenario.DisclosurePolicy.AllowAttackInspection)
        {
            yield return new ChessQuestCapabilityBinding(
                "inspect_public_attacks",
                "Inspect public attack facts",
                ChessQuestCapabilityBindingState.Available,
                "Opponent legal captures are public facts without scoring or move selection.",
                ChessQuestToolIds.InspectAttacks,
                Priority: 65);
        }
        else
        {
            yield return new ChessQuestCapabilityBinding(
                "inspect_public_attacks",
                "Inspect public attack facts",
                ChessQuestCapabilityBindingState.Hidden,
                "Attack inspection is not exposed for this surface mode.");
        }

        if (session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection)
        {
            yield return new ChessQuestCapabilityBinding(
                "inspect_agent_candidate",
                "Inspect candidate after-state attack facts",
                ChessQuestCapabilityBindingState.Available,
                "The agent may inspect neutral public opponent capture facts after its own candidate move without scoring or ranking.",
                ChessQuestToolIds.InspectCandidate,
                Priority: 68);
        }

        yield return new ChessQuestCapabilityBinding(
            "play_agent_move",
            "Play one legal agent move",
            session.SessionContext.AgentToMove ? ChessQuestCapabilityBindingState.Preferred : ChessQuestCapabilityBindingState.Unavailable,
            session.SessionContext.AgentToMove
                ? "It is the agent side's turn; play_move may commit one legal UCI move."
                : "The planner receives action frames only when the agent can move or the game is terminal.",
            ChessQuestToolIds.PlayMove,
            Priority: 90);
        yield return new ChessQuestCapabilityBinding(
            "verify_objective",
            "Verify ChessQuest objective",
            session.CurrentState.IsTerminal ? ChessQuestCapabilityBindingState.Preferred : ChessQuestCapabilityBindingState.Available,
            "Completion is artifact-gated by host board state.",
            ChessQuestToolIds.CompleteObjective,
            Priority: session.CurrentState.IsTerminal ? 95 : 30);

    }

    private static IReadOnlyDictionary<string, object?> TurnContract(ChessQuestSession session)
    {
        var allowed = new List<string>
        {
            "inspect public state",
            "render board",
            "list legal moves"
        };
        if (session.Scenario.DisclosurePolicy.AllowAttackInspection)
        {
            allowed.Add("inspect public attack facts");
        }

        if (session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection)
        {
            allowed.Add("inspect neutral consequences after an agent-authored candidate move");
        }

        if (session.Scenario.DisclosurePolicy.AllowLineProjection)
        {
            allowed.Add("project self-authored hypothetical lines");
        }

        allowed.Add("play one legal move");
        allowed.Add("check completion");

        var contract = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mustUseUci"] = true,
            ["allowedNextActions"] = allowed.ToArray(),
            ["hostCandidateConsequencesHidden"] = true,
            ["agentAuthoredLineProjectionAllowed"] = session.Scenario.DisclosurePolicy.AllowLineProjection,
            ["attackInspectionAllowed"] = session.Scenario.DisclosurePolicy.AllowAttackInspection,
            ["lineProjectionDoesNotGenerateOpponentMoves"] = true,
            ["attackInspectionDoesNotChooseMoves"] = true,
            ["legalMoveObservationRequired"] = session.Scenario.DisclosurePolicy.RequireLegalMoveObservationForPlay,
            ["nonPublicOracleDataHidden"] = true,
            ["opponentPolicyHidden"] = true,
            ["hiddenObjectiveHintsHidden"] = true,
            ["maxProjectedLinesPerTurn"] = session.Scenario.DisclosurePolicy.MaxProjectedLinesPerTurn,
            ["maxProjectedPliesPerLine"] = session.Scenario.DisclosurePolicy.MaxProjectedPliesPerLine
        };

        if (session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection)
        {
            contract["agentAuthoredCandidateInspectionAllowed"] = true;
            contract["agentAuthoredCandidateConsequencesAllowed"] = true;
            contract["maxCandidateInspectionsPerTurn"] = session.Scenario.DisclosurePolicy.EffectiveMaxCandidateInspectionsPerTurn;
            contract["candidateInspectionDoesNotScoreOrChooseMoves"] = true;
            contract["candidateInspectionDoesNotProveSafety"] = true;
        }

        return contract;
    }

    private static IReadOnlyList<string> AntiLeakRulesFor(ChessQuestSession session)
    {
        var rules = new List<string>
        {
            "Do not expose host-selected candidate moves.",
            "Do not expose non-public strategy, scoring, or host-selected candidates.",
            "Do not expose the opponent selection process.",
            "Do not expose hidden objective solution lines.",
            "Only project consequences for UCI lines explicitly supplied by the agent."
        };

        if (session.Scenario.DisclosurePolicy.EffectiveAllowCandidateInspection)
        {
            rules.Add("Only inspect consequences for UCI moves explicitly supplied by the agent.");
        }

        return rules;
    }

    private static string HashObject(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

using Agentica;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Lab.Scenarios.MazeQuest;

public sealed record MazeQuestCockpitFrame(
    string Kind,
    string Version,
    DateTimeOffset CreatedAt,
    MazeQuestCurrentStateFrame CurrentState,
    MazeQuestTrajectorySummary RecentTrajectory,
    MazeQuestProgressSignals ProgressSignals,
    MazeQuestLoopSignals LoopSignals,
    MazeQuestResourceRisk ResourceRisk,
    IReadOnlyList<MazeQuestEscapeCandidateMove> EscapeCandidateMoves,
    IReadOnlyList<MazeKnownTravelOption> KnownTravelOptions,
    string RecommendedPlannerPosture,
    IReadOnlyList<string> PlannerGuidance);

public sealed record MazeQuestCurrentStateFrame(
    string QuestId,
    string ActiveObjectiveId,
    MazePoint Position,
    int Health,
    int Energy,
    int RestCharges,
    IReadOnlyList<string> Inventory,
    IReadOnlyList<string> CompletedObjectiveIds,
    IReadOnlyList<string> RemainingObjectiveIds,
    bool CanCompleteObjective);

public sealed record MazeQuestRecentMove(
    string StepId,
    string ReceiptId,
    string Direction,
    MazePoint? From,
    MazePoint To,
    int? EnergyBefore,
    int EnergyAfter,
    int? HealthBefore,
    int HealthAfter,
    string ActiveObjectiveId,
    int FrontierGain,
    string ObjectiveDelta,
    double VisibleRisk);

public sealed record MazeQuestTrajectorySummary(
    IReadOnlyList<MazeQuestRecentMove> RecentMoveLog,
    IReadOnlyDictionary<string, int> RecentPositionCounts,
    string DetectedPattern,
    int RepeatedMoveCount,
    string? LastFrontierGainStepId,
    string? LastObjectiveProgressStepId,
    int EnergySpentSinceProgress);

public sealed record MazeQuestProgressSignals(
    int RecentFrontierGain,
    int RecentObjectiveProgressCount,
    string ObjectiveSignalTrend,
    string? LastProductiveStepId,
    string? LastProductiveToolId,
    int TurnsSinceProductiveStep,
    bool NoRecentFrontierGain,
    bool NoRecentObjectiveProgress);

public sealed record MazeQuestLoopSignals(
    bool StagnationSuspected,
    string CycleType,
    IReadOnlyList<MazePoint> CyclePositions,
    string Reason,
    bool SafeLocalMoveRepeatsCycle,
    bool NonRepeatingLegalMoveExists);

public sealed record MazeQuestResourceRisk(
    int Health,
    int Energy,
    int RestCharges,
    bool CanRest,
    bool LowEnergy,
    bool MovementEnergyCritical,
    bool BoundedRiskAllowed,
    double MaxVisibleRiskToConsider,
    string Rationale);

public sealed record MazeQuestEscapeCandidateMove(
    string Direction,
    MazePoint To,
    bool Legal,
    string Reason,
    int TerrainCost,
    double VisibleRisk,
    int FrontierGain,
    string ObjectiveDelta,
    int RecentVisitCount,
    bool WouldReturnToPreviousCell,
    bool WouldBreakLoop,
    string ProgressClass,
    bool Recommended,
    string RiskJustification);

public sealed class MazeQuestCockpitFrameProjector : IPlanningFrameProjector
{
    private readonly MazeQuestSession _session;

    public MazeQuestCockpitFrameProjector(MazeQuestSession session)
    {
        _session = session;
    }

    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        var cockpitFrame = MazeQuestCockpitFrameCompiler.BuildFrame(
            _session.Stage,
            _session.CurrentRunState,
            _session.Turns);
        var harnessContext = MazeQuestCapabilitySurfaceCompiler.BuildHarnessContext(
            _session.Stage,
            _session.CurrentRunState,
            _session.Stage.Quest.Objective,
            _session.Turns);

        return
        [
            new PlanningFrame(
                FrameId: AgenticaIds.New("frame"),
                Kind: "mazequest.cockpit",
                Version: "1.0",
                CreatedAt: DateTimeOffset.UtcNow,
                Payload: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cockpitFrame"] = cockpitFrame,
                    ["agenticHarness"] = harnessContext,
                    ["activeCapabilitySurface"] = harnessContext.ActiveCapabilitySurface,
                    ["contextSurfaceReceipt"] = harnessContext.ContextSurfaceReceipt,
                    ["promptTemplateShape"] = MazeQuestCockpitFrameCompiler.PromptTemplateShape
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

public static class MazeQuestCockpitFrameCompiler
{
    public const string PromptTemplateShape =
        """
        MazeQuest cockpit frame instructions:
        - Treat cockpitFrame as current host-projected public context for this planner turn.
        - Use recentTrajectory and loopSignals to detect repeated legal moves that do not improve frontier, objective progress, or resource position.
        - If loopSignals.stagnationSuspected is true, do not choose a move whose escapeCandidateMoves.progressClass is looping unless no other legal action exists.
        - If all no-risk moves are looping and resourceRisk.boundedRiskAllowed is true, a bounded risk_branch move may be justified.
        - ExecutionIntent.rationale must name the loop/progress/risk tradeoff when selecting an escape move.
        - Prefer maze.move_to over repeated maze.move calls when knownTravelOptions provides an exact destination and every hop is already exposed.
        - Do not use maze.move_to to skip a needed current-cell take/use interaction or to guess a hidden destination.
        - Rest only when resourceRisk says rest is available and it enables a specific non-loop next move.
        - Prefer maze.analyze_progress or maze.evaluate_escape_moves when the cockpit frame indicates stale or ambiguous trajectory evidence.
        """;

    public static MazeQuestCockpitFrame BuildFrame(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestToolTurn> turns)
    {
        var recentMoves = RecentMoves(turns, maxItems: 20);
        var trajectory = BuildTrajectorySummary(state, turns, recentMoves);
        var progress = BuildProgressSignals(turns, trajectory);
        var loop = BuildLoopSignals(stage, state, recentMoves, trajectory, progress);
        var resourceRisk = BuildResourceRisk(stage, state, loop);
        var escapeMoves = BuildEscapeCandidateMoves(stage, state, recentMoves, loop, resourceRisk);
        var knownTravelOptions = MazeQuestAnalyzer.KnownTravelOptions(stage, state);
        var guidance = BuildPlannerGuidance(loop, resourceRisk, escapeMoves, knownTravelOptions);

        return new MazeQuestCockpitFrame(
            Kind: "MazeQuestCockpitFrame",
            Version: "1.0",
            CreatedAt: DateTimeOffset.UtcNow,
            CurrentState: BuildCurrentState(stage, state),
            RecentTrajectory: trajectory,
            ProgressSignals: progress,
            LoopSignals: loop,
            ResourceRisk: resourceRisk,
            EscapeCandidateMoves: escapeMoves,
            KnownTravelOptions: knownTravelOptions,
            RecommendedPlannerPosture: RecommendedPlannerPosture(loop, resourceRisk, escapeMoves),
            PlannerGuidance: guidance);
    }

    public static IReadOnlyList<MazeQuestEscapeCandidateMove> BuildEscapeCandidateMoves(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestToolTurn> turns)
    {
        var recentMoves = RecentMoves(turns, maxItems: 20);
        var trajectory = BuildTrajectorySummary(state, turns, recentMoves);
        var progress = BuildProgressSignals(turns, trajectory);
        var loop = BuildLoopSignals(stage, state, recentMoves, trajectory, progress);
        var resourceRisk = BuildResourceRisk(stage, state, loop);
        return BuildEscapeCandidateMoves(stage, state, recentMoves, loop, resourceRisk);
    }

    private static MazeQuestCurrentStateFrame BuildCurrentState(MazeQuestStage stage, MazeQuestRunState state)
    {
        var remaining = stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => objective.Required)
            .Where(objective => !state.CompletedObjectives.Contains(objective.ObjectiveId))
            .Select(objective => objective.ObjectiveId)
            .ToArray();

        return new MazeQuestCurrentStateFrame(
            QuestId: stage.Quest.QuestId,
            ActiveObjectiveId: state.ActiveObjectiveId,
            Position: state.Position,
            Health: state.Health,
            Energy: state.Energy,
            RestCharges: state.RestCharges,
            Inventory: state.Inventory.Order(StringComparer.Ordinal).ToArray(),
            CompletedObjectiveIds: state.CompletedObjectives.Order(StringComparer.Ordinal).ToArray(),
            RemainingObjectiveIds: remaining,
            CanCompleteObjective: remaining.Length == 0);
    }

    private static IReadOnlyList<MazeQuestRecentMove> RecentMoves(
        IReadOnlyList<MazeQuestToolTurn> turns,
        int maxItems) =>
        turns
            .Where(turn =>
                (string.Equals(turn.Invocation.ToolId, MazeQuestToolIds.Move, StringComparison.Ordinal) ||
                 string.Equals(turn.Invocation.ToolId, MazeQuestToolIds.MoveTo, StringComparison.Ordinal)) &&
                turn.Result.Receipt.Status == Agentica.Artifacts.ReceiptStatus.Succeeded)
            .TakeLast(maxItems)
            .SelectMany(CreateRecentMoves)
            .TakeLast(maxItems)
            .ToArray();

    private static IReadOnlyList<MazeQuestRecentMove> CreateRecentMoves(MazeQuestToolTurn turn)
    {
        if (string.Equals(turn.Invocation.ToolId, MazeQuestToolIds.MoveTo, StringComparison.Ordinal) &&
            turn.Result.Receipt.Data.TryGetValue("hops", out var value) &&
            value is IEnumerable<MazeKnownTravelHop> hops)
        {
            return hops
                .Select(hop => new MazeQuestRecentMove(
                    StepId: $"{turn.Invocation.StepId}.{hop.Index:00}",
                    ReceiptId: turn.Result.Receipt.ReceiptId,
                    Direction: hop.Direction,
                    From: hop.From,
                    To: hop.To,
                    EnergyBefore: hop.EnergyBefore,
                    EnergyAfter: hop.EnergyAfter,
                    HealthBefore: hop.HealthBefore,
                    HealthAfter: hop.HealthAfter,
                    ActiveObjectiveId: hop.ActiveObjectiveIdAfter,
                    FrontierGain: hop.FrontierGain,
                    ObjectiveDelta: hop.ObjectiveDelta,
                    VisibleRisk: hop.VisibleRisk))
                .ToArray();
        }

        return [CreateRecentMove(turn)];
    }

    private static MazeQuestRecentMove CreateRecentMove(MazeQuestToolTurn turn)
    {
        var data = turn.Result.Receipt.Data;
        return new MazeQuestRecentMove(
            StepId: turn.Invocation.StepId,
            ReceiptId: turn.Result.Receipt.ReceiptId,
            Direction: ReadString(data, "direction") ?? "unknown",
            From: turn.BeforeRunState?.Position,
            To: turn.RunState.Position,
            EnergyBefore: turn.BeforeRunState?.Energy,
            EnergyAfter: turn.RunState.Energy,
            HealthBefore: turn.BeforeRunState?.Health,
            HealthAfter: turn.RunState.Health,
            ActiveObjectiveId: turn.RunState.ActiveObjectiveId,
            FrontierGain: ReadInt(data, "frontierGain") ?? 0,
            ObjectiveDelta: ReadString(data, "objectiveDelta") ?? "unknown",
            VisibleRisk: ReadDouble(data, "visibleRisk") ?? 0);
    }

    private static MazeQuestTrajectorySummary BuildTrajectorySummary(
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestToolTurn> turns,
        IReadOnlyList<MazeQuestRecentMove> recentMoves)
    {
        var positionCounts = recentMoves
            .GroupBy(move => PointKey(move.To), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var detectedPattern = DetectPattern(recentMoves);
        var repeatedMoveCount = CountRepeatedTail(recentMoves, detectedPattern);
        var lastFrontierGain = recentMoves.LastOrDefault(move => move.FrontierGain > 0)?.StepId;
        var lastObjectiveProgress = turns
            .Where(IsObjectiveProgressTurn)
            .Select(turn => turn.Invocation.StepId)
            .LastOrDefault();
        var lastProductive = LastProductiveTurn(turns);
        var energySpent = lastProductive is null
            ? 0
            : Math.Max(0, lastProductive.RunState.Energy - state.Energy);

        return new MazeQuestTrajectorySummary(
            RecentMoveLog: recentMoves,
            RecentPositionCounts: positionCounts,
            DetectedPattern: detectedPattern,
            RepeatedMoveCount: repeatedMoveCount,
            LastFrontierGainStepId: lastFrontierGain,
            LastObjectiveProgressStepId: lastObjectiveProgress,
            EnergySpentSinceProgress: energySpent);
    }

    private static MazeQuestProgressSignals BuildProgressSignals(
        IReadOnlyList<MazeQuestToolTurn> turns,
        MazeQuestTrajectorySummary trajectory)
    {
        var recentTurns = turns.TakeLast(12).ToArray();
        var recentFrontierGain = trajectory.RecentMoveLog.TakeLast(12).Sum(move => move.FrontierGain);
        var recentObjectiveProgressCount = recentTurns.Count(IsObjectiveProgressTurn);
        var lastProductive = LastProductiveTurn(turns);
        var turnsSinceProductive = lastProductive is null
            ? turns.Count
            : turns.Count - turns.ToList().LastIndexOf(lastProductive) - 1;

        return new MazeQuestProgressSignals(
            RecentFrontierGain: recentFrontierGain,
            RecentObjectiveProgressCount: recentObjectiveProgressCount,
            ObjectiveSignalTrend: ObjectiveSignalTrend(recentTurns),
            LastProductiveStepId: lastProductive?.Invocation.StepId,
            LastProductiveToolId: lastProductive?.Invocation.ToolId,
            TurnsSinceProductiveStep: Math.Max(0, turnsSinceProductive),
            NoRecentFrontierGain: recentFrontierGain <= 0,
            NoRecentObjectiveProgress: recentObjectiveProgressCount == 0);
    }

    private static MazeQuestLoopSignals BuildLoopSignals(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestRecentMove> recentMoves,
        MazeQuestTrajectorySummary trajectory,
        MazeQuestProgressSignals progress)
    {
        var cyclePositions = CyclePositions(recentMoves, trajectory.DetectedPattern);
        var repeatedTail = trajectory.RepeatedMoveCount <= 0
            ? Array.Empty<MazeQuestRecentMove>()
            : recentMoves.TakeLast(trajectory.RepeatedMoveCount).ToArray();
        var repeatedTailHasNoFrontierGain = repeatedTail.Length > 0 &&
            repeatedTail.All(move => move.FrontierGain <= 0);
        var repeatedTailHasNoObjectiveProgress = progress.TurnsSinceProductiveStep >=
            Math.Min(trajectory.RepeatedMoveCount, 4);
        var stagnation = trajectory.DetectedPattern != "none" &&
            trajectory.RepeatedMoveCount >= 4 &&
            repeatedTailHasNoFrontierGain &&
            (progress.NoRecentObjectiveProgress || repeatedTailHasNoObjectiveProgress);
        var legalMoves = MazeQuestAnalyzer.EvaluateMoves(stage, state)
            .Where(move => move.Legal)
            .ToArray();
        var cyclePositionSet = cyclePositions.ToHashSet();
        var safeLocalMoveRepeatsCycle = legalMoves.Any(move =>
            move.VisibleRisk <= 0 &&
            cyclePositionSet.Contains(move.To));
        var nonRepeatingLegalMoveExists = legalMoves.Any(move => !cyclePositionSet.Contains(move.To));

        return new MazeQuestLoopSignals(
            StagnationSuspected: stagnation,
            CycleType: stagnation ? trajectory.DetectedPattern : "none",
            CyclePositions: cyclePositions,
            Reason: stagnation
                ? "Recent movement repeatedly revisits the same public cells without frontier gain or objective progress."
                : "Recent movement does not currently meet the stagnation threshold.",
            SafeLocalMoveRepeatsCycle: safeLocalMoveRepeatsCycle,
            NonRepeatingLegalMoveExists: nonRepeatingLegalMoveExists);
    }

    private static MazeQuestResourceRisk BuildResourceRisk(
        MazeQuestStage stage,
        MazeQuestRunState state,
        MazeQuestLoopSignals loop)
    {
        var canRest = state.RestCharges > 0 &&
            (state.Energy < stage.EnergyPolicy.MaxEnergy || state.Health < 8);
        var maxRisk = state.Health >= 5 ? 0.5 : state.Health >= 3 ? 0.35 : 0.15;
        var boundedRiskAllowed = loop.StagnationSuspected && state.Health > 1;

        return new MazeQuestResourceRisk(
            Health: state.Health,
            Energy: state.Energy,
            RestCharges: state.RestCharges,
            CanRest: canRest,
            LowEnergy: state.Energy <= Math.Max(2, stage.EnergyPolicy.Padding / 2),
            MovementEnergyCritical: state.Energy <= 1,
            BoundedRiskAllowed: boundedRiskAllowed,
            MaxVisibleRiskToConsider: maxRisk,
            Rationale: boundedRiskAllowed
                ? "A bounded visible risk can be considered because recent no-risk movement appears stagnant."
                : "Prefer low-risk moves unless public progress signals show stagnation.");
    }

    private static IReadOnlyList<MazeQuestEscapeCandidateMove> BuildEscapeCandidateMoves(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestRecentMove> recentMoves,
        MazeQuestLoopSignals loop,
        MazeQuestResourceRisk resourceRisk)
    {
        var recentPositionCounts = recentMoves
            .GroupBy(move => move.To)
            .ToDictionary(group => group.Key, group => group.Count());
        var previousPosition = recentMoves.LastOrDefault()?.From;
        var cyclePositions = loop.CyclePositions.ToHashSet();

        return MazeQuestAnalyzer.EvaluateMoves(stage, state)
            .Select(move =>
            {
                var recentVisitCount = recentPositionCounts.GetValueOrDefault(move.To);
                var wouldReturn = previousPosition is not null && move.To == previousPosition.Value;
                var wouldBreakLoop = loop.StagnationSuspected && !cyclePositions.Contains(move.To);
                var progressClass = ProgressClass(move, loop, recentVisitCount, wouldReturn, wouldBreakLoop);
                var recommended = move.Legal &&
                    (progressClass == "productive" ||
                     (wouldBreakLoop && move.VisibleRisk <= 0) ||
                     (progressClass == "risk_branch" &&
                      resourceRisk.BoundedRiskAllowed &&
                      move.VisibleRisk <= resourceRisk.MaxVisibleRiskToConsider));

                return new MazeQuestEscapeCandidateMove(
                    Direction: move.Direction,
                    To: move.To,
                    Legal: move.Legal,
                    Reason: move.Reason,
                    TerrainCost: move.TerrainCost,
                    VisibleRisk: move.VisibleRisk,
                    FrontierGain: move.FrontierGain,
                    ObjectiveDelta: move.ObjectiveDelta,
                    RecentVisitCount: recentVisitCount,
                    WouldReturnToPreviousCell: wouldReturn,
                    WouldBreakLoop: wouldBreakLoop,
                    ProgressClass: progressClass,
                    Recommended: recommended,
                    RiskJustification: RiskJustification(move, progressClass, resourceRisk));
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildPlannerGuidance(
        MazeQuestLoopSignals loop,
        MazeQuestResourceRisk resourceRisk,
        IReadOnlyList<MazeQuestEscapeCandidateMove> escapeMoves,
        IReadOnlyList<MazeKnownTravelOption> knownTravelOptions)
    {
        var guidance = new List<string>();
        if (knownTravelOptions.Count > 0)
        {
            guidance.Add("Use maze.move_to to compress movement through fully exposed public routes when the destination is copied from knownTravelOptions and no immediate interaction is being skipped.");
        }

        if (loop.StagnationSuspected)
        {
            guidance.Add("Recent public trajectory indicates stagnation; choose a legal action that breaks the repeated pattern.");
            guidance.Add("Do not choose a safe local move that only returns to the repeated cell pair unless no other legal action exists.");
        }

        if (resourceRisk.BoundedRiskAllowed &&
            escapeMoves.Any(move => move.ProgressClass == "risk_branch" && move.Recommended))
        {
            guidance.Add("A bounded visible-risk branch is acceptable when all no-risk choices are looped and current health can absorb the risk.");
        }

        if (resourceRisk.CanRest)
        {
            guidance.Add("Rest only if it enables a specific non-loop movement or interaction on the next turn.");
        }

        return guidance.Count == 0
            ? ["Use public move evaluation, objective signal, and current legal actions to choose the next bounded step."]
            : guidance;
    }

    private static string RecommendedPlannerPosture(
        MazeQuestLoopSignals loop,
        MazeQuestResourceRisk resourceRisk,
        IReadOnlyList<MazeQuestEscapeCandidateMove> escapeMoves)
    {
        if (loop.StagnationSuspected &&
            escapeMoves.Any(move => move.Recommended && move.ProgressClass == "risk_branch"))
        {
            return "break_loop_with_bounded_risk";
        }

        if (loop.StagnationSuspected)
        {
            return "break_loop";
        }

        if (resourceRisk.LowEnergy)
        {
            return "resource_cautious";
        }

        return "normal";
    }

    private static string ProgressClass(
        MazeMoveEvaluation move,
        MazeQuestLoopSignals loop,
        int recentVisitCount,
        bool wouldReturn,
        bool wouldBreakLoop)
    {
        if (!move.Legal)
        {
            return "blocked";
        }

        if (move.FrontierGain > 0 ||
            move.ObjectiveDelta.Contains("objective", StringComparison.OrdinalIgnoreCase) ||
            move.ObjectiveDelta.Contains("warmer", StringComparison.OrdinalIgnoreCase))
        {
            return "productive";
        }

        if (loop.StagnationSuspected && !wouldBreakLoop && (recentVisitCount > 0 || wouldReturn))
        {
            return "looping";
        }

        if (move.VisibleRisk > 0)
        {
            return "risk_branch";
        }

        return "neutral";
    }

    private static string RiskJustification(
        MazeMoveEvaluation move,
        string progressClass,
        MazeQuestResourceRisk resourceRisk)
    {
        if (!move.Legal)
        {
            return $"Blocked: {move.Reason}.";
        }

        if (progressClass == "risk_branch" && resourceRisk.BoundedRiskAllowed)
        {
            return $"Visible risk {move.VisibleRisk:0.##} may be justified to break stagnation; current health is {resourceRisk.Health}.";
        }

        if (move.VisibleRisk > 0)
        {
            return $"Visible risk {move.VisibleRisk:0.##}; prefer only when safer moves are stagnant or blocked.";
        }

        return "No visible risk.";
    }

    private static string DetectPattern(IReadOnlyList<MazeQuestRecentMove> recentMoves)
    {
        var positions = recentMoves.Select(move => move.To).ToArray();
        if (positions.Length >= 4)
        {
            var a = positions[^1];
            var b = positions[^2];
            if (a != b && positions[^3] == a && positions[^4] == b)
            {
                return "two_cell_cycle";
            }
        }

        var tail = positions.TakeLast(Math.Min(8, positions.Length)).ToArray();
        return tail.Length >= 6 && tail.Distinct().Count() <= 3
            ? "local_area_cycle"
            : "none";
    }

    private static int CountRepeatedTail(
        IReadOnlyList<MazeQuestRecentMove> recentMoves,
        string detectedPattern)
    {
        if (detectedPattern == "none" || recentMoves.Count == 0)
        {
            return 0;
        }

        var cycle = CyclePositions(recentMoves, detectedPattern).ToHashSet();
        var count = 0;
        for (var index = recentMoves.Count - 1; index >= 0; index--)
        {
            if (!cycle.Contains(recentMoves[index].To))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static IReadOnlyList<MazePoint> CyclePositions(
        IReadOnlyList<MazeQuestRecentMove> recentMoves,
        string detectedPattern)
    {
        if (detectedPattern == "none")
        {
            return [];
        }

        var take = detectedPattern == "two_cell_cycle" ? 4 : 8;
        return recentMoves
            .TakeLast(Math.Min(take, recentMoves.Count))
            .Select(move => move.To)
            .Distinct()
            .ToArray();
    }

    private static bool IsObjectiveProgressTurn(MazeQuestToolTurn turn)
    {
        if (turn.BeforeRunState is null)
        {
            return false;
        }

        return turn.RunState.CompletedObjectives.Count > turn.BeforeRunState.CompletedObjectives.Count ||
            turn.RunState.Inventory.Count > turn.BeforeRunState.Inventory.Count;
    }

    private static MazeQuestToolTurn? LastProductiveTurn(IReadOnlyList<MazeQuestToolTurn> turns) =>
        turns.LastOrDefault(turn =>
            IsObjectiveProgressTurn(turn) ||
            ReadInt(turn.Result.Receipt.Data, "frontierGain") > 0 ||
            ReadInt(turn.Result.Receipt.Data, "newCellsDiscovered") > 0);

    private static string ObjectiveSignalTrend(IReadOnlyList<MazeQuestToolTurn> turns)
    {
        var signals = turns
            .Select(turn => TryReadSignal(turn.Result.Receipt.Data))
            .Where(signal => signal is not null)
            .Select(signal => signal!)
            .TakeLast(4)
            .ToArray();

        if (signals.Length < 2)
        {
            return "unknown";
        }

        var first = signals[0].Warmth;
        var last = signals[^1].Warmth;
        if (last > first + 0.05)
        {
            return "warming";
        }

        if (last < first - 0.05)
        {
            return "cooling";
        }

        return "flat";
    }

    private static MazeObjectiveSignal? TryReadSignal(IReadOnlyDictionary<string, object?> data) =>
        data.TryGetValue("objectiveSignal", out var value) && value is MazeObjectiveSignal signal
            ? signal
            : null;

    public static string PointKey(MazePoint point) => $"{point.X},{point.Y}";

    private static string? ReadString(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            _ => int.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            decimal decimalValue => (double)decimalValue,
            int intValue => intValue,
            _ => double.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }
}

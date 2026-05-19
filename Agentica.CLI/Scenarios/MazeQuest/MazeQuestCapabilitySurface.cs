using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.CLI.Scenarios.MazeQuest;

public enum MazeQuestCapabilityBindingState
{
    Preferred,
    Available,
    Demoted,
    Blocked,
    Denied,
    Hidden,
    Unavailable
}

public sealed record MazeQuestHarnessManifest(
    string ManifestId,
    string Version,
    string DomainId,
    string PlannerOutputLevel,
    IReadOnlyList<string> CapabilityFamilies,
    IReadOnlyList<string> ReceiptKinds,
    IReadOnlyList<string> ArtifactKinds,
    IReadOnlyList<string> AntiLeakRules);

public sealed record MazeQuestHarnessContext(
    string Kind,
    string Version,
    MazeQuestActiveCapabilitySurface ActiveCapabilitySurface,
    MazeQuestContextSurfaceReceipt ContextSurfaceReceipt);

public sealed record MazeQuestActiveCapabilitySurface(
    string SurfaceId,
    string ContextSurfaceReceiptId,
    string ManifestId,
    string ManifestVersion,
    string QuestId,
    string QuestType,
    string Objective,
    string PlannerOutputLevel,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, object?> PublicStateSummary,
    IReadOnlyList<MazeQuestCapabilityBinding> Bindings,
    IReadOnlyDictionary<string, int> BindingCounts,
    string SurfaceHash);

public sealed record MazeQuestCapabilityBinding(
    string CapabilityId,
    string Label,
    MazeQuestCapabilityBindingState State,
    string Reason,
    string? ToolId = null,
    IReadOnlyDictionary<string, object?>? Input = null,
    int Priority = 0,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record MazeQuestContextSurfaceReceipt(
    string ReceiptId,
    string SurfaceId,
    string ManifestId,
    string ManifestVersion,
    DateTimeOffset CreatedAt,
    string SurfaceHash,
    string StateProjectionHash,
    IReadOnlyList<string> ExposedToolIds,
    IReadOnlyList<string> PreferredCapabilityIds,
    IReadOnlyList<string> BlockedCapabilityIds,
    IReadOnlyList<string> DemotedCapabilityIds,
    IReadOnlyList<string> DeniedCapabilityIds,
    int HiddenCapabilityCount,
    IReadOnlyList<string> HiddenCapabilityCategories,
    string PlannerOutputLevel,
    IReadOnlyList<string> AntiLeakRules);

public static class MazeQuestCapabilitySurfaceCompiler
{
    public const string ContextKey = "agenticHarness";

    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly MazeQuestHarnessManifest Manifest = new(
        ManifestId: "mazequest.harness",
        Version: "1.0",
        DomainId: "mazequest",
        PlannerOutputLevel: "ToolLevelPlan",
        CapabilityFamilies:
        [
            "public_state_query",
            "local_sensor",
            "local_movement",
            "known_route_movement",
            "progress_analysis",
            "loop_escape",
            "visible_object_interaction",
            "resource_recovery",
            "completion_gate"
        ],
        ReceiptKinds:
        [
            "maze.get_state",
            "maze.render_map",
            "maze.scan",
            "maze.sense_objective",
            "maze.evaluate_moves",
            "maze.analyze_progress",
            "maze.evaluate_escape_moves",
            "maze.move",
            "maze.move_to",
            "maze.take",
            "maze.use",
            "maze.rest",
            "maze.complete_objective"
        ],
        ArtifactKinds:
        [
            "mazequest.objective_completed"
        ],
        AntiLeakRules:
        [
            "Do not expose unrevealed cell coordinates.",
            "Do not expose the full hidden route.",
            "Do not expose hidden object placement coordinates.",
            "Do not expose future receipts or completion artifacts.",
            "Only expose known-route movement for routes whose destination and every hop are already discovered.",
            "Use public sensors, legal actions, and receipt-backed observations as planner evidence."
        ]);

    static MazeQuestCapabilitySurfaceCompiler()
    {
        HashJsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static IReadOnlyDictionary<string, object?> BuildPlannerContext(
        MazeQuestStage stage,
        MazeQuestRunState state,
        string objective,
        IReadOnlyList<MazeQuestToolTurn>? turns = null)
    {
        var context = new Dictionary<string, object?>(
            MazeQuestAnalyzer.BuildPublicSnapshot(stage, state),
            StringComparer.Ordinal)
        {
            [ContextKey] = BuildHarnessContext(stage, state, objective, turns)
        };

        return context;
    }

    public static MazeQuestHarnessContext BuildHarnessContext(
        MazeQuestStage stage,
        MazeQuestRunState state,
        string objective,
        IReadOnlyList<MazeQuestToolTurn>? turns = null)
    {
        var compiled = Compile(stage, state, objective, turns ?? []);
        return new MazeQuestHarnessContext(
            Kind: "MazeQuestHarnessContext",
            Version: "1.0",
            ActiveCapabilitySurface: compiled.Surface,
            ContextSurfaceReceipt: compiled.Receipt);
    }

    private static CompiledSurface Compile(
        MazeQuestStage stage,
        MazeQuestRunState state,
        string objective,
        IReadOnlyList<MazeQuestToolTurn> turns)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var surfaceId = $"maze_surface_{Guid.NewGuid():N}"[..21];
        var receiptId = $"maze_context_surface_{Guid.NewGuid():N}"[..29];
        var publicStateSummary = PublicStateSummary(stage, state, turns);
        var bindings = BuildBindings(stage, state, turns).ToArray();
        var bindingCounts = bindings
            .GroupBy(binding => binding.State.ToString())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var stateProjectionHash = HashObject(publicStateSummary);
        var surfaceHash = HashObject(new
        {
            surfaceId,
            Manifest.ManifestId,
            Manifest.Version,
            stage.Quest.QuestId,
            stage.Quest.QuestType,
            objective,
            publicStateSummary,
            bindings
        });

        var surface = new MazeQuestActiveCapabilitySurface(
            SurfaceId: surfaceId,
            ContextSurfaceReceiptId: receiptId,
            ManifestId: Manifest.ManifestId,
            ManifestVersion: Manifest.Version,
            QuestId: stage.Quest.QuestId,
            QuestType: stage.Quest.QuestType.ToString(),
            Objective: objective,
            PlannerOutputLevel: Manifest.PlannerOutputLevel,
            CreatedAt: createdAt,
            PublicStateSummary: publicStateSummary,
            Bindings: bindings,
            BindingCounts: bindingCounts,
            SurfaceHash: surfaceHash);

        var receipt = new MazeQuestContextSurfaceReceipt(
            ReceiptId: receiptId,
            SurfaceId: surfaceId,
            ManifestId: Manifest.ManifestId,
            ManifestVersion: Manifest.Version,
            CreatedAt: createdAt,
            SurfaceHash: surfaceHash,
            StateProjectionHash: stateProjectionHash,
            ExposedToolIds: bindings
                .Where(binding => binding.ToolId is not null)
                .Select(binding => binding.ToolId!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PreferredCapabilityIds: CapabilityIds(bindings, MazeQuestCapabilityBindingState.Preferred),
            BlockedCapabilityIds: CapabilityIds(bindings, MazeQuestCapabilityBindingState.Blocked),
            DemotedCapabilityIds: CapabilityIds(bindings, MazeQuestCapabilityBindingState.Demoted),
            DeniedCapabilityIds: CapabilityIds(bindings, MazeQuestCapabilityBindingState.Denied),
            HiddenCapabilityCount: bindings.Count(binding => binding.State == MazeQuestCapabilityBindingState.Hidden),
            HiddenCapabilityCategories:
            [
                "fog_of_war",
                "route_oracle",
                "future_receipts"
            ],
            PlannerOutputLevel: Manifest.PlannerOutputLevel,
            AntiLeakRules: Manifest.AntiLeakRules);

        return new CompiledSurface(surface, receipt);
    }

    private static IReadOnlyList<string> CapabilityIds(
        IReadOnlyList<MazeQuestCapabilityBinding> bindings,
        MazeQuestCapabilityBindingState state) =>
        bindings
            .Where(binding => binding.State == state)
            .Select(binding => binding.CapabilityId)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyDictionary<string, object?> PublicStateSummary(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestToolTurn> turns)
    {
        var remainingRequired = stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => objective.Required)
            .Where(objective => !state.CompletedObjectives.Contains(objective.ObjectiveId))
            .Select(objective => objective.ObjectiveId)
            .ToArray();
        var moveEvaluations = MazeQuestAnalyzer.EvaluateMoves(stage, state);
        var cockpitFrame = MazeQuestCockpitFrameCompiler.BuildFrame(stage, state, turns);
        var knownTravelOptions = MazeQuestAnalyzer.KnownTravelOptions(stage, state);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["questId"] = stage.Quest.QuestId,
            ["questType"] = stage.Quest.QuestType.ToString(),
            ["position"] = Point(state.Position),
            ["health"] = state.Health,
            ["energy"] = state.Energy,
            ["restCharges"] = state.RestCharges,
            ["inventory"] = state.Inventory.Order(StringComparer.Ordinal).ToArray(),
            ["activeObjectiveId"] = state.ActiveObjectiveId,
            ["completedObjectiveIds"] = state.CompletedObjectives.Order(StringComparer.Ordinal).ToArray(),
            ["remainingRequiredObjectiveIds"] = remainingRequired,
            ["visibleCellCount"] = state.Discovered.Count,
            ["legalMoveDirections"] = moveEvaluations
                .Where(move => move.Legal)
                .Select(move => move.Direction)
                .ToArray(),
            ["knownTravelOptionCount"] = knownTravelOptions.Count,
            ["knownTravelOptions"] = knownTravelOptions
                .Take(8)
                .Select(option => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["destination"] = Point(option.Destination),
                    ["hopCount"] = option.HopCount,
                    ["directions"] = option.Directions,
                    ["totalTerrainCost"] = option.TotalTerrainCost,
                    ["frontierGain"] = option.FrontierGain,
                    ["maxVisibleRisk"] = option.MaxVisibleRisk,
                    ["objectiveDelta"] = option.ObjectiveDelta,
                    ["summary"] = option.Summary
                })
                .ToArray(),
            ["blockedMoveDirections"] = moveEvaluations
                .Where(move => !move.Legal)
                .Select(move => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["direction"] = move.Direction,
                    ["reason"] = move.Reason
                })
                .ToArray(),
            ["canCompleteObjective"] = remainingRequired.Length == 0,
            ["trajectorySummary"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["recentMoveCount"] = cockpitFrame.RecentTrajectory.RecentMoveLog.Count,
                ["recentPath"] = cockpitFrame.RecentTrajectory.RecentMoveLog
                    .Select(move => MazeQuestCockpitFrameCompiler.PointKey(move.To))
                    .ToArray(),
                ["detectedPattern"] = cockpitFrame.RecentTrajectory.DetectedPattern,
                ["repeatedMoveCount"] = cockpitFrame.RecentTrajectory.RepeatedMoveCount,
                ["lastFrontierGainStepId"] = cockpitFrame.RecentTrajectory.LastFrontierGainStepId,
                ["lastObjectiveProgressStepId"] = cockpitFrame.RecentTrajectory.LastObjectiveProgressStepId,
                ["energySpentSinceProgress"] = cockpitFrame.RecentTrajectory.EnergySpentSinceProgress
            },
            ["loopSignals"] = cockpitFrame.LoopSignals,
            ["resourceRisk"] = cockpitFrame.ResourceRisk,
            ["recommendedPlannerPosture"] = cockpitFrame.RecommendedPlannerPosture
        };
    }

    private static IEnumerable<MazeQuestCapabilityBinding> BuildBindings(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazeQuestToolTurn> turns)
    {
        var cockpitFrame = MazeQuestCockpitFrameCompiler.BuildFrame(stage, state, turns);
        var escapeMovesByDirection = cockpitFrame.EscapeCandidateMoves
            .ToDictionary(move => move.Direction, StringComparer.Ordinal);

        yield return Binding(
            "maze.get_state",
            "Read public MazeQuest state",
            MazeQuestCapabilityBindingState.Preferred,
            "The planner needs public state, legal actions, objective progress, and receipt-backed context before choosing work.",
            MazeQuestToolIds.GetState,
            priority: 80);

        yield return Binding(
            "maze.render_map",
            "Render visible fog-of-war map",
            MazeQuestCapabilityBindingState.Available,
            "The visible map can help local navigation, but it only shows discovered cells.",
            MazeQuestToolIds.RenderMap,
            priority: 55);

        var visibleRefreshCount = MazeVisibility
            .VisiblePoints(stage.Grid, state.Position, stage.VisibilityRadius)
            .Count(point => !state.Discovered.Contains(point));
        yield return Binding(
            "maze.scan",
            "Refresh local visible cells",
            visibleRefreshCount > 0
                ? MazeQuestCapabilityBindingState.Preferred
                : MazeQuestCapabilityBindingState.Demoted,
            visibleRefreshCount > 0
                ? "A scan can reveal additional public local cells around the current position."
                : "The current visibility radius is already discovered; prefer state or move evaluation unless context is stale.",
            MazeQuestToolIds.Scan,
            priority: visibleRefreshCount > 0 ? 75 : 25,
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["newVisibleCellEstimate"] = visibleRefreshCount
            });

        yield return Binding(
            "maze.sense_objective",
            "Sense active objective direction",
            MazeQuestCapabilityBindingState.Preferred,
            "The public objective sensor gives coarse bearing and distance without revealing hidden route coordinates.",
            MazeQuestToolIds.SenseObjective,
            priority: 70);

        yield return Binding(
            "maze.evaluate_moves",
            "Evaluate local move options",
            MazeQuestCapabilityBindingState.Preferred,
            "The planner should use local move legality, cost, risk, and frontier facts before moving.",
            MazeQuestToolIds.EvaluateMoves,
            priority: 85);

        yield return Binding(
            "maze.analyze_progress",
            "Analyze recent public progress and loop signals",
            cockpitFrame.LoopSignals.StagnationSuspected
                ? MazeQuestCapabilityBindingState.Preferred
                : MazeQuestCapabilityBindingState.Available,
            cockpitFrame.LoopSignals.StagnationSuspected
                ? "Recent public trajectory suggests stagnation; analyze progress before choosing another local move."
                : "The host can summarize recent public trajectory, objective trend, repeated cells, and resource burn when movement evidence is ambiguous.",
            MazeQuestToolIds.AnalyzeProgress,
            priority: cockpitFrame.LoopSignals.StagnationSuspected ? 90 : 60,
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stagnationSuspected"] = cockpitFrame.LoopSignals.StagnationSuspected,
                ["detectedPattern"] = cockpitFrame.RecentTrajectory.DetectedPattern,
                ["repeatedMoveCount"] = cockpitFrame.RecentTrajectory.RepeatedMoveCount,
                ["turnsSinceProductiveStep"] = cockpitFrame.ProgressSignals.TurnsSinceProductiveStep
            });

        yield return Binding(
            "maze.evaluate_escape_moves",
            "Evaluate non-oracle loop escape moves",
            cockpitFrame.LoopSignals.StagnationSuspected
                ? MazeQuestCapabilityBindingState.Preferred
                : MazeQuestCapabilityBindingState.Available,
            cockpitFrame.LoopSignals.StagnationSuspected
                ? "A loop may be present; compare legal moves by revisit count, loop break, visible risk, and resource impact."
                : "The host can classify current legal moves by public loop and bounded-risk signals without revealing a route.",
            MazeQuestToolIds.EvaluateEscapeMoves,
            priority: cockpitFrame.LoopSignals.StagnationSuspected ? 92 : 58,
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["recommendedPlannerPosture"] = cockpitFrame.RecommendedPlannerPosture,
                ["boundedRiskAllowed"] = cockpitFrame.ResourceRisk.BoundedRiskAllowed,
                ["recommendedDirections"] = cockpitFrame.EscapeCandidateMoves
                    .Where(move => move.Recommended)
                    .Select(move => move.Direction)
                    .ToArray()
            });

        foreach (var move in MazeQuestAnalyzer.EvaluateMoves(stage, state))
        {
            var escapeMove = escapeMovesByDirection.GetValueOrDefault(move.Direction);
            var stateForMove = move.Legal
                ? escapeMove?.ProgressClass == "looping"
                    ? MazeQuestCapabilityBindingState.Demoted
                    : MazeQuestCapabilityBindingState.Available
                : MazeQuestCapabilityBindingState.Blocked;
            var priority = move.Legal
                ? 50 + Math.Min(20, move.FrontierGain) + (move.ObjectiveDelta.Contains("objective", StringComparison.Ordinal) ? 20 : 0)
                : 0;
            if (escapeMove?.Recommended == true)
            {
                priority += 18;
            }
            else if (escapeMove?.ProgressClass == "looping")
            {
                priority = Math.Min(priority, 20);
            }

            yield return Binding(
                $"maze.move.{move.Direction}",
                $"Move {move.Direction}",
                stateForMove,
                move.Legal
                    ? escapeMove?.ProgressClass == "looping"
                        ? $"{move.Summary} This move is demoted because recent public trajectory suggests it repeats the loop."
                        : move.Summary
                    : $"Cannot move {move.Direction}: {move.Reason}.",
                MazeQuestToolIds.Move,
                input: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["direction"] = move.Direction
                },
                priority: priority,
                metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["terrainCost"] = move.TerrainCost,
                    ["visibleRisk"] = move.VisibleRisk,
                    ["objectiveDelta"] = move.ObjectiveDelta,
                    ["frontierGain"] = move.FrontierGain,
                    ["recentVisitCount"] = escapeMove?.RecentVisitCount ?? 0,
                    ["wouldReturnToPreviousCell"] = escapeMove?.WouldReturnToPreviousCell ?? false,
                    ["wouldBreakLoop"] = escapeMove?.WouldBreakLoop ?? false,
                    ["progressClass"] = escapeMove?.ProgressClass ?? "unknown",
                    ["riskJustification"] = escapeMove?.RiskJustification
                });
        }

        var knownTravelOptions = MazeQuestAnalyzer.KnownTravelOptions(stage, state);
        foreach (var option in knownTravelOptions.Take(8))
        {
            var stateForOption = option.ObjectiveDelta.Contains("active_objective", StringComparison.Ordinal)
                ? MazeQuestCapabilityBindingState.Preferred
                : option.FrontierGain > 0
                    ? MazeQuestCapabilityBindingState.Available
                    : MazeQuestCapabilityBindingState.Demoted;

            yield return Binding(
                $"maze.move_to.{option.Destination.X}_{option.Destination.Y}",
                $"Move to known cell ({option.Destination.X},{option.Destination.Y})",
                stateForOption,
                option.ObjectiveDelta.Contains("active_objective", StringComparison.Ordinal)
                    ? "A fully exposed public route reaches the active objective object; use this to compress repeated single-cell movement."
                    : option.FrontierGain > 0
                        ? "A fully exposed public route reaches a frontier-expanding cell; move_to can compress known traversal before the next decision."
                        : "A fully exposed public route can reposition the agent, but it is demoted because it does not immediately improve frontier or objective progress.",
                MazeQuestToolIds.MoveTo,
                input: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x"] = option.Destination.X,
                    ["y"] = option.Destination.Y
                },
                priority: Math.Max(10, 70 + option.FrontierGain - option.HopCount),
                metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["hopCount"] = option.HopCount,
                    ["directions"] = option.Directions,
                    ["totalTerrainCost"] = option.TotalTerrainCost,
                    ["frontierGain"] = option.FrontierGain,
                    ["maxVisibleRisk"] = option.MaxVisibleRisk,
                    ["guaranteedHazardDamage"] = option.GuaranteedHazardDamage,
                    ["objectiveDelta"] = option.ObjectiveDelta,
                    ["summary"] = option.Summary
                });
        }

        var currentObject = stage.Objects.Values.FirstOrDefault(item => item.Point == state.Position);
        var canTake = currentObject is not null &&
            IsTakeable(currentObject) &&
            !state.Inventory.Contains(currentObject.ObjectId, StringComparer.Ordinal);
        yield return Binding(
            "maze.take.current_object",
            "Take visible current-cell object",
            canTake ? MazeQuestCapabilityBindingState.Preferred : MazeQuestCapabilityBindingState.Blocked,
            canTake
                ? "A takeable objective or resource object is visible at the current cell."
                : "No untaken takeable object is visible at the current cell.",
            MazeQuestToolIds.Take,
            input: canTake
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["objectId"] = currentObject!.ObjectId
                }
                : null,
            priority: canTake ? 95 : 0);

        var canUse = currentObject is not null && IsUsable(currentObject);
        var hasRequiredItem = currentObject?.RequiredItem is null ||
            state.Inventory.Contains(currentObject.RequiredItem, StringComparer.Ordinal);
        yield return Binding(
            "maze.use.current_object",
            "Use visible current-cell object",
            canUse && hasRequiredItem
                ? MazeQuestCapabilityBindingState.Preferred
                : MazeQuestCapabilityBindingState.Blocked,
            canUse
                ? hasRequiredItem
                    ? "A usable visible target is at the current cell and public inventory satisfies its requirement."
                    : "The visible target is usable, but public inventory does not contain the required item."
                : "No usable visible target is at the current cell.",
            MazeQuestToolIds.Use,
            input: canUse && hasRequiredItem
                ? UseInput(currentObject!)
                : null,
            priority: canUse && hasRequiredItem ? 95 : 0);

        var canRest = CanRest(stage, state);
        var shouldRest = canRest && (state.Health <= 4 || state.Energy <= Math.Max(2, stage.EnergyPolicy.Padding / 2));
        yield return Binding(
            "maze.rest",
            "Recover health and energy",
            canRest
                ? shouldRest
                    ? MazeQuestCapabilityBindingState.Preferred
                    : MazeQuestCapabilityBindingState.Available
                : MazeQuestCapabilityBindingState.Blocked,
            canRest
                ? shouldRest
                    ? "Visible resource state makes continued movement risky; rest is available."
                    : "Rest is available, but current resources do not make it urgent."
                : "Rest is unavailable because no charges remain or resources are already full.",
            MazeQuestToolIds.Rest,
            priority: shouldRest ? 85 : canRest ? 35 : 0);

        var remainingRequired = stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => objective.Required)
            .Where(objective => !state.CompletedObjectives.Contains(objective.ObjectiveId))
            .Select(objective => objective.ObjectiveId)
            .ToArray();
        yield return Binding(
            "maze.complete_objective",
            "Complete objective with host proof",
            remainingRequired.Length == 0
                ? MazeQuestCapabilityBindingState.Preferred
                : MazeQuestCapabilityBindingState.Blocked,
            remainingRequired.Length == 0
                ? "All required public objectives are receipt-backed as complete; request the host completion artifact."
                : "Required public objectives remain incomplete; completion would be refused.",
            MazeQuestToolIds.CompleteObjective,
            priority: remainingRequired.Length == 0 ? 100 : 0,
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["remainingRequiredObjectiveIds"] = remainingRequired
            });

        yield return Binding(
            "redacted.fog_of_war_oracle",
            "Redacted hidden route and map oracle",
            MazeQuestCapabilityBindingState.Hidden,
            "The host withholds unrevealed coordinates, full route data, and answer-key object placement to preserve fog-of-war.",
            priority: 0,
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["category"] = "fog_of_war"
            });

        yield return Binding(
            "maze.teleport",
            "Teleport or coordinate jump",
            MazeQuestCapabilityBindingState.Unavailable,
            "This harness has no teleport, hidden global path, or arbitrary coordinate jump. It can only compress movement through maze.move_to when the full route is already public.",
            priority: 0);
    }

    private static MazeQuestCapabilityBinding Binding(
        string capabilityId,
        string label,
        MazeQuestCapabilityBindingState state,
        string reason,
        string? toolId = null,
        IReadOnlyDictionary<string, object?>? input = null,
        int priority = 0,
        IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(
            CapabilityId: capabilityId,
            Label: label,
            State: state,
            Reason: reason,
            ToolId: toolId,
            Input: input,
            Priority: priority,
            Metadata: metadata);

    private static bool CanRest(MazeQuestStage stage, MazeQuestRunState state) =>
        state.RestCharges > 0 &&
        (state.Energy < stage.EnergyPolicy.MaxEnergy || state.Health < 8);

    private static bool IsUsable(MazeQuestObject questObject) =>
        questObject.Kind is MazeQuestObjectKind.Gate
            or MazeQuestObjectKind.DeliveryDropoff
            or MazeQuestObjectKind.Activator
            or MazeQuestObjectKind.Refuge
            or MazeQuestObjectKind.PuzzleRune
            or MazeQuestObjectKind.DiscoveryMarker;

    private static bool IsTakeable(MazeQuestObject questObject) =>
        questObject.Kind is MazeQuestObjectKind.Key
            or MazeQuestObjectKind.Collectible
            or MazeQuestObjectKind.DeliveryPickup
            or MazeQuestObjectKind.RescueTarget
            or MazeQuestObjectKind.ResourceCache;

    private static IReadOnlyDictionary<string, object?> UseInput(MazeQuestObject questObject)
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["targetId"] = questObject.ObjectId
        };

        if (questObject.RequiredItem is not null)
        {
            input["item"] = questObject.RequiredItem;
        }

        return input;
    }

    private static Dictionary<string, object?> Point(MazePoint point) =>
        new(StringComparer.Ordinal)
        {
            ["x"] = point.X,
            ["y"] = point.Y
        };

    private static string HashObject(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, HashJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record CompiledSurface(
        MazeQuestActiveCapabilitySurface Surface,
        MazeQuestContextSurfaceReceipt Receipt);
}

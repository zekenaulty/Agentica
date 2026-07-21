namespace Agentica.Lab.Scenarios.MazeQuest;

public sealed record MazeQuestTemplateResult(
    MazeQuestDefinition Quest,
    MazeQuestPlacements Placements,
    IReadOnlyDictionary<string, MazeQuestObject> Objects);

public sealed class MazeQuestTemplateGenerator
{
    private static readonly MazeQuestArchetype[] Archetypes =
    [
        MazeQuestArchetype.Unlock,
        MazeQuestArchetype.Collect,
        MazeQuestArchetype.Delivery,
        MazeQuestArchetype.Explore,
        MazeQuestArchetype.Activate,
        MazeQuestArchetype.PuzzleSequence,
        MazeQuestArchetype.Rescue,
        MazeQuestArchetype.ResourceRoute
    ];

    private readonly Random _random;
    private readonly MazeQuestDecorators _decorators;

    public MazeQuestTemplateGenerator(Random random, MazeQuestDecorators decorators)
    {
        _random = random;
        _decorators = decorators;
    }

    public MazeQuestTemplateResult Generate(
        MazeQuestDescriptor descriptor,
        IReadOnlyList<MazePoint> path,
        MazeQuestArchetype? requestedType)
    {
        var archetype = requestedType ?? Archetypes[_random.Next(Archetypes.Length)];
        return archetype switch
        {
            MazeQuestArchetype.Collect => BuildCollect(descriptor, path),
            MazeQuestArchetype.Delivery => BuildDelivery(descriptor, path),
            MazeQuestArchetype.Explore => BuildExplore(descriptor, path),
            MazeQuestArchetype.Activate => BuildActivate(descriptor, path),
            MazeQuestArchetype.PuzzleSequence => BuildPuzzleSequence(descriptor, path),
            MazeQuestArchetype.Rescue => BuildRescue(descriptor, path),
            MazeQuestArchetype.ResourceRoute => BuildResourceRoute(descriptor, path),
            _ => BuildUnlock(descriptor, path)
        };
    }

    private MazeQuestTemplateResult BuildUnlock(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var key = PointAt(path, 0.33);
        var cache = PointAt(path, 0.5);
        var gate = PointAt(path, 0.66);
        var exit = path[^1];
        var objects = ObjectBuilder();
        Add(objects, "sun_key", MazeQuestObjectKind.Key, key, _decorators.DecorateObjectiveItem(MazeObjectiveItem.SunKey), null, "inventory", "dependency_order");
        Add(objects, "focus_cache", MazeQuestObjectKind.ResourceCache, cache, _decorators.ResourceCache(), null, "optional", "resource_management");
        Add(objects, "sun_gate", MazeQuestObjectKind.Gate, gate, "sun gate", "sun_key", "blocked_recovery", "inventory_use");
        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Sun Gate",
            "Find the sun key, unlock the sun gate, and reach the exit.",
            MazeQuestArchetype.Unlock,
            ["dependency_order", "blocked_recovery", "inventory_use"],
            new MazeQuestPlacements(path[0], key, gate, exit),
            objects,
            [
                new MazeQuestObjective("find_sun_key", "Find the sun key.", MazeObjectiveKind.FindItem, "sun_key"),
                new MazeQuestObjective("collect_focus_cache", "Optionally collect the focus cache if the route budget justifies it.", MazeObjectiveKind.CollectItem, "focus_cache")
                {
                    Required = false,
                    Priority = 35
                },
                new MazeQuestObjective("unlock_sun_gate", "Unlock the sun gate.", MazeObjectiveKind.UnlockGate, "sun_gate"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildCollect(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var points = DistinctPoints(path, 0.25, 0.5, 0.75);
        var exit = path[^1];
        var objects = ObjectBuilder();
        for (var index = 0; index < points.Count; index++)
        {
            Add(objects, $"relic_{index + 1}", MazeQuestObjectKind.Collectible, points[index], _decorators.Collectible(index + 1), null, "collection", "inventory_count");
        }

        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Relic Sweep",
            "Collect the scattered relics and reach the exit.",
            MazeQuestArchetype.Collect,
            ["collection", "inventory_count", "route_planning"],
            new MazeQuestPlacements(path[0], points[0], points[^1], exit),
            objects,
            [
                new MazeQuestObjective("collect_relic_1", $"Collect {objects["relic_1"].DisplayName}.", MazeObjectiveKind.CollectItem, "relic_1"),
                new MazeQuestObjective("collect_relic_2", $"Collect {objects["relic_2"].DisplayName}.", MazeObjectiveKind.CollectItem, "relic_2"),
                new MazeQuestObjective("collect_relic_3", $"Collect {objects["relic_3"].DisplayName}.", MazeObjectiveKind.CollectItem, "relic_3"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildDelivery(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var pickup = PointAt(path, 0.3);
        var dropoff = PointAt(path, 0.72);
        var exit = path[^1];
        var objects = ObjectBuilder();
        Add(objects, "courier_charm", MazeQuestObjectKind.DeliveryPickup, pickup, _decorators.DeliveryPickup(), null, "pickup");
        Add(objects, "dropoff", MazeQuestObjectKind.DeliveryDropoff, dropoff, _decorators.DeliveryDropoff(), "courier_charm", "delivery", "backtracking");
        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Courier Thread",
            "Recover the courier charm, deliver it to the marked place, and reach the exit.",
            MazeQuestArchetype.Delivery,
            ["pickup_and_delivery", "destination_memory", "backtracking"],
            new MazeQuestPlacements(path[0], pickup, dropoff, exit),
            objects,
            [
                new MazeQuestObjective("recover_charm", "Recover the courier charm.", MazeObjectiveKind.FindItem, "courier_charm"),
                new MazeQuestObjective("deliver_charm", "Deliver the charm to its dropoff.", MazeObjectiveKind.DeliverItem, "dropoff"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildExplore(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var points = DistinctPoints(path, 0.35, 0.62);
        var exit = path[^1];
        var objects = ObjectBuilder();
        Add(objects, "marker_1", MazeQuestObjectKind.DiscoveryMarker, points[0], _decorators.DiscoveryMarker(1), null, "fog_coverage");
        Add(objects, "marker_2", MazeQuestObjectKind.DiscoveryMarker, points[1], _decorators.DiscoveryMarker(2), null, "frontier_selection");
        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Cartographer Walk",
            "Discover two marked locations and reach the exit.",
            MazeQuestArchetype.Explore,
            ["fog_coverage", "frontier_selection", "location_discovery"],
            new MazeQuestPlacements(path[0], points[0], points[1], exit),
            objects,
            [
                new MazeQuestObjective("discover_marker_1", "Discover the first marker.", MazeObjectiveKind.DiscoverLocation, "marker_1"),
                new MazeQuestObjective("discover_marker_2", "Discover the second marker.", MazeObjectiveKind.DiscoverLocation, "marker_2"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildActivate(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var points = DistinctPoints(path, 0.3, 0.55, 0.78);
        var exit = path[^1];
        var objects = ObjectBuilder();
        Add(objects, "activator_1", MazeQuestObjectKind.Activator, points[0], _decorators.Activator(1), null, "multi_target");
        Add(objects, "activator_2", MazeQuestObjectKind.Activator, points[1], _decorators.Activator(2), null, "stateful_unlock");
        Add(objects, "gate_shrine", MazeQuestObjectKind.DeliveryDropoff, points[2], "charged gate shrine", "activator_charge", "object_interaction");
        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Obelisk Circuit",
            "Activate the obelisks, touch the gate shrine, and reach the exit.",
            MazeQuestArchetype.Activate,
            ["object_interaction", "multi_target", "stateful_unlock"],
            new MazeQuestPlacements(path[0], points[0], points[2], exit),
            objects,
            [
                new MazeQuestObjective("activate_1", "Activate the first obelisk.", MazeObjectiveKind.ActivateObject, "activator_1"),
                new MazeQuestObjective("activate_2", "Activate the second obelisk.", MazeObjectiveKind.ActivateObject, "activator_2"),
                new MazeQuestObjective("activate_shrine", "Use the charge at the gate shrine.", MazeObjectiveKind.ActivateObject, "gate_shrine"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildPuzzleSequence(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var points = DistinctPoints(path, 0.25, 0.5, 0.75);
        var exit = path[^1];
        var objects = ObjectBuilder();
        for (var index = 0; index < points.Count; index++)
        {
            Add(objects, $"rune_{index + 1}", MazeQuestObjectKind.PuzzleRune, points[index], _decorators.PuzzleRune(index + 1), null, "ordered_interaction");
        }

        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Rune Order",
            "Activate the runes in order and reach the exit.",
            MazeQuestArchetype.PuzzleSequence,
            ["sequence_constraints", "ordered_interaction", "state_memory"],
            new MazeQuestPlacements(path[0], points[0], points[^1], exit),
            objects,
            [
                new MazeQuestObjective("activate_rune_1", "Activate the first rune.", MazeObjectiveKind.ActivateObject, "rune_1"),
                new MazeQuestObjective("activate_rune_2", "Activate the second rune.", MazeObjectiveKind.ActivateObject, "rune_2"),
                new MazeQuestObjective("activate_rune_3", "Activate the third rune.", MazeObjectiveKind.ActivateObject, "rune_3"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildRescue(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var target = PointAt(path, 0.62);
        var refuge = PointAt(path, 0.18);
        var exit = path[^1];
        var objects = ObjectBuilder();
        Add(objects, "lost_scout", MazeQuestObjectKind.RescueTarget, target, _decorators.RescueTarget(), null, "retrieval");
        Add(objects, "refuge", MazeQuestObjectKind.Refuge, refuge, _decorators.Refuge(), "lost_scout", "return_route");
        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Lost Scout",
            "Find the scout marker, return it to refuge, and reach the exit.",
            MazeQuestArchetype.Rescue,
            ["retrieval", "return_route", "partial_success"],
            new MazeQuestPlacements(path[0], target, refuge, exit),
            objects,
            [
                new MazeQuestObjective("find_scout", "Find the lost scout marker.", MazeObjectiveKind.RescueTarget, "lost_scout"),
                new MazeQuestObjective("return_scout", "Return the marker to refuge.", MazeObjectiveKind.DeliverItem, "refuge"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private MazeQuestTemplateResult BuildResourceRoute(MazeQuestDescriptor descriptor, IReadOnlyList<MazePoint> path)
    {
        var cache = PointAt(path, 0.35);
        var checkpoint = PointAt(path, 0.72);
        var exit = path[^1];
        var objects = ObjectBuilder();
        Add(objects, "focus_cache", MazeQuestObjectKind.ResourceCache, cache, _decorators.ResourceCache(), null, "resource_management");
        Add(objects, "hazard_crossing", MazeQuestObjectKind.DiscoveryMarker, checkpoint, "hazard crossing marker", null, "risk_reward");
        Add(objects, "exit", MazeQuestObjectKind.Exit, exit, "north exit", null, "terminal_evidence");

        return Result(
            descriptor,
            "Careful Crossing",
            "Collect the focus cache, cross the marked route, and reach the exit.",
            MazeQuestArchetype.ResourceRoute,
            ["resource_management", "risk_reward", "hazard_avoidance"],
            new MazeQuestPlacements(path[0], cache, checkpoint, exit),
            objects,
            [
                new MazeQuestObjective("collect_cache", "Collect the focus cache.", MazeObjectiveKind.CollectItem, "focus_cache"),
                new MazeQuestObjective("reach_crossing", "Reach the marked crossing.", MazeObjectiveKind.DiscoverLocation, "hazard_crossing"),
                new MazeQuestObjective("reach_exit", "Reach the exit.", MazeObjectiveKind.ReachExit, "exit"),
                new MazeQuestObjective("complete", "Complete the quest.", MazeObjectiveKind.Complete, "objective")
            ]);
    }

    private static MazeQuestTemplateResult Result(
        MazeQuestDescriptor descriptor,
        string titleSuffix,
        string objective,
        MazeQuestArchetype archetype,
        IReadOnlyList<string> coverageTags,
        MazeQuestPlacements placements,
        IReadOnlyDictionary<string, MazeQuestObject> objects,
        IReadOnlyList<MazeQuestObjective> objectives) =>
        new(
            Quest: new MazeQuestDefinition(
                QuestId: descriptor.QuestId,
                Title: $"{descriptor.Title}: {titleSuffix}",
                Objective: objective,
                QuestType: archetype,
                CoverageTags: coverageTags,
                Objectives: objectives),
            Placements: placements,
            Objects: objects);

    private static Dictionary<string, MazeQuestObject> ObjectBuilder() =>
        new(StringComparer.Ordinal);

    private static void Add(
        IDictionary<string, MazeQuestObject> objects,
        string objectId,
        MazeQuestObjectKind kind,
        MazePoint point,
        string displayName,
        string? requiredItem,
        params string[] coverageTags)
    {
        objects[objectId] = new MazeQuestObject(
            ObjectId: objectId,
            Kind: kind,
            Point: point,
            DisplayName: displayName,
            RequiredItem: requiredItem,
            CoverageTags: coverageTags);
    }

    private static MazePoint PointAt(IReadOnlyList<MazePoint> path, double fraction)
    {
        var index = Math.Clamp((int)Math.Round((path.Count - 1) * fraction), 1, path.Count - 1);
        return path[index];
    }

    private static IReadOnlyList<MazePoint> DistinctPoints(IReadOnlyList<MazePoint> path, params double[] fractions)
    {
        var points = new List<MazePoint>();
        foreach (var fraction in fractions)
        {
            var point = PointAt(path, fraction);
            if (!points.Contains(point))
            {
                points.Add(point);
            }
        }

        for (var index = 1; points.Count < fractions.Length && index < path.Count - 1; index++)
        {
            if (!points.Contains(path[index]))
            {
                points.Add(path[index]);
            }
        }

        return points;
    }
}

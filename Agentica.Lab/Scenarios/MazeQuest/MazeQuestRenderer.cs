using System.Text;

namespace Agentica.Lab.Scenarios.MazeQuest;

public static class MazeQuestRenderer
{
    public static string RenderFog(MazeQuestStage stage, MazeQuestRunState state)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, stage, state);
        AppendMap(builder, stage, state.Position, state.Discovered, revealAll: false);
        AppendLegend(builder);
        return builder.ToString();
    }

    public static string RenderRevealed(MazeQuestStage stage, MazeQuestRunState state)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, stage, state);
        AppendMap(builder, stage, state.Position, stage.Grid.Cells.Keys.ToHashSet(), revealAll: true);
        AppendLegend(builder);
        return builder.ToString();
    }

    private static void AppendHeader(StringBuilder builder, MazeQuestStage stage, MazeQuestRunState state)
    {
        var activeObjective = stage.Quest.Objectives.First(objective => objective.ObjectiveId == state.ActiveObjectiveId);
        builder.AppendLine($"Stage: {stage.Quest.Title}");
        builder.AppendLine($"Seed: {stage.Seed}");
        builder.AppendLine($"Position: ({state.Position.X}, {state.Position.Y})  Health: {state.Health}  Energy: {state.Energy}  Steps: {state.StepCount}");
        builder.AppendLine($"Active Objective: {activeObjective.Description}");
        builder.AppendLine();
    }

    private static void AppendMap(
        StringBuilder builder,
        MazeQuestStage stage,
        MazePoint position,
        IReadOnlySet<MazePoint> discovered,
        bool revealAll)
    {
        for (var y = 0; y < stage.Grid.Height; y++)
        {
            for (var x = 0; x < stage.Grid.Width; x++)
            {
                var point = new MazePoint(x, y);
                if (point == position)
                {
                    builder.Append('@');
                    continue;
                }

                if (!revealAll && !discovered.Contains(point))
                {
                    builder.Append('?');
                    continue;
                }

                builder.Append(SymbolFor(stage, stage.Grid[point]));
            }

            builder.AppendLine();
        }
    }

    private static char SymbolFor(MazeQuestStage stage, MazeCell cell)
    {
        if (cell.Terrain == MazeTerrain.Wall)
        {
            return '#';
        }

        var questObject = stage.Objects.Values.FirstOrDefault(item => item.Point == cell.Point);
        if (questObject is not null)
        {
            return questObject.Kind switch
            {
                MazeQuestObjectKind.Key => 'K',
                MazeQuestObjectKind.Gate => 'G',
                MazeQuestObjectKind.Exit => 'E',
                MazeQuestObjectKind.Collectible => 'C',
                MazeQuestObjectKind.DeliveryPickup => 'P',
                MazeQuestObjectKind.DeliveryDropoff => 'D',
                MazeQuestObjectKind.DiscoveryMarker => 'M',
                MazeQuestObjectKind.Activator => 'A',
                MazeQuestObjectKind.RescueTarget => 'R',
                MazeQuestObjectKind.Refuge => 'F',
                MazeQuestObjectKind.PuzzleRune => 'O',
                MazeQuestObjectKind.ResourceCache => '+',
                _ => '.'
            };
        }

        if (cell.Hazard != MazeHazard.None)
        {
            return '!';
        }

        if (cell.Reward != MazeReward.None)
        {
            return '+';
        }

        return '.';
    }

    private static void AppendLegend(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("Legend: @ agent  ? unknown  # wall  . floor  ! hazard  + reward/cache  K key  G gate  E exit  C collect  P pickup  D dropoff  M marker  A activator  R rescue  F refuge  O rune");
    }
}

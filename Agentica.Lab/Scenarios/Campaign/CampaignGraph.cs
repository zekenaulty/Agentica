namespace Agentica.Lab.Scenarios.Campaign;

public static class CampaignGraph
{
    public static IReadOnlyList<CampaignMilestone> AvailableMilestones(
        CampaignDefinition definition,
        CampaignState state)
    {
        var completed = state.CompletedMilestones.ToHashSet(StringComparer.Ordinal);
        var blocked = state.BlockedMilestones.ToHashSet(StringComparer.Ordinal);

        return definition.Milestones
            .Where(milestone =>
                !completed.Contains(milestone.MilestoneId) &&
                !blocked.Contains(milestone.MilestoneId) &&
                milestone.DependsOn.All(completed.Contains))
            .Select((milestone, index) => new { Milestone = milestone, Index = index })
            .OrderBy(milestone => milestone.Milestone.Priority)
            .ThenBy(milestone => milestone.Index)
            .Select(milestone => milestone.Milestone)
            .ToArray();
    }

    public static bool RequiredMilestonesComplete(
        CampaignDefinition definition,
        CampaignState state)
    {
        var completed = state.CompletedMilestones.ToHashSet(StringComparer.Ordinal);
        return definition.Milestones
            .Where(milestone => !milestone.Optional)
            .All(milestone => completed.Contains(milestone.MilestoneId));
    }
}

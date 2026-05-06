using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.Campaign;

public sealed class DungeonCampaignDeterministicPlanner : IWorkflowPlanner
{
    private readonly CampaignMilestone _milestone;

    public DungeonCampaignDeterministicPlanner(CampaignMilestone milestone)
    {
        _milestone = milestone;
    }

    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreatePlan());

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreatePlan());

    private WorkflowPlan CreatePlan()
    {
        PlanStep[] steps = _milestone.MilestoneId switch
        {
            "acquire_lantern" =>
            [
                Step(1, DungeonCampaignToolIds.AcquireItem, ("item", "lantern")),
                Complete(2)
            ],
            "acquire_bronze_key" =>
            [
                Step(1, DungeonCampaignToolIds.AcquireItem, ("item", "bronze_key")),
                Complete(2)
            ],
            "explore_dark_archive" =>
            [
                Step(1, DungeonCampaignToolIds.Explore, ("area", "dark_archive")),
                Complete(2)
            ],
            "unlock_bronze_vault" =>
            [
                Step(1, DungeonCampaignToolIds.Unlock, ("gate", "bronze_vault")),
                Complete(2)
            ],
            "recover_moon_sigil" =>
            [
                Step(1, DungeonCampaignToolIds.AcquireItem, ("item", "moon_sigil")),
                Complete(2)
            ],
            "recover_sun_sigil" =>
            [
                Step(1, DungeonCampaignToolIds.AcquireItem, ("item", "sun_sigil")),
                Complete(2)
            ],
            "optional_cache" =>
            [
                Step(1, DungeonCampaignToolIds.Unlock, ("gate", "optional_cache")),
                Complete(2)
            ],
            "open_final_gate" =>
            [
                Step(1, DungeonCampaignToolIds.OpenFinalGate),
                Complete(2)
            ],
            _ =>
            [
                Step(1, DungeonCampaignToolIds.GetState)
            ]
        };

        return new WorkflowPlan(
            $"campaign_plan_{_milestone.MilestoneId}",
            1,
            steps,
            $"Deterministic plan for campaign milestone {_milestone.MilestoneId}.");
    }

    private PlanStep Complete(int number) =>
        Step(number, DungeonCampaignToolIds.CompleteMilestone, ("milestoneId", _milestone.MilestoneId));

    private PlanStep Step(
        int number,
        string toolId,
        params (string Key, object? Value)[] input) =>
        new(
            $"campaign_step_{_milestone.MilestoneId}_{number:000}",
            toolId,
            ToolKind.Action,
            ToolEffect.WritesLocalState,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

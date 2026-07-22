using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Lab.Scenarios.MazeQuest;

public sealed class MazeQuestDeterministicPlanner : IWorkflowPlanner
{
    private readonly MazeQuestSession _session;
    private int _nextStepNumber = 1;

    public MazeQuestDeterministicPlanner(MazeQuestSession session)
    {
        _session = session;
    }

    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextPlan(request));

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextPlan(request));

    private WorkflowPlan NextPlan(PlanningRequest request)
    {
        var stepNumber = _nextStepNumber++;
        if (stepNumber == 1)
        {
            return Plan(stepNumber, Step(stepNumber, MazeQuestToolIds.GetState, ToolKind.Query, ToolEffect.ReadOnly));
        }

        var step = NextStep(stepNumber);
        return Plan(stepNumber, step);
    }

    private PlanStep NextStep(int stepNumber)
    {
        var objective = _session.Stage.Quest.Objectives.FirstOrDefault(item =>
            item.Required && !_session.State.CompletedObjectives.Contains(item.ObjectiveId));

        if (objective is null || objective.Kind == MazeObjectiveKind.Complete)
        {
            return Step(stepNumber, MazeQuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState);
        }

        if (!_session.Stage.Objects.TryGetValue(objective.TargetId, out var target))
        {
            return Step(stepNumber, MazeQuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState);
        }

        if (_session.State.Position != target.Point)
        {
            var path = MazePathfinder.ShortestPath(_session.Stage.Grid, _session.State.Position, target.Point);
            if (path.Count > 1)
            {
                return Step(
                    stepNumber,
                    MazeQuestToolIds.Move,
                    ToolKind.Action,
                    ToolEffect.WritesLocalState,
                    ("direction", DirectionBetween(path[0], path[1])));
            }

            return Step(stepNumber, MazeQuestToolIds.EvaluateMoves, ToolKind.Query, ToolEffect.ReadOnly);
        }

        return objective.Kind switch
        {
            MazeObjectiveKind.FindItem or MazeObjectiveKind.CollectItem or MazeObjectiveKind.RescueTarget =>
                Step(
                    stepNumber,
                    MazeQuestToolIds.Take,
                    ToolKind.Action,
                    ToolEffect.WritesLocalState,
                    ("objectId", target.ObjectId)),
            MazeObjectiveKind.UnlockGate =>
                Step(
                    stepNumber,
                    MazeQuestToolIds.Use,
                    ToolKind.Action,
                    ToolEffect.WritesLocalState,
                    ("targetId", target.ObjectId),
                    ("item", target.RequiredItem)),
            MazeObjectiveKind.DeliverItem or MazeObjectiveKind.DiscoverLocation or MazeObjectiveKind.ActivateObject =>
                Step(
                    stepNumber,
                    MazeQuestToolIds.Use,
                    ToolKind.Action,
                    ToolEffect.WritesLocalState,
                    ("targetId", target.ObjectId),
                    ("item", target.RequiredItem)),
            MazeObjectiveKind.ReachExit =>
                Step(stepNumber, MazeQuestToolIds.EvaluateMoves, ToolKind.Query, ToolEffect.ReadOnly),
            _ => Step(stepNumber, MazeQuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState)
        };
    }

    private static WorkflowPlan Plan(int version, PlanStep step) =>
        new(
            PlanId: $"maze_plan_{version:000}",
            Version: version,
            Steps: [step],
            Description: "Deterministic MazeQuest plan slice.");

    private static PlanStep Step(
        int number,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            StepId: $"maze_step_{number:000}",
            ToolId: toolId,
            Kind: kind,
            Effect: effect,
            Input: input
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static string DirectionBetween(MazePoint from, MazePoint to)
    {
        if (to.X > from.X)
        {
            return "east";
        }

        if (to.X < from.X)
        {
            return "west";
        }

        return to.Y > from.Y ? "south" : "north";
    }
}

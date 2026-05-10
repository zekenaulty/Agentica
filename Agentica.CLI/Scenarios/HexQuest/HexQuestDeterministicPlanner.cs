using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.HexQuest;

public sealed class HexQuestDeterministicPlanner : IWorkflowPlanner
{
    private readonly string _scenarioId;
    private int _nextStepNumber = 1;

    public HexQuestDeterministicPlanner(string scenarioId = "xor_checksum_strength")
    {
        _scenarioId = scenarioId;
    }

    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextPlan());

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextPlan());

    private WorkflowPlan NextPlan()
    {
        var stepNumber = _nextStepNumber++;
        return new WorkflowPlan(
            $"hexquest_plan_{stepNumber:000}",
            stepNumber,
            [StepForScenario(stepNumber)],
            "Deterministic HexQuest plan slice.")
        {
            PlanningReason = stepNumber switch
            {
                <= 4 => "insufficient_transform_evidence",
                5 => "patch_validation",
                _ => "commit_verified_encoded_patch"
            }
        };
    }

    private PlanStep StepForScenario(int stepNumber) =>
        string.Equals(_scenarioId, "record_scope_conflict_v2", StringComparison.OrdinalIgnoreCase)
            ? RecordScopeConflictV2Step(stepNumber)
            : 
        string.Equals(_scenarioId, "record_scope_conflict", StringComparison.OrdinalIgnoreCase)
            ? RecordScopeConflictStep(stepNumber)
            : IntroStep(stepNumber);

    private static PlanStep IntroStep(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, HexQuestToolIds.InspectEncoded, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, HexQuestToolIds.InspectDecoded, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(stepNumber, HexQuestToolIds.RequestExample, ToolKind.Query, ToolEffect.ReadOnly),
            4 => Step(
                stepNumber,
                HexQuestToolIds.SandboxSetDecoded,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("field", "Strength"),
                ("value", 18)),
            5 => Step(
                stepNumber,
                HexQuestToolIds.ValidatePatch,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("patch", "0:A9>B7,4:E8>E6")),
            _ => Step(
                stepNumber,
                HexQuestToolIds.CommitPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("patch", "0:A9>B7,4:E8>E6"))
        };

    private static PlanStep RecordScopeConflictStep(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, HexQuestToolIds.InspectEncoded, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, HexQuestToolIds.InspectDecoded, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(stepNumber, HexQuestToolIds.RequestExample, ToolKind.Query, ToolEffect.ReadOnly),
            4 => Step(
                stepNumber,
                HexQuestToolIds.SandboxSetDecoded,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("entity", "B"),
                ("field", "Strength"),
                ("value", 18)),
            5 => Step(
                stepNumber,
                HexQuestToolIds.ValidatePatch,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("patch", "16:A9>B7,48:1C>12")),
            _ => Step(
                stepNumber,
                HexQuestToolIds.CommitPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("patch", "16:A9>B7,48:1C>12"))
        };

    private static PlanStep RecordScopeConflictV2Step(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, HexQuestToolIds.InspectEncoded, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, HexQuestToolIds.InspectDecoded, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(
                stepNumber,
                HexQuestToolIds.SandboxSetDecoded,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("entity", "A"),
                ("field", "Strength"),
                ("value", 18)),
            4 => Step(
                stepNumber,
                HexQuestToolIds.SandboxSetDecoded,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("entity", "B"),
                ("field", "Strength"),
                ("value", 18)),
            5 => Step(
                stepNumber,
                HexQuestToolIds.ValidatePatch,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                ("patch", "32:A9>B7,120:D6>D8")),
            _ => Step(
                stepNumber,
                HexQuestToolIds.CommitPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("patch", "32:A9>B7,120:D6>D8"))
        };

    private static PlanStep Step(
        int number,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            $"hexquest_step_{number:000}",
            toolId,
            kind,
            effect,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
        {
            Reason = ReasonFor(toolId)
        };

    private static string ReasonFor(string toolId) =>
        toolId switch
        {
            HexQuestToolIds.InspectEncoded => "Inspect the authoritative encoded payload before hypothesizing offsets.",
            HexQuestToolIds.InspectDecoded => "Inspect the decoded projection and goal constraints.",
            HexQuestToolIds.RequestExample => "Gather a paired decoded/encoded example from the hidden transform.",
            HexQuestToolIds.SandboxSetDecoded => "Probe the encoded delta for a decoded target edit in a sandbox copy.",
            HexQuestToolIds.ValidatePatch => "Dry-run the inferred encoded payload patch before committing.",
            HexQuestToolIds.CommitPatch => "Win only through the authoritative encoded payload patch surface.",
            _ => "Continue the bounded HexQuest plan slice."
        };
}

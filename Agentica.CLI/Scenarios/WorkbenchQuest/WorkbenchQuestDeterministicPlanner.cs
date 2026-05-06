using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.WorkbenchQuest;

public sealed class WorkbenchQuestDeterministicPlanner : IWorkflowPlanner
{
    private readonly string _scenarioId;
    private int _nextStepNumber = 1;

    public WorkbenchQuestDeterministicPlanner(string scenarioId)
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
        var step = _scenarioId switch
        {
            "missing_mapping" => MissingMappingStep(stepNumber),
            "structured_doc_merge" => StructuredDocMergeStep(stepNumber),
            "word_ladder" => WordLadderStep(stepNumber),
            _ => BrokenCheckStep(stepNumber)
        };

        return new WorkflowPlan(
            PlanId: $"workbench_plan_{stepNumber:000}",
            Version: stepNumber,
            Steps: [step],
            Description: "Deterministic WorkbenchQuest plan slice.")
        {
            PlanningReason = stepNumber switch
            {
                <= 4 => "insufficient_evidence",
                5 => "mutation_risk",
                6 => "failed_verification",
                _ => "completion_check"
            }
        };
    }

    private static PlanStep BrokenCheckStep(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, WorkbenchQuestToolIds.ListFiles, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "tests/CalculatorTests.txt")),
            4 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "src/Calculator.txt")),
            5 => Step(
                stepNumber,
                WorkbenchQuestToolIds.ApplyPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("path", "src/Calculator.txt"),
                ("find", "return left - right"),
                ("replace", "return left + right"),
                ("rationale", "The failing Add check expects 2 and 3 to combine to 5, while the implementation subtracts.")),
            6 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            _ => Step(stepNumber, WorkbenchQuestToolIds.Complete, ToolKind.Action, ToolEffect.WritesLocalState)
        };

    private static PlanStep MissingMappingStep(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, WorkbenchQuestToolIds.ListFiles, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "expected/output.csv")),
            4 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "config/mapping.csv")),
            5 => Step(
                stepNumber,
                WorkbenchQuestToolIds.ApplyPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("path", "config/mapping.csv"),
                ("find", "EXP,UNKNOWN"),
                ("replace", "EXP,EXPRESS"),
                ("rationale", "The output comparison shows EXP produces UNKNOWN where expected output requires EXPRESS.")),
            6 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            _ => Step(stepNumber, WorkbenchQuestToolIds.Complete, ToolKind.Action, ToolEffect.WritesLocalState)
        };

    private static PlanStep StructuredDocMergeStep(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, WorkbenchQuestToolIds.ListFiles, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "merge_rules.txt")),
            4 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "revision_a.md")),
            5 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "revision_b.md")),
            6 => Step(
                stepNumber,
                WorkbenchQuestToolIds.ApplyPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("path", "merged.md"),
                ("find",
                    """
                    <!-- section:id=intro -->
                    Welcome to the account guide.

                    <!-- section:id=billing -->
                    Billing happens on the first day of each month.

                    <!-- section:id=legacy -->
                    Legacy tokens may be used for old integrations.
                    """),
                ("replace",
                    """
                    <!-- section:id=intro -->
                    Welcome to the account guide. Keep your profile email current.

                    <!-- section:id=billing -->
                    Billing happens on the first business day of each month.

                    <!-- section:id=support -->
                    Contact support within 30 days to dispute an invoice.
                    """),
                ("rationale", "Merge rules select intro from revision A, billing and support from revision B, and drop legacy.")),
            7 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            _ => Step(stepNumber, WorkbenchQuestToolIds.Complete, ToolKind.Action, ToolEffect.WritesLocalState)
        };

    private static PlanStep WordLadderStep(int stepNumber) =>
        stepNumber switch
        {
            1 => Step(stepNumber, WorkbenchQuestToolIds.ListFiles, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            3 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "rules.txt")),
            4 => Step(stepNumber, WorkbenchQuestToolIds.ReadFile, ToolKind.Query, ToolEffect.ReadOnly, ("path", "dictionary.txt")),
            5 => Step(
                stepNumber,
                WorkbenchQuestToolIds.ApplyPatch,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("path", "answer.json"),
                ("find", """["cold", "warm"]"""),
                ("replace", """["cold", "cord", "card", "ward", "warm"]"""),
                ("rationale", "Each adjacent dictionary word changes one letter from cold to warm.")),
            6 => Step(stepNumber, WorkbenchQuestToolIds.RunCheck, ToolKind.Query, ToolEffect.ReadOnly),
            _ => Step(stepNumber, WorkbenchQuestToolIds.Complete, ToolKind.Action, ToolEffect.WritesLocalState)
        };

    private static PlanStep Step(
        int number,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            StepId: $"workbench_step_{number:000}",
            ToolId: toolId,
            Kind: kind,
            Effect: effect,
            Input: input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
        {
            Reason = ReasonFor(toolId)
        };

    private static string ReasonFor(string toolId) =>
        toolId switch
        {
            WorkbenchQuestToolIds.ListFiles => "Establish available raw evidence before choosing files.",
            WorkbenchQuestToolIds.RunCheck => "Capture the failing validation output before mutation.",
            WorkbenchQuestToolIds.ReadFile => "Inspect raw source or test evidence before patching.",
            WorkbenchQuestToolIds.ApplyPatch => "Apply the smallest evidence-supported source change.",
            WorkbenchQuestToolIds.Complete => "Complete only after verification evidence exists.",
            _ => "Continue the bounded WorkbenchQuest plan slice."
        };
}

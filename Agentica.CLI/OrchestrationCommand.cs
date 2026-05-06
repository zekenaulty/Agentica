using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Orchestration;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Orchestration;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

internal static class OrchestrationCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<bool> geminiCredentialsAvailable,
        Action printUsage)
    {
        var options = OrchestrationRunOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            printUsage();
            return 2;
        }

        if (options.TaskPlanner == PlannerKind.Gemini && !geminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Gemini task planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        var eventSink = new ConsoleEventSink();
        var orchestrator = new TaskOrchestrator(
            CreateTaskPlanner(options),
            new InProcessAgenticaRunExecutor(
                _ => new DeterministicWorkflowPlanner(),
                _ => DemoTools.CreateCatalog(),
                eventSink,
                new DeterministicOutcomeReporter(),
                _ => new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2)),
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>());

        var outcome = await orchestrator.RunAsync(
            new LargeTaskRequest(
                options.Objective,
                RequestOrigin.User,
                new Dictionary<string, object?>()),
            CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("--- OrchestrationOutcomeEnvelope ---");
        Console.WriteLine(JsonSerializer.Serialize(outcome, JsonOptions.Create()));
        return outcome.Status == OrchestrationStatus.Succeeded ? 0 : 1;
    }

    private static ITaskPlanner CreateTaskPlanner(OrchestrationRunOptions options)
    {
        if (options.TaskPlanner == PlannerKind.Deterministic)
        {
            return new DeterministicSingleTaskPlanner();
        }

        var modelId = options.ModelId ?? GeminiModelId.Flash25;
        var llmClient = new RetryingLlmClient(
            new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
            new LlmRetryOptions(CallTimeout: TimeSpan.FromMinutes(10)));
        return new LlmTaskPlanner(
            llmClient,
            new LlmTaskPlannerOptions(
                modelId,
                new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: 4096,
                    Thinking: LlmThinkingOptions.Off())));
    }

    private sealed class DeterministicSingleTaskPlanner : ITaskPlanner
    {
        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskGraphPlan(
                "task_plan_001",
                request.Request.Objective,
                [
                    new TaskNode(
                        "direct_agentica_run",
                        request.Request.Objective,
                        [],
                        Optional: false,
                        Priority: 1,
                        MaxRuns: 1,
                        new Dictionary<string, object?>(),
                        [
                            new TaskAcceptanceRequirement(
                                TaskAcceptanceRequirementKind.OutcomeStatus,
                                RunOutcomeStatus.Succeeded),
                            new TaskAcceptanceRequirement(
                                TaskAcceptanceRequirementKind.Artifact,
                                ArtifactKind: "action_result")
                        ])
                ],
                [],
                DateTimeOffset.UtcNow));

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskGraphRefinement(
                "deterministic_no_refinement",
                [],
                ["Deterministic single-task planner cannot refine the task graph."],
                RequiresUserInput: true));
    }
}

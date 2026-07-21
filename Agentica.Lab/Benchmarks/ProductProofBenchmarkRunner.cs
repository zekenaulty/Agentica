using System.Diagnostics;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Lab.Scenarios.MazeQuest;
using Agentica.Lab.Scenarios.WorkbenchQuest;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Lab.Benchmarks;

public sealed record ProductProofBenchmarkConfiguration(
    string HarnessVersion,
    string ConfigurationId,
    string ProviderName,
    string ApiSurface,
    string ModelId,
    double Temperature,
    int ThinkingBudgetTokens,
    int MaxOutputTokens,
    PlanningMode PlanningMode,
    string InitialPromptVersion,
    string RefinementPromptVersion,
    string InitialSchemaVersion,
    string RefinementSchemaVersion,
    int PlannerRepairAttempts,
    int ProviderMaxAttempts,
    int ProviderRetryBaseDelayMilliseconds,
    int ProviderRetryMaxDelayMilliseconds,
    int ProviderCallTimeoutSeconds,
    bool ProviderRetryJitter,
    int PlanningContextRecentObservations,
    int PlanningContextRecentReceipts,
    int WorkbenchMaxBlockedRetries,
    int WorkbenchMaxSteps,
    int WorkbenchMaxRefinements,
    int WorkbenchMaxPlanContinuations,
    int WorkbenchTimeoutSeconds,
    int MazeMaxBlockedRetries,
    int MazeMaxSteps,
    int MazeMaxRefinements,
    int MazeMaxPlanContinuations,
    int MazeTimeoutSeconds,
    string PricingSnapshotId);

public static class ProductProofBenchmarkFixedConfiguration
{
    public const string HarnessVersion = "agentica-product-proof-harness-v1";
    public const string ConfigurationId = "proof-h1-gemini-developer-api-standard-gemini25flash-t0-th0-o12288-stepwise-ip1-rp1-is1-rs1-repair2-pr3-d500-3000-c600-j0-pc8-8-wrr1s24r18c6t180-mrr2s240r240c16t900";
    public const string ApiSurface = "Gemini Developer API Standard";
    public const int PlannerRepairAttempts = 2;
    public const int ProviderMaxAttempts = 3;
    public const int ProviderRetryBaseDelayMilliseconds = 500;
    public const int ProviderRetryMaxDelayMilliseconds = 3000;
    public const int ProviderCallTimeoutSeconds = 600;
    public const bool ProviderRetryJitter = false;
    public const int PlanningContextRecentObservations = 8;
    public const int PlanningContextRecentReceipts = 8;
    public const int WorkbenchMaxBlockedRetries = 1;
    public const int WorkbenchMaxSteps = 24;
    public const int WorkbenchMaxRefinements = 18;
    public const int WorkbenchMaxPlanContinuations = 6;
    public const int WorkbenchTimeoutSeconds = 180;
    public const int MazeMaxBlockedRetries = 2;
    public const int MazeMaxSteps = 240;
    public const int MazeMaxRefinements = 240;
    public const int MazeMaxPlanContinuations = 16;
    public const int MazeTimeoutSeconds = 900;

    public static ProductProofBenchmarkConfiguration Current { get; } = new(
        HarnessVersion,
        ConfigurationId,
        GeminiLlmClient.ProviderName,
        ApiSurface,
        GeminiModelId.Flash25,
        Temperature: 0,
        ThinkingBudgetTokens: LlmThinkingOptions.DisabledBudget,
        MaxOutputTokens: LlmPlannerOptions.DefaultMaxOutputTokens,
        PlanningMode.Stepwise,
        WorkflowPlanPromptBuilder.InitialPromptVersion,
        WorkflowPlanPromptBuilder.RefinementPromptVersion,
        WorkflowPlanPromptBuilder.InitialSchemaVersion,
        WorkflowPlanPromptBuilder.RefinementSchemaVersion,
        PlannerRepairAttempts,
        ProviderMaxAttempts,
        ProviderRetryBaseDelayMilliseconds,
        ProviderRetryMaxDelayMilliseconds,
        ProviderCallTimeoutSeconds,
        ProviderRetryJitter,
        PlanningContextRecentObservations,
        PlanningContextRecentReceipts,
        WorkbenchMaxBlockedRetries,
        WorkbenchMaxSteps,
        WorkbenchMaxRefinements,
        WorkbenchMaxPlanContinuations,
        WorkbenchTimeoutSeconds,
        MazeMaxBlockedRetries,
        MazeMaxSteps,
        MazeMaxRefinements,
        MazeMaxPlanContinuations,
        MazeTimeoutSeconds,
        ProductProofPricing.SnapshotId);
}

internal sealed class ProductProofBenchmarkRunner
{
    private readonly Func<ILlmClient> _providerFactory;

    public ProductProofBenchmarkRunner(Func<ILlmClient>? providerFactory = null)
    {
        _providerFactory = providerFactory ?? (() =>
            new GeminiLlmClient(GeminiClientOptions.FromEnvironment(GeminiModelId.Flash25)));
    }

    public async Task<BenchmarkRunResult> RunAsync(
        BenchmarkRunDefinition definition,
        BenchmarkCohortIdentity cohort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(cohort);

        var measuredClient = CreateMeasuredClient();
        var planner = CreatePlanner(measuredClient);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var execution = definition.Suite switch
            {
                BenchmarkSuiteKind.PrimaryWorkbench =>
                    await RunWorkbenchAsync(definition, planner, cancellationToken).ConfigureAwait(false),
                BenchmarkSuiteKind.GeneralizationHoldout =>
                    await RunMazeAsync(definition, planner, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown benchmark suite '{definition.Suite}'.")
            };

            stopwatch.Stop();
            if (execution.Exception is { } executionException)
            {
                return FromException(
                    definition,
                    cohort,
                    executionException,
                    execution.Oracle,
                    stopwatch.Elapsed,
                    measuredClient.Snapshot(),
                    cancellationToken);
            }

            return FromEnvelope(
                definition,
                cohort,
                execution.Envelope!,
                execution.Oracle,
                stopwatch.Elapsed,
                measuredClient.Snapshot());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stopwatch.Stop();
            return new BenchmarkRunResult(
                Cohort: cohort,
                RunId: definition.RunId,
                RunOutcomeStatus: ExceptionStatus(exception, cancellationToken),
                ReportedSuccess: false,
                OracleSuccess: false,
                InvalidPlan: exception is LlmPlannerException,
                Elapsed: stopwatch.Elapsed,
                RuntimeRetryCount: 0,
                OracleEvidence: null,
                OracleFailure: "benchmark_run_did_not_return_an_envelope",
                LlmCalls: measuredClient.Snapshot());
        }
    }

    private MeasuredLlmClient CreateMeasuredClient()
    {
        var retryingClient = new RetryingLlmClient(
            _providerFactory(),
            new LlmRetryOptions(
                MaxAttempts: ProductProofBenchmarkFixedConfiguration.ProviderMaxAttempts,
                BaseDelay: TimeSpan.FromMilliseconds(ProductProofBenchmarkFixedConfiguration.ProviderRetryBaseDelayMilliseconds),
                MaxDelay: TimeSpan.FromMilliseconds(ProductProofBenchmarkFixedConfiguration.ProviderRetryMaxDelayMilliseconds),
                CallTimeout: TimeSpan.FromSeconds(ProductProofBenchmarkFixedConfiguration.ProviderCallTimeoutSeconds),
                UseJitter: ProductProofBenchmarkFixedConfiguration.ProviderRetryJitter));
        return new MeasuredLlmClient(retryingClient);
    }

    private static LlmWorkflowPlanner CreatePlanner(MeasuredLlmClient client) =>
        new(
            client,
            new LlmPlannerOptions(
                ModelId: GeminiModelId.Flash25,
                GenerationOptions: new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: LlmPlannerOptions.DefaultMaxOutputTokens,
                    Thinking: LlmThinkingOptions.Off()),
                InvalidJsonRepairAttempts: ProductProofBenchmarkFixedConfiguration.PlannerRepairAttempts));

    private static async Task<ScenarioExecution> RunWorkbenchAsync(
        BenchmarkRunDefinition definition,
        LlmWorkflowPlanner planner,
        CancellationToken cancellationToken)
    {
        RequireParameter(definition, "suite", "workbench");
        RequireParameter(definition, "scenario", definition.ScenarioId);

        var scenario = new WorkbenchQuestBoard().Load(definition.ScenarioId);
        var session = new WorkbenchQuestSession(scenario);
        var objective = WorkbenchQuestCommand.BuildObjective(scenario);
        var runner = new AgenticaRunner(
            planner: planner,
            toolCatalog: WorkbenchQuestTools.CreateCatalog(session),
            eventSink: new InMemoryEventSink(),
            outcomeReporter: new WorkbenchQuestOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: ProductProofBenchmarkFixedConfiguration.WorkbenchMaxSteps,
                MaxRefinements: ProductProofBenchmarkFixedConfiguration.WorkbenchMaxRefinements,
                Timeout: TimeSpan.FromSeconds(ProductProofBenchmarkFixedConfiguration.WorkbenchTimeoutSeconds),
                PlanningMode: PlanningMode.Stepwise,
                MaxPlanContinuations: ProductProofBenchmarkFixedConfiguration.WorkbenchMaxPlanContinuations,
                PlanningContext: new PlanningContextOptions(
                    MaxRecentObservations: ProductProofBenchmarkFixedConfiguration.PlanningContextRecentObservations,
                    MaxRecentReceipts: ProductProofBenchmarkFixedConfiguration.PlanningContextRecentReceipts),
                MaxBlockedRetries: ProductProofBenchmarkFixedConfiguration.WorkbenchMaxBlockedRetries,
                SecurityPolicy: global::LabSecurityPolicy.ForPlanner(planner)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("workbench.objective_completed"));

        try
        {
            var envelope = await runner.RunAsync(
                    new RunRequest(objective, RequestOrigin.User, session.PublicSnapshot()),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ScenarioExecution(
                envelope,
                ProductProofBenchmarkOracles.Evaluate(session),
                Exception: null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ScenarioExecution(
                Envelope: null,
                ProductProofBenchmarkOracles.Evaluate(session),
                exception);
        }
    }

    private static async Task<ScenarioExecution> RunMazeAsync(
        BenchmarkRunDefinition definition,
        LlmWorkflowPlanner planner,
        CancellationToken cancellationToken)
    {
        RequireParameter(definition, "suite", "maze");
        RequireParameter(definition, "questType", "unlock");
        var seed = RequireIntParameter(definition, "seed", expected: 173);
        var width = RequireIntParameter(definition, "width", expected: 7);
        var height = RequireIntParameter(definition, "height", expected: 7);
        var visibility = RequireIntParameter(definition, "visibility", expected: 2);
        if (!string.Equals(definition.ScenarioId, "unlock", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported fixed holdout scenario '{definition.ScenarioId}'.");
        }

        var descriptor = new MazeQuestBoard().GetQuest("sun_gate_maze");
        var stage = new MazeQuestGenerator().Generate(
            descriptor,
            new MazeQuestGenerationOptions(
                QuestId: descriptor.QuestId,
                Seed: seed,
                Width: width,
                Height: height,
                VisibilityRadius: visibility,
                QuestType: MazeQuestArchetype.Unlock));
        var session = new MazeQuestSession(stage);
        var objective = MazeQuestCommand.BuildObjective(stage);
        var initialState = session.CurrentRunState;
        var plannerContext = MazeQuestCapabilitySurfaceCompiler.BuildPlannerContext(
            stage,
            initialState,
            objective);
        var runner = new AgenticaRunner(
            planner: planner,
            toolCatalog: MazeQuestTools.CreateCatalog(session),
            eventSink: new InMemoryEventSink(),
            outcomeReporter: new MazeQuestOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: ProductProofBenchmarkFixedConfiguration.MazeMaxSteps,
                MaxRefinements: ProductProofBenchmarkFixedConfiguration.MazeMaxRefinements,
                Timeout: TimeSpan.FromSeconds(ProductProofBenchmarkFixedConfiguration.MazeTimeoutSeconds),
                PlanningMode: PlanningMode.Stepwise,
                MaxPlanContinuations: ProductProofBenchmarkFixedConfiguration.MazeMaxPlanContinuations,
                PlanningContext: new PlanningContextOptions(
                    MaxRecentObservations: ProductProofBenchmarkFixedConfiguration.PlanningContextRecentObservations,
                    MaxRecentReceipts: ProductProofBenchmarkFixedConfiguration.PlanningContextRecentReceipts),
                MaxBlockedRetries: ProductProofBenchmarkFixedConfiguration.MazeMaxBlockedRetries,
                SecurityPolicy: global::LabSecurityPolicy.ForPlanner(planner)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("mazequest.objective_completed"),
            planningFrameProjector: new MazeQuestCockpitFrameProjector(session),
            userFacingReasonProjector: MazeQuestUserFacingReasonProjector.Instance);

        try
        {
            var envelope = await runner.RunAsync(
                    new RunRequest(objective, RequestOrigin.User, plannerContext),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ScenarioExecution(
                envelope,
                ProductProofBenchmarkOracles.Evaluate(session),
                Exception: null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ScenarioExecution(
                Envelope: null,
                ProductProofBenchmarkOracles.Evaluate(session),
                exception);
        }
    }

    private static BenchmarkRunResult FromEnvelope(
        BenchmarkRunDefinition definition,
        BenchmarkCohortIdentity cohort,
        OutcomeEnvelope envelope,
        ProductProofOracleResult oracle,
        TimeSpan elapsed,
        IReadOnlyList<BenchmarkLlmCallTelemetry> calls)
    {
        var allAttempts = envelope.PriorAttempts.Append(envelope).ToArray();
        return new BenchmarkRunResult(
            Cohort: cohort,
            RunId: definition.RunId,
            RunOutcomeStatus: envelope.Outcome.Status.ToString(),
            ReportedSuccess: envelope.Outcome.Status == RunOutcomeStatus.Succeeded,
            OracleSuccess: oracle.Success,
            InvalidPlan: allAttempts.Any(item => item.Outcome.Status == RunOutcomeStatus.PlanInvalid),
            Elapsed: elapsed,
            RuntimeRetryCount: envelope.PriorAttempts.Count,
            OracleEvidence: oracle.Evidence,
            OracleFailure: oracle.Failure,
            LlmCalls: calls);
    }

    private static BenchmarkRunResult FromException(
        BenchmarkRunDefinition definition,
        BenchmarkCohortIdentity cohort,
        Exception exception,
        ProductProofOracleResult oracle,
        TimeSpan elapsed,
        IReadOnlyList<BenchmarkLlmCallTelemetry> calls,
        CancellationToken cancellationToken) =>
        new(
            Cohort: cohort,
            RunId: definition.RunId,
            RunOutcomeStatus: ExceptionStatus(exception, cancellationToken),
            ReportedSuccess: false,
            OracleSuccess: oracle.Success,
            InvalidPlan: exception is LlmPlannerException,
            Elapsed: elapsed,
            RuntimeRetryCount: 0,
            OracleEvidence: oracle.Evidence,
            OracleFailure: oracle.Failure,
            LlmCalls: calls);

    private static void RequireParameter(
        BenchmarkRunDefinition definition,
        string key,
        string expected)
    {
        if (!definition.Parameters.TryGetValue(key, out var value) ||
            !string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Benchmark run '{definition.RunId}' requires fixed parameter {key}={expected}.");
        }
    }

    private static int RequireIntParameter(
        BenchmarkRunDefinition definition,
        string key,
        int expected)
    {
        RequireParameter(definition, key, expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return expected;
    }

    private static string ExceptionStatus(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? RunOutcomeStatus.Cancelled.ToString()
            : $"HarnessException:{exception.GetType().Name}";

    private sealed record ScenarioExecution(
        OutcomeEnvelope? Envelope,
        ProductProofOracleResult Oracle,
        Exception? Exception);
}

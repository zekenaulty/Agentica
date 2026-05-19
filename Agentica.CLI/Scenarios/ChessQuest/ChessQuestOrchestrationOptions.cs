using Agentica.Execution;

namespace Agentica.CLI.Scenarios.ChessQuest;

internal sealed record ChessQuestOrchestrationOptions(
    string ScenarioId,
    PlannerKind TaskPlanner,
    PlannerKind RunPlanner,
    string? TaskModelId,
    string? RunModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    int? MaxOutputTokens,
    int TimeoutSeconds,
    int PhaseMaxAgentTurns,
    int MaxOrchestrationRuns,
    int MaxOrchestrationRefinements,
    int MaxGraphMutations,
    int MaxSteps,
    int MaxRefinements,
    int MaxPlanContinuations,
    int MaxBlockedRetries,
    ChessQuestOpponentMode? OpponentMode,
    string OpponentDifficulty,
    int OpponentSeed,
    bool Quiet,
    bool VerboseEvents,
    bool TurnJson,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static ChessQuestOrchestrationOptions Parse(IReadOnlyList<string> args)
    {
        string? scenarioId = null;
        var taskPlanner = PlannerKind.Deterministic;
        var runPlanner = PlannerKind.Deterministic;
        string? taskModelId = null;
        string? runModelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        int? maxOutputTokens = null;
        var timeoutSeconds = 300;
        var phaseMaxAgentTurns = 2;
        var maxOrchestrationRuns = 3;
        var maxOrchestrationRefinements = 3;
        var maxGraphMutations = 4;
        var maxSteps = 96;
        var maxRefinements = 72;
        var maxPlanContinuations = 8;
        var maxBlockedRetries = 1;
        ChessQuestOpponentMode? opponentMode = null;
        var opponentDifficulty = "club";
        var opponentSeed = 1;
        var quiet = false;
        var verboseEvents = false;
        var turnJson = false;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (scenarioId is not null)
                {
                    return Invalid($"Unexpected chessquest orchestration argument '{arg}'.");
                }

                scenarioId = arg;
                continue;
            }

            switch (arg)
            {
                case "--task-planner":
                    if (!TryReadPlanner(args, ref index, out taskPlanner, out var taskPlannerError))
                    {
                        return Invalid(taskPlannerError);
                    }

                    break;

                case "--run-planner":
                case "--planner":
                    if (!TryReadPlanner(args, ref index, out runPlanner, out var runPlannerError))
                    {
                        return Invalid(runPlannerError);
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out var modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    taskModelId = modelId;
                    runModelId = modelId;
                    break;

                case "--task-model":
                    if (!TryReadValue(args, ref index, out taskModelId))
                    {
                        return Invalid("Missing value for --task-model.");
                    }

                    break;

                case "--run-model":
                    if (!TryReadValue(args, ref index, out runModelId))
                    {
                        return Invalid("Missing value for --run-model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!TryReadValue(args, ref index, out thinkingBudget))
                    {
                        return Invalid("Missing value for --thinking-budget.");
                    }

                    thinkingBudget = thinkingBudget.ToLowerInvariant();
                    if (thinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(thinkingBudget, out var tokens) || tokens < 0))
                    {
                        return Invalid($"Invalid thinking budget '{thinkingBudget}'.");
                    }

                    break;

                case "--include-thoughts":
                    includeThoughts = true;
                    break;

                case "--max-output-tokens":
                    if (!TryReadPositiveInt(args, ref index, out var parsedMaxOutputTokens))
                    {
                        return Invalid("Missing or invalid value for --max-output-tokens.");
                    }

                    maxOutputTokens = parsedMaxOutputTokens;
                    break;

                case "--timeout-seconds":
                    if (!TryReadPositiveInt(args, ref index, out timeoutSeconds))
                    {
                        return Invalid("Missing or invalid value for --timeout-seconds.");
                    }

                    break;

                case "--phase-max-agent-turns":
                    if (!TryReadPositiveInt(args, ref index, out phaseMaxAgentTurns))
                    {
                        return Invalid("Missing or invalid value for --phase-max-agent-turns.");
                    }

                    break;

                case "--max-orchestration-runs":
                    if (!TryReadPositiveInt(args, ref index, out maxOrchestrationRuns))
                    {
                        return Invalid("Missing or invalid value for --max-orchestration-runs.");
                    }

                    break;

                case "--max-orchestration-refinements":
                    if (!TryReadNonNegativeInt(args, ref index, out maxOrchestrationRefinements))
                    {
                        return Invalid("Missing or invalid value for --max-orchestration-refinements.");
                    }

                    break;

                case "--max-graph-mutations":
                    if (!TryReadPositiveInt(args, ref index, out maxGraphMutations))
                    {
                        return Invalid("Missing or invalid value for --max-graph-mutations.");
                    }

                    break;

                case "--max-steps":
                    if (!TryReadPositiveInt(args, ref index, out maxSteps))
                    {
                        return Invalid("Missing or invalid value for --max-steps.");
                    }

                    break;

                case "--max-refinements":
                    if (!TryReadNonNegativeInt(args, ref index, out maxRefinements))
                    {
                        return Invalid("Missing or invalid value for --max-refinements.");
                    }

                    break;

                case "--max-plan-continuations":
                    if (!TryReadNonNegativeInt(args, ref index, out maxPlanContinuations))
                    {
                        return Invalid("Missing or invalid value for --max-plan-continuations.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!TryReadNonNegativeInt(args, ref index, out maxBlockedRetries))
                    {
                        return Invalid("Missing or invalid value for --max-blocked-retries.");
                    }

                    break;

                case "--opponent":
                    if (!TryReadValue(args, ref index, out var opponentValue) ||
                        !Enum.TryParse<ChessQuestOpponentMode>(opponentValue, ignoreCase: true, out var parsedOpponentMode))
                    {
                        return Invalid("Missing or invalid value for --opponent.");
                    }

                    opponentMode = parsedOpponentMode;
                    break;

                case "--opponent-difficulty":
                    if (!TryReadValue(args, ref index, out opponentDifficulty) ||
                        string.IsNullOrWhiteSpace(opponentDifficulty))
                    {
                        return Invalid("Missing value for --opponent-difficulty.");
                    }

                    opponentDifficulty = opponentDifficulty.Trim().ToLowerInvariant();
                    break;

                case "--opponent-seed":
                    if (!TryReadInt(args, ref index, out opponentSeed))
                    {
                        return Invalid("Missing or invalid value for --opponent-seed.");
                    }

                    break;

                case "--quiet":
                    quiet = true;
                    break;

                case "--verbose-events":
                    verboseEvents = true;
                    break;

                case "--turn-json":
                    turnJson = true;
                    break;

                case "--log-run":
                    logRun = true;
                    break;

                case "--log-dir":
                    if (!TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.");
                    }

                    break;

                default:
                    return Invalid($"Unknown chessquest orchestration option '{arg}'.");
            }
        }

        return new ChessQuestOrchestrationOptions(
            scenarioId ?? ChessQuestBoard.DefaultScenarioId,
            taskPlanner,
            runPlanner,
            taskModelId,
            runModelId,
            thinkingBudget,
            includeThoughts,
            maxOutputTokens,
            timeoutSeconds,
            phaseMaxAgentTurns,
            maxOrchestrationRuns,
            maxOrchestrationRefinements,
            maxGraphMutations,
            maxSteps,
            maxRefinements,
            maxPlanContinuations,
            maxBlockedRetries,
            opponentMode,
            opponentDifficulty,
            opponentSeed,
            quiet,
            verboseEvents,
            turnJson,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    public ChessQuestRunOptions ToRunOptions() =>
        new(
            ScenarioId,
            RunPlanner,
            RunModelId,
            ThinkingBudget,
            IncludeThoughts,
            MaxOutputTokens,
            PlanningMode.QueryAndBlockerDriven,
            MaxBlockedRetries,
            TimeoutSeconds,
            MaxSteps,
            MaxRefinements,
            MaxPlanContinuations,
            OpponentMode,
            OpponentDifficulty,
            OpponentPlanner: null,
            OpponentModelId: null,
            OpponentThinkingBudget: null,
            OpponentIncludeThoughts: false,
            OpponentMaxOutputTokens: null,
            OpponentTimeoutSeconds: 120,
            OpponentMaxSteps: 8,
            OpponentMaxRefinements: 8,
            OpponentMaxPlanContinuations: 2,
            OpponentSeed,
            Quiet,
            VerboseEvents,
            TurnJson,
            VerboseEnvelope: false,
            StrategyMode: ChessQuestStrategyMode.Off,
            Phase: "opening",
            PhaseMaxAgentTurns,
            LogRun,
            LogDir,
            IsValid: true,
            Error: null);

    private static bool TryReadPlanner(
        IReadOnlyList<string> args,
        ref int index,
        out PlannerKind planner,
        out string error)
    {
        if (!TryReadValue(args, ref index, out var plannerValue))
        {
            planner = PlannerKind.Deterministic;
            error = "Missing planner value.";
            return false;
        }

        if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
        {
            error = $"Unknown planner '{plannerValue}'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryReadPositiveInt(
        IReadOnlyList<string> args,
        ref int index,
        out int value) =>
        TryReadInt(args, ref index, out value) && value > 0;

    private static bool TryReadNonNegativeInt(
        IReadOnlyList<string> args,
        ref int index,
        out int value) =>
        TryReadInt(args, ref index, out value) && value >= 0;

    private static bool TryReadInt(
        IReadOnlyList<string> args,
        ref int index,
        out int value)
    {
        if (!TryReadValue(args, ref index, out var raw) ||
            !int.TryParse(raw, out value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static ChessQuestOrchestrationOptions Invalid(string error) =>
        new(
            ChessQuestBoard.DefaultScenarioId,
            PlannerKind.Deterministic,
            PlannerKind.Deterministic,
            null,
            null,
            null,
            false,
            null,
            TimeoutSeconds: 300,
            PhaseMaxAgentTurns: 2,
            MaxOrchestrationRuns: 3,
            MaxOrchestrationRefinements: 3,
            MaxGraphMutations: 4,
            MaxSteps: 96,
            MaxRefinements: 72,
            MaxPlanContinuations: 8,
            MaxBlockedRetries: 1,
            OpponentMode: null,
            OpponentDifficulty: "club",
            OpponentSeed: 1,
            Quiet: false,
            VerboseEvents: false,
            TurnJson: false,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}

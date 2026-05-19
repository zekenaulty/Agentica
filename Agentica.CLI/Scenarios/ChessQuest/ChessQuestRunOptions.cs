using Agentica.Execution;

namespace Agentica.CLI.Scenarios.ChessQuest;

internal sealed record ChessQuestRunOptions(
    string ScenarioId,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    int? MaxOutputTokens,
    PlanningMode PlanningMode,
    int MaxBlockedRetries,
    int TimeoutSeconds,
    int MaxSteps,
    int MaxRefinements,
    int MaxPlanContinuations,
    ChessQuestOpponentMode? OpponentMode,
    string OpponentDifficulty,
    PlannerKind? OpponentPlanner,
    string? OpponentModelId,
    string? OpponentThinkingBudget,
    bool OpponentIncludeThoughts,
    int? OpponentMaxOutputTokens,
    int OpponentTimeoutSeconds,
    int OpponentMaxSteps,
    int OpponentMaxRefinements,
    int OpponentMaxPlanContinuations,
    int OpponentSeed,
    bool Quiet,
    bool VerboseEvents,
    bool TurnJson,
    bool VerboseEnvelope,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static ChessQuestRunOptions Parse(IReadOnlyList<string> args)
    {
        string? scenarioId = null;
        var planner = PlannerKind.Deterministic;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        int? maxOutputTokens = null;
        var planningMode = PlanningMode.Stepwise;
        var maxBlockedRetries = 1;
        var timeoutSeconds = 180;
        var maxSteps = 96;
        var maxRefinements = 72;
        var maxPlanContinuations = 8;
        ChessQuestOpponentMode? opponentMode = null;
        var opponentDifficulty = "club";
        PlannerKind? opponentPlanner = null;
        string? opponentModelId = null;
        string? opponentThinkingBudget = null;
        var opponentIncludeThoughts = false;
        int? opponentMaxOutputTokens = null;
        var opponentTimeoutSeconds = 120;
        var opponentMaxSteps = 8;
        var opponentMaxRefinements = 8;
        var opponentMaxPlanContinuations = 2;
        var opponentSeed = 1;
        var quiet = false;
        var verboseEvents = false;
        var turnJson = false;
        var verboseEnvelope = false;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (scenarioId is not null)
                {
                    return Invalid($"Unexpected chessquest argument '{arg}'.");
                }

                scenarioId = arg;
                continue;
            }

            switch (arg)
            {
                case "--planner":
                    if (!TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
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
                    if (!TryReadValue(args, ref index, out var maxOutputTokensValue) ||
                        !int.TryParse(maxOutputTokensValue, out var parsedMaxOutputTokens) ||
                        parsedMaxOutputTokens <= 0)
                    {
                        return Invalid("Missing or invalid value for --max-output-tokens.");
                    }

                    maxOutputTokens = parsedMaxOutputTokens;
                    break;

                case "--planning-mode":
                    if (!TryReadValue(args, ref index, out var planningModeValue))
                    {
                        return Invalid("Missing value for --planning-mode.");
                    }

                    if (!CliParsing.TryParsePlanningMode(planningModeValue, out planningMode))
                    {
                        return Invalid($"Unknown planning mode '{planningModeValue}'.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!TryReadValue(args, ref index, out var maxBlockedRetriesValue) ||
                        !int.TryParse(maxBlockedRetriesValue, out maxBlockedRetries) ||
                        maxBlockedRetries < 0)
                    {
                        return Invalid("Missing or invalid value for --max-blocked-retries.");
                    }

                    break;

                case "--timeout-seconds":
                    if (!TryReadValue(args, ref index, out var timeoutSecondsValue) ||
                        !int.TryParse(timeoutSecondsValue, out timeoutSeconds) ||
                        timeoutSeconds <= 0)
                    {
                        return Invalid("Missing or invalid value for --timeout-seconds.");
                    }

                    break;

                case "--max-steps":
                    if (!TryReadValue(args, ref index, out var maxStepsValue) ||
                        !int.TryParse(maxStepsValue, out maxSteps) ||
                        maxSteps <= 0)
                    {
                        return Invalid("Missing or invalid value for --max-steps.");
                    }

                    break;

                case "--max-refinements":
                    if (!TryReadValue(args, ref index, out var maxRefinementsValue) ||
                        !int.TryParse(maxRefinementsValue, out maxRefinements) ||
                        maxRefinements < 0)
                    {
                        return Invalid("Missing or invalid value for --max-refinements.");
                    }

                    break;

                case "--max-plan-continuations":
                    if (!TryReadValue(args, ref index, out var maxPlanContinuationsValue) ||
                        !int.TryParse(maxPlanContinuationsValue, out maxPlanContinuations) ||
                        maxPlanContinuations < 0)
                    {
                        return Invalid("Missing or invalid value for --max-plan-continuations.");
                    }

                    break;

                case "--opponent":
                    if (!TryReadValue(args, ref index, out var opponentValue))
                    {
                        return Invalid("Missing value for --opponent.");
                    }

                    if (!Enum.TryParse<ChessQuestOpponentMode>(opponentValue, ignoreCase: true, out var parsedOpponentMode))
                    {
                        return Invalid($"Unknown opponent '{opponentValue}'. Expected random, heuristic, or agent.");
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

                case "--opponent-planner":
                    if (!TryReadValue(args, ref index, out var opponentPlannerValue))
                    {
                        return Invalid("Missing value for --opponent-planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(opponentPlannerValue, ignoreCase: true, out var parsedOpponentPlanner))
                    {
                        return Invalid($"Unknown opponent planner '{opponentPlannerValue}'.");
                    }

                    opponentPlanner = parsedOpponentPlanner;
                    break;

                case "--opponent-model":
                    if (!TryReadValue(args, ref index, out opponentModelId))
                    {
                        return Invalid("Missing value for --opponent-model.");
                    }

                    break;

                case "--opponent-thinking-budget":
                    if (!TryReadValue(args, ref index, out opponentThinkingBudget))
                    {
                        return Invalid("Missing value for --opponent-thinking-budget.");
                    }

                    opponentThinkingBudget = opponentThinkingBudget.ToLowerInvariant();
                    if (opponentThinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(opponentThinkingBudget, out var opponentTokens) || opponentTokens < 0))
                    {
                        return Invalid($"Invalid opponent thinking budget '{opponentThinkingBudget}'.");
                    }

                    break;

                case "--opponent-include-thoughts":
                    opponentIncludeThoughts = true;
                    break;

                case "--opponent-max-output-tokens":
                    if (!TryReadValue(args, ref index, out var opponentMaxOutputTokensValue) ||
                        !int.TryParse(opponentMaxOutputTokensValue, out var parsedOpponentMaxOutputTokens) ||
                        parsedOpponentMaxOutputTokens <= 0)
                    {
                        return Invalid("Missing or invalid value for --opponent-max-output-tokens.");
                    }

                    opponentMaxOutputTokens = parsedOpponentMaxOutputTokens;
                    break;

                case "--opponent-timeout-seconds":
                    if (!TryReadValue(args, ref index, out var opponentTimeoutSecondsValue) ||
                        !int.TryParse(opponentTimeoutSecondsValue, out opponentTimeoutSeconds) ||
                        opponentTimeoutSeconds <= 0)
                    {
                        return Invalid("Missing or invalid value for --opponent-timeout-seconds.");
                    }

                    break;

                case "--opponent-max-steps":
                    if (!TryReadValue(args, ref index, out var opponentMaxStepsValue) ||
                        !int.TryParse(opponentMaxStepsValue, out opponentMaxSteps) ||
                        opponentMaxSteps <= 0)
                    {
                        return Invalid("Missing or invalid value for --opponent-max-steps.");
                    }

                    break;

                case "--opponent-max-refinements":
                    if (!TryReadValue(args, ref index, out var opponentMaxRefinementsValue) ||
                        !int.TryParse(opponentMaxRefinementsValue, out opponentMaxRefinements) ||
                        opponentMaxRefinements < 0)
                    {
                        return Invalid("Missing or invalid value for --opponent-max-refinements.");
                    }

                    break;

                case "--opponent-max-plan-continuations":
                    if (!TryReadValue(args, ref index, out var opponentMaxPlanContinuationsValue) ||
                        !int.TryParse(opponentMaxPlanContinuationsValue, out opponentMaxPlanContinuations) ||
                        opponentMaxPlanContinuations < 0)
                    {
                        return Invalid("Missing or invalid value for --opponent-max-plan-continuations.");
                    }

                    break;

                case "--opponent-seed":
                    if (!TryReadValue(args, ref index, out var opponentSeedValue) ||
                        !int.TryParse(opponentSeedValue, out opponentSeed))
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

                case "--verbose-envelope":
                    verboseEnvelope = true;
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
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        return new ChessQuestRunOptions(
            scenarioId ?? ChessQuestBoard.DefaultScenarioId,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            maxOutputTokens,
            planningMode,
            maxBlockedRetries,
            timeoutSeconds,
            maxSteps,
            maxRefinements,
            maxPlanContinuations,
            opponentMode,
            opponentDifficulty,
            opponentPlanner,
            opponentModelId,
            opponentThinkingBudget,
            opponentIncludeThoughts,
            opponentMaxOutputTokens,
            opponentTimeoutSeconds,
            opponentMaxSteps,
            opponentMaxRefinements,
            opponentMaxPlanContinuations,
            opponentSeed,
            quiet,
            verboseEvents,
            turnJson,
            verboseEnvelope,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
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

    private static ChessQuestRunOptions Invalid(string error) =>
        new(
            ChessQuestBoard.DefaultScenarioId,
            PlannerKind.Deterministic,
            null,
            null,
            false,
            null,
            PlanningMode.Stepwise,
            MaxBlockedRetries: 1,
            TimeoutSeconds: 180,
            MaxSteps: 96,
            MaxRefinements: 72,
            MaxPlanContinuations: 8,
            OpponentMode: null,
            OpponentDifficulty: "club",
            OpponentPlanner: null,
            OpponentModelId: null,
            OpponentThinkingBudget: null,
            OpponentIncludeThoughts: false,
            OpponentMaxOutputTokens: null,
            OpponentTimeoutSeconds: 120,
            OpponentMaxSteps: 8,
            OpponentMaxRefinements: 8,
            OpponentMaxPlanContinuations: 2,
            OpponentSeed: 1,
            Quiet: false,
            VerboseEvents: false,
            TurnJson: false,
            VerboseEnvelope: false,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}

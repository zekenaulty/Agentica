using Agentica.CLI.Scenarios.MazeQuest;
using Agentica.Execution;

internal enum MazeQuestNarratorKind
{
    Deterministic,
    Gemini,
    Off
}

internal sealed record MazeQuestPreviewOptions(
    string QuestId,
    int? Seed,
    MazeQuestArchetype? QuestType,
    int Width,
    int Height,
    int VisibilityRadius,
    bool Reveal,
    bool Json,
    bool IsValid,
    string? Error)
{
    public static MazeQuestPreviewOptions Parse(IReadOnlyList<string> args)
    {
        string? questId = null;
        int? seed = null;
        MazeQuestArchetype? questType = null;
        var width = 13;
        var height = 13;
        var visibilityRadius = 2;
        var reveal = false;
        var json = false;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (questId is not null)
                {
                    return Invalid($"Unexpected maze quest argument '{arg}'.");
                }

                questId = arg;
                continue;
            }

            switch (arg)
            {
                case "--seed":
                    if (!CliOptionReader.TryReadInt(args, ref index, out var seedValue))
                    {
                        return Invalid("Missing or invalid value for --seed.");
                    }

                    seed = seedValue;
                    break;

                case "--type":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var questTypeValue))
                    {
                        return Invalid("Missing value for --type.");
                    }

                    if (!TryParseQuestType(questTypeValue, out var parsedQuestType))
                    {
                        return Invalid($"Unknown maze quest type '{questTypeValue}'.");
                    }

                    questType = parsedQuestType;
                    break;

                case "--width":
                    if (!CliOptionReader.TryReadInt(args, ref index, out width))
                    {
                        return Invalid("Missing or invalid value for --width.");
                    }

                    break;

                case "--height":
                    if (!CliOptionReader.TryReadInt(args, ref index, out height))
                    {
                        return Invalid("Missing or invalid value for --height.");
                    }

                    break;

                case "--visibility":
                    if (!CliOptionReader.TryReadInt(args, ref index, out visibilityRadius))
                    {
                        return Invalid("Missing or invalid value for --visibility.");
                    }

                    break;

                case "--reveal":
                    reveal = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(questId))
        {
            return Invalid("Maze quest id is required.");
        }

        return new MazeQuestPreviewOptions(
            questId,
            seed,
            questType,
            width,
            height,
            visibilityRadius,
            reveal,
            json,
            IsValid: true,
            Error: null);
    }

    public static bool TryParseQuestType(string value, out MazeQuestArchetype questType)
    {
        switch (value.ToLowerInvariant())
        {
            case "unlock":
                questType = MazeQuestArchetype.Unlock;
                return true;
            case "collect":
                questType = MazeQuestArchetype.Collect;
                return true;
            case "delivery":
            case "courier":
                questType = MazeQuestArchetype.Delivery;
                return true;
            case "explore":
            case "discovery":
                questType = MazeQuestArchetype.Explore;
                return true;
            case "activate":
            case "interact":
                questType = MazeQuestArchetype.Activate;
                return true;
            case "puzzle":
            case "sequence":
                questType = MazeQuestArchetype.PuzzleSequence;
                return true;
            case "rescue":
            case "retrieve":
                questType = MazeQuestArchetype.Rescue;
                return true;
            case "resource":
            case "resource-route":
                questType = MazeQuestArchetype.ResourceRoute;
                return true;
            default:
                questType = MazeQuestArchetype.Unlock;
                return false;
        }
    }

    private static MazeQuestPreviewOptions Invalid(string error) =>
        new(
            string.Empty,
            null,
            null,
            Width: 13,
            Height: 13,
            VisibilityRadius: 2,
            Reveal: false,
            Json: false,
            IsValid: false,
            Error: error);
}

internal sealed record MazeQuestRunOptions(
    string QuestId,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    PlanningMode PlanningMode,
    bool PlannerSpecified,
    int MaxBlockedRetries,
    int? Seed,
    MazeQuestArchetype? QuestType,
    int Width,
    int Height,
    int VisibilityRadius,
    bool Watch,
    MazeQuestNarratorKind Narrator,
    bool TurnJson,
    int WatchDelayMilliseconds,
    int TimeoutSeconds,
    string? NarrationModelId,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static MazeQuestRunOptions Parse(IReadOnlyList<string> args)
    {
        string? questId = null;
        var planner = PlannerKind.Deterministic;
        var plannerSpecified = false;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        var planningMode = PlanningMode.Stepwise;
        var maxBlockedRetries = 2;
        int? seed = null;
        MazeQuestArchetype? questType = null;
        var width = 13;
        var height = 13;
        var visibilityRadius = 2;
        var watch = false;
        var narrator = MazeQuestNarratorKind.Deterministic;
        var turnJson = false;
        var watchDelayMilliseconds = 0;
        var timeoutSeconds = 120;
        string? narrationModelId = null;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (questId is not null)
                {
                    return Invalid($"Unexpected maze quest argument '{arg}'.");
                }

                questId = arg;
                continue;
            }

            switch (arg)
            {
                case "--planner":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    plannerSpecified = true;
                    break;

                case "--model":
                    if (!CliOptionReader.TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    break;

                case "--narration-model":
                    if (!CliOptionReader.TryReadValue(args, ref index, out narrationModelId))
                    {
                        return Invalid("Missing value for --narration-model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!CliOptionReader.TryReadValue(args, ref index, out thinkingBudget))
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

                case "--planning-mode":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var planningModeValue))
                    {
                        return Invalid("Missing value for --planning-mode.");
                    }

                    if (!CliParsing.TryParsePlanningMode(planningModeValue, out planningMode))
                    {
                        return Invalid($"Unknown planning mode '{planningModeValue}'.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var maxBlockedRetriesValue) ||
                        !int.TryParse(maxBlockedRetriesValue, out maxBlockedRetries) ||
                        maxBlockedRetries < 0)
                    {
                        return Invalid("Missing or invalid value for --max-blocked-retries.");
                    }

                    break;

                case "--seed":
                    if (!CliOptionReader.TryReadInt(args, ref index, out var seedValue))
                    {
                        return Invalid("Missing or invalid value for --seed.");
                    }

                    seed = seedValue;
                    break;

                case "--type":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var questTypeValue))
                    {
                        return Invalid("Missing value for --type.");
                    }

                    if (!MazeQuestPreviewOptions.TryParseQuestType(questTypeValue, out var parsedQuestType))
                    {
                        return Invalid($"Unknown maze quest type '{questTypeValue}'.");
                    }

                    questType = parsedQuestType;
                    break;

                case "--width":
                    if (!CliOptionReader.TryReadInt(args, ref index, out width))
                    {
                        return Invalid("Missing or invalid value for --width.");
                    }

                    break;

                case "--height":
                    if (!CliOptionReader.TryReadInt(args, ref index, out height))
                    {
                        return Invalid("Missing or invalid value for --height.");
                    }

                    break;

                case "--visibility":
                    if (!CliOptionReader.TryReadInt(args, ref index, out visibilityRadius))
                    {
                        return Invalid("Missing or invalid value for --visibility.");
                    }

                    break;

                case "--watch":
                    watch = true;
                    break;

                case "--narrator":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var narratorValue))
                    {
                        return Invalid("Missing value for --narrator.");
                    }

                    if (!TryParseNarrator(narratorValue, out narrator))
                    {
                        return Invalid($"Unknown narrator '{narratorValue}'.");
                    }

                    break;

                case "--turn-json":
                    turnJson = true;
                    break;

                case "--watch-delay-ms":
                    if (!CliOptionReader.TryReadInt(args, ref index, out watchDelayMilliseconds) || watchDelayMilliseconds < 0)
                    {
                        return Invalid("Missing or invalid value for --watch-delay-ms.");
                    }

                    break;

                case "--timeout-seconds":
                    if (!CliOptionReader.TryReadInt(args, ref index, out timeoutSeconds) || timeoutSeconds <= 0)
                    {
                        return Invalid("Missing or invalid value for --timeout-seconds.");
                    }

                    break;

                case "--log-run":
                    logRun = true;
                    break;

                case "--log-dir":
                    if (!CliOptionReader.TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.");
                    }

                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        return new MazeQuestRunOptions(
            questId ?? string.Empty,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            planningMode,
            plannerSpecified,
            maxBlockedRetries,
            seed,
            questType,
            width,
            height,
            visibilityRadius,
            watch,
            narrator,
            turnJson,
            watchDelayMilliseconds,
            timeoutSeconds,
            narrationModelId,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    private static bool TryParseNarrator(string value, out MazeQuestNarratorKind narrator)
    {
        switch (value.ToLowerInvariant())
        {
            case "off":
            case "none":
                narrator = MazeQuestNarratorKind.Off;
                return true;
            case "deterministic":
            case "host":
                narrator = MazeQuestNarratorKind.Deterministic;
                return true;
            case "gemini":
            case "llm":
                narrator = MazeQuestNarratorKind.Gemini;
                return true;
            default:
                narrator = MazeQuestNarratorKind.Deterministic;
                return false;
        }
    }

    private static MazeQuestRunOptions Invalid(string error) =>
        new(
            string.Empty,
            PlannerKind.Deterministic,
            null,
            null,
            false,
            PlanningMode.Stepwise,
            PlannerSpecified: false,
            MaxBlockedRetries: 2,
            Seed: null,
            QuestType: null,
            Width: 13,
            Height: 13,
            VisibilityRadius: 2,
            Watch: false,
            Narrator: MazeQuestNarratorKind.Deterministic,
            TurnJson: false,
            WatchDelayMilliseconds: 0,
            TimeoutSeconds: 120,
            NarrationModelId: null,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Clients.Llm;

namespace Agentica.CLI.Scenarios.ChessQuest;

public enum ChessQuestBoardProbePresentation
{
    Ascii,
    Fen,
    Both
}

public enum ChessQuestBoardProbeTargetMode
{
    Occupied,
    Empty,
    Mixed
}

public enum ChessQuestPuzzleProbeSource
{
    BuiltIn,
    Generated,
    RandomGenerated,
    Mixed
}

public enum ChessQuestStateProbeKind
{
    Legality,
    Capture,
    Check,
    Material,
    Phase,
    Stacked
}

public sealed record ChessQuestBoardProbeOptions(
    int Trials,
    int Seed,
    int ScramblePlies,
    ChessQuestBoardProbePresentation Presentation,
    ChessQuestBoardProbeTargetMode TargetMode,
    ChessQuestPuzzleProbeSource PuzzleSource,
    ChessQuestStateProbeKind StateProbeKind,
    string ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    int MaxOutputTokens,
    int TimeoutSeconds,
    bool Json,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static ChessQuestBoardProbeOptions Parse(
        IReadOnlyList<string> args,
        string defaultModelId)
    {
        var trials = 5;
        var seed = 12345;
        var scramblePlies = 24;
        var presentation = ChessQuestBoardProbePresentation.Ascii;
        var targetMode = ChessQuestBoardProbeTargetMode.Occupied;
        var puzzleSource = ChessQuestPuzzleProbeSource.BuiltIn;
        var stateProbeKind = ChessQuestStateProbeKind.Stacked;
        var modelId = defaultModelId;
        string? thinkingBudget = "off";
        var includeThoughts = false;
        var maxOutputTokens = 512;
        var timeoutSeconds = 120;
        var json = false;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--trials":
                    if (!TryReadPositiveInt(args, ref index, out trials))
                    {
                        return Invalid("Missing or invalid value for --trials.", defaultModelId);
                    }

                    break;

                case "--seed":
                    if (!TryReadInt(args, ref index, out seed))
                    {
                        return Invalid("Missing or invalid value for --seed.", defaultModelId);
                    }

                    break;

                case "--scramble-plies":
                    if (!TryReadPositiveInt(args, ref index, out scramblePlies))
                    {
                        return Invalid("Missing or invalid value for --scramble-plies.", defaultModelId);
                    }

                    break;

                case "--presentation":
                    if (!TryReadValue(args, ref index, out var presentationValue))
                    {
                        return Invalid("Missing value for --presentation.", defaultModelId);
                    }

                    if (!TryParsePresentation(presentationValue, out presentation))
                    {
                        return Invalid($"Unknown board-probe presentation '{presentationValue}'.", defaultModelId);
                    }

                    break;

                case "--target":
                    if (!TryReadValue(args, ref index, out var targetValue))
                    {
                        return Invalid("Missing value for --target.", defaultModelId);
                    }

                    if (!TryParseTargetMode(targetValue, out targetMode))
                    {
                        return Invalid($"Unknown board-probe target '{targetValue}'.", defaultModelId);
                    }

                    break;

                case "--puzzle-source":
                    if (!TryReadValue(args, ref index, out var puzzleSourceValue))
                    {
                        return Invalid("Missing value for --puzzle-source.", defaultModelId);
                    }

                    if (!TryParsePuzzleSource(puzzleSourceValue, out puzzleSource))
                    {
                        return Invalid($"Unknown puzzle-probe source '{puzzleSourceValue}'.", defaultModelId);
                    }

                    break;

                case "--probe-kind":
                case "--kind":
                    if (!TryReadValue(args, ref index, out var stateProbeKindValue))
                    {
                        return Invalid("Missing value for --probe-kind.", defaultModelId);
                    }

                    if (!TryParseStateProbeKind(stateProbeKindValue, out stateProbeKind))
                    {
                        return Invalid($"Unknown state-probe kind '{stateProbeKindValue}'.", defaultModelId);
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.", defaultModelId);
                    }

                    break;

                case "--thinking-budget":
                    if (!TryReadValue(args, ref index, out thinkingBudget))
                    {
                        return Invalid("Missing value for --thinking-budget.", defaultModelId);
                    }

                    thinkingBudget = thinkingBudget.ToLowerInvariant();
                    if (thinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(thinkingBudget, out var tokens) || tokens < 0))
                    {
                        return Invalid($"Invalid thinking budget '{thinkingBudget}'.", defaultModelId);
                    }

                    break;

                case "--include-thoughts":
                    includeThoughts = true;
                    break;

                case "--max-output-tokens":
                    if (!TryReadPositiveInt(args, ref index, out maxOutputTokens))
                    {
                        return Invalid("Missing or invalid value for --max-output-tokens.", defaultModelId);
                    }

                    break;

                case "--timeout-seconds":
                    if (!TryReadPositiveInt(args, ref index, out timeoutSeconds))
                    {
                        return Invalid("Missing or invalid value for --timeout-seconds.", defaultModelId);
                    }

                    break;

                case "--json":
                    json = true;
                    break;

                case "--log-run":
                    logRun = true;
                    break;

                case "--log-dir":
                    if (!TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.", defaultModelId);
                    }

                    break;

                default:
                    return Invalid($"Unknown board-probe option '{arg}'.", defaultModelId);
            }
        }

        return new ChessQuestBoardProbeOptions(
            trials,
            seed,
            scramblePlies,
            presentation,
            targetMode,
            puzzleSource,
            stateProbeKind,
            modelId,
            thinkingBudget,
            includeThoughts,
            maxOutputTokens,
            timeoutSeconds,
            json,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    private static bool TryParsePresentation(
        string value,
        out ChessQuestBoardProbePresentation presentation)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "ascii":
            case "board":
                presentation = ChessQuestBoardProbePresentation.Ascii;
                return true;
            case "fen":
                presentation = ChessQuestBoardProbePresentation.Fen;
                return true;
            case "both":
                presentation = ChessQuestBoardProbePresentation.Both;
                return true;
            default:
                presentation = ChessQuestBoardProbePresentation.Ascii;
                return false;
        }
    }

    private static bool TryParseTargetMode(
        string value,
        out ChessQuestBoardProbeTargetMode targetMode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "occupied":
            case "piece":
                targetMode = ChessQuestBoardProbeTargetMode.Occupied;
                return true;
            case "empty":
                targetMode = ChessQuestBoardProbeTargetMode.Empty;
                return true;
            case "mixed":
            case "any":
                targetMode = ChessQuestBoardProbeTargetMode.Mixed;
                return true;
            default:
                targetMode = ChessQuestBoardProbeTargetMode.Occupied;
                return false;
        }
    }

    private static bool TryParsePuzzleSource(
        string value,
        out ChessQuestPuzzleProbeSource source)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "built-in":
            case "builtin":
            case "fixture":
            case "fixtures":
                source = ChessQuestPuzzleProbeSource.BuiltIn;
                return true;
            case "generated":
            case "dynamic":
                source = ChessQuestPuzzleProbeSource.Generated;
                return true;
            case "random-generated":
            case "generated-random":
            case "random-material":
                source = ChessQuestPuzzleProbeSource.RandomGenerated;
                return true;
            case "mixed":
            case "both":
                source = ChessQuestPuzzleProbeSource.Mixed;
                return true;
            default:
                source = ChessQuestPuzzleProbeSource.BuiltIn;
                return false;
        }
    }

    private static bool TryParseStateProbeKind(
        string value,
        out ChessQuestStateProbeKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "legality":
            case "legal":
            case "move-legality":
                kind = ChessQuestStateProbeKind.Legality;
                return true;
            case "capture":
            case "capture-truth":
                kind = ChessQuestStateProbeKind.Capture;
                return true;
            case "check":
            case "check-status":
                kind = ChessQuestStateProbeKind.Check;
                return true;
            case "material":
            case "material-count":
                kind = ChessQuestStateProbeKind.Material;
                return true;
            case "phase":
            case "phase-selection":
                kind = ChessQuestStateProbeKind.Phase;
                return true;
            case "stacked":
            case "all":
            case "stress":
                kind = ChessQuestStateProbeKind.Stacked;
                return true;
            default:
                kind = ChessQuestStateProbeKind.Stacked;
                return false;
        }
    }

    private static bool TryReadPositiveInt(
        IReadOnlyList<string> args,
        ref int index,
        out int value) =>
        TryReadInt(args, ref index, out value) && value > 0;

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

    private static ChessQuestBoardProbeOptions Invalid(
        string error,
        string defaultModelId) =>
        new(
            Trials: 0,
            Seed: 0,
            ScramblePlies: 0,
            Presentation: ChessQuestBoardProbePresentation.Ascii,
            TargetMode: ChessQuestBoardProbeTargetMode.Occupied,
            PuzzleSource: ChessQuestPuzzleProbeSource.BuiltIn,
            StateProbeKind: ChessQuestStateProbeKind.Stacked,
            ModelId: defaultModelId,
            ThinkingBudget: null,
            IncludeThoughts: false,
            MaxOutputTokens: 0,
            TimeoutSeconds: 0,
            Json: false,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}

public sealed record ChessQuestBoardProbeTrial(
    int TrialNumber,
    int Seed,
    string Fen,
    IReadOnlyList<string> BoardLines,
    string Square,
    ChessQuestBoardProbeExpected Expected);

public sealed record ChessQuestBoardProbeExpected(
    string Square,
    bool Occupied,
    string Color,
    string Piece,
    char FenSymbol);

public sealed record ChessQuestBoardProbeAnswer(
    string Square,
    bool Occupied,
    string Color,
    string Piece);

public sealed record ChessQuestBoardProbeTrialResult(
    int TrialNumber,
    bool Passed,
    string Square,
    ChessQuestBoardProbeExpected Expected,
    ChessQuestBoardProbeAnswer? Answer,
    string RawResponse,
    string? FailureReason,
    string? ProviderName = null,
    string? ResponseModelId = null,
    LlmFinishReason FinishReason = LlmFinishReason.Unknown,
    LlmUsage? Usage = null,
    IReadOnlyDictionary<string, string>? ResponseMetadata = null);

public sealed record ChessQuestBoardProbeSummary(
    int Trials,
    int Passed,
    int Failed,
    int Seed,
    int ScramblePlies,
    ChessQuestBoardProbePresentation Presentation,
    ChessQuestBoardProbeTargetMode TargetMode,
    string ModelId,
    IReadOnlyList<ChessQuestBoardProbeTrialResult> Results);

public sealed class ChessQuestBoardProbeRunner
{
    internal const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILlmClient _client;

    public ChessQuestBoardProbeRunner(ILlmClient client)
    {
        _client = client;
    }

    public async Task<ChessQuestBoardProbeSummary> RunAsync(
        ChessQuestBoardProbeOptions options,
        Action<ChessQuestBoardProbeTrial, ChessQuestBoardProbeTrialResult>? onTrialCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ChessQuestBoardProbeTrialResult>(options.Trials);

        for (var index = 0; index < options.Trials; index++)
        {
            var trial = CreateTrial(
                seed: unchecked(options.Seed + index * 7919),
                trialNumber: index + 1,
                scramblePlies: options.ScramblePlies,
                targetMode: options.TargetMode);

            var result = await RunTrialAsync(trial, options, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onTrialCompleted?.Invoke(trial, result);
        }

        return new ChessQuestBoardProbeSummary(
            Trials: results.Count,
            Passed: results.Count(result => result.Passed),
            Failed: results.Count(result => !result.Passed),
            Seed: options.Seed,
            ScramblePlies: options.ScramblePlies,
            Presentation: options.Presentation,
            TargetMode: options.TargetMode,
            ModelId: options.ModelId,
            Results: results);
    }

    public static ChessQuestBoardProbeTrial CreateTrial(
        int seed,
        int trialNumber,
        int scramblePlies,
        ChessQuestBoardProbeTargetMode targetMode)
    {
        var random = new Random(seed);
        var rules = new GeraChessRulesEngine(StartFen);
        for (var ply = 0; ply < scramblePlies; ply++)
        {
            var scrambleLegalMoves = rules.ListLegalMoves();
            if (scrambleLegalMoves.Count == 0 || rules.GetState().IsTerminal)
            {
                break;
            }

            var move = scrambleLegalMoves[random.Next(scrambleLegalMoves.Count)].Uci;
            var result = rules.TryPlayMove(move);
            if (!result.Accepted)
            {
                break;
            }
        }

        var fen = rules.GetFen();
        var square = ChooseTargetSquare(fen, random, targetMode);
        var expected = ExpectedAt(fen, square);

        return new ChessQuestBoardProbeTrial(
            TrialNumber: trialNumber,
            Seed: seed,
            Fen: fen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(fen),
            Square: square,
            Expected: expected);
    }

    public static ChessQuestBoardProbeExpected ExpectedAt(
        string fen,
        string square)
    {
        var symbol = FenSymbolAt(fen, square);
        if (symbol == '.')
        {
            return new ChessQuestBoardProbeExpected(
                Square: square.ToLowerInvariant(),
                Occupied: false,
                Color: "none",
                Piece: "empty",
                FenSymbol: '.');
        }

        return new ChessQuestBoardProbeExpected(
            Square: square.ToLowerInvariant(),
            Occupied: true,
            Color: char.IsUpper(symbol) ? "white" : "black",
            Piece: PieceName(symbol),
            FenSymbol: symbol);
    }

    public static ChessQuestBoardProbeTrialResult Validate(
        ChessQuestBoardProbeTrial trial,
        string rawResponse)
    {
        ChessQuestBoardProbeAnswer? answer;
        try
        {
            var json = ExtractJson(rawResponse);
            answer = JsonSerializer.Deserialize<ChessQuestBoardProbeAnswer>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Failure(trial, rawResponse, $"invalid_json: {exception.Message}");
        }

        if (answer is null)
        {
            return Failure(trial, rawResponse, "empty_answer");
        }

        var expected = trial.Expected;
        var normalized = Normalize(answer);
        var errors = new List<string>();

        if (!string.Equals(normalized.Square, expected.Square, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"square expected {expected.Square} got {normalized.Square}");
        }

        if (normalized.Occupied != expected.Occupied)
        {
            errors.Add($"occupied expected {expected.Occupied} got {normalized.Occupied}");
        }

        if (!string.Equals(normalized.Color, expected.Color, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"color expected {expected.Color} got {normalized.Color}");
        }

        if (!string.Equals(normalized.Piece, expected.Piece, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"piece expected {expected.Piece} got {normalized.Piece}");
        }

        return new ChessQuestBoardProbeTrialResult(
            TrialNumber: trial.TrialNumber,
            Passed: errors.Count == 0,
            Square: expected.Square,
            Expected: expected,
            Answer: normalized,
            RawResponse: rawResponse,
            FailureReason: errors.Count == 0 ? null : string.Join("; ", errors));
    }

    public static string BuildPrompt(
        ChessQuestBoardProbeTrial trial,
        ChessQuestBoardProbePresentation presentation)
    {
        var boardSection = presentation is ChessQuestBoardProbePresentation.Ascii or ChessQuestBoardProbePresentation.Both
            ? $"""

            ASCII board:
            {string.Join(Environment.NewLine, trial.BoardLines)}
            """
            : string.Empty;

        var fenSection = presentation is ChessQuestBoardProbePresentation.Fen or ChessQuestBoardProbePresentation.Both
            ? $"""

            FEN:
            {trial.Fen}
            """
            : string.Empty;

        return
            $$"""
            Identify the chess piece on square {{trial.Square}}.
            {{boardSection}}
            {{fenSection}}

            Return JSON only with this shape:
            {
              "square": "{{trial.Square}}",
              "occupied": true or false,
              "color": "white", "black", or "none",
              "piece": "king", "queen", "rook", "bishop", "knight", "pawn", or "empty"
            }
            """;
    }

    public static string SerializeSummary(ChessQuestBoardProbeSummary summary) =>
        JsonSerializer.Serialize(summary, JsonOptions);

    private async Task<ChessQuestBoardProbeTrialResult> RunTrialAsync(
        ChessQuestBoardProbeTrial trial,
        ChessQuestBoardProbeOptions options,
        CancellationToken cancellationToken)
    {
        var request = new LlmRequest(
            options.ModelId,
            [
                new LlmMessage(
                    LlmMessageRole.System,
                    """
                    You are being tested for chess board-state parsing. Answer only the requested JSON object.
                    Coordinates use files a-h from left to right and ranks 8 down to 1 from top to bottom.
                    In the ASCII board, uppercase letters are White pieces, lowercase letters are Black pieces, and "." is empty.
                    Piece letters are K king, Q queen, R rook, B bishop, N knight, and P pawn.
                    Do not infer from opening theory. Read the supplied board state.
                    """),
                new LlmMessage(
                    LlmMessageRole.User,
                    BuildPrompt(trial, options.Presentation))
            ],
            GenerationOptions: new LlmGenerationOptions(
                Temperature: 0,
                MaxOutputTokens: options.MaxOutputTokens,
                Thinking: ToThinkingOptions(options.ThinkingBudget, options.IncludeThoughts)),
            StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: AnswerJsonSchema));

        var response = await _client.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        return Validate(trial, response.StructuredJson ?? response.Text) with
        {
            ProviderName = response.ProviderName,
            ResponseModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ResponseMetadata = response.Metadata
        };
    }

    private static ChessQuestBoardProbeAnswer Normalize(ChessQuestBoardProbeAnswer answer)
    {
        var piece = NormalizePiece(answer.Piece);
        var occupied = answer.Occupied || piece is not "empty";
        var color = NormalizeColor(answer.Color, occupied);

        if (!occupied)
        {
            color = "none";
            piece = "empty";
        }

        return answer with
        {
            Square = answer.Square.Trim().ToLowerInvariant(),
            Occupied = occupied,
            Color = color,
            Piece = piece
        };
    }

    private static string NormalizePiece(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "none" or "no piece" or "empty square" => "empty",
            "k" => "king",
            "q" => "queen",
            "r" => "rook",
            "b" => "bishop",
            "n" => "knight",
            "p" => "pawn",
            _ => normalized
        };
    }

    private static string NormalizeColor(
        string value,
        bool occupied)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (!occupied)
        {
            return "none";
        }

        return normalized switch
        {
            "" or "none" or "empty" => "none",
            _ => normalized
        };
    }

    private static ChessQuestBoardProbeTrialResult Failure(
        ChessQuestBoardProbeTrial trial,
        string rawResponse,
        string reason) =>
        new(
            TrialNumber: trial.TrialNumber,
            Passed: false,
            Square: trial.Expected.Square,
            Expected: trial.Expected,
            Answer: null,
            RawResponse: rawResponse,
            FailureReason: reason);

    internal static string ExtractJson(string rawResponse)
    {
        var trimmed = rawResponse.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    private static string ChooseTargetSquare(
        string fen,
        Random random,
        ChessQuestBoardProbeTargetMode targetMode)
    {
        var squares = AllSquares().ToArray();
        var candidates = targetMode switch
        {
            ChessQuestBoardProbeTargetMode.Occupied =>
                squares.Where(square => FenSymbolAt(fen, square) != '.').ToArray(),
            ChessQuestBoardProbeTargetMode.Empty =>
                squares.Where(square => FenSymbolAt(fen, square) == '.').ToArray(),
            _ => squares
        };

        if (candidates.Length == 0)
        {
            candidates = squares;
        }

        return candidates[random.Next(candidates.Length)];
    }

    private static IEnumerable<string> AllSquares()
    {
        for (var rank = 1; rank <= 8; rank++)
        {
            for (var file = 'a'; file <= 'h'; file++)
            {
                yield return $"{file}{rank}";
            }
        }
    }

    private static char FenSymbolAt(
        string fen,
        string square)
    {
        var normalized = square.Trim().ToLowerInvariant();
        if (normalized.Length != 2 ||
            normalized[0] is < 'a' or > 'h' ||
            normalized[1] is < '1' or > '8')
        {
            throw new ArgumentException($"Invalid square '{square}'.", nameof(square));
        }

        var targetFile = normalized[0] - 'a';
        var targetRank = normalized[1] - '0';
        var placement = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var ranks = placement.Split('/');
        if (ranks.Length != 8)
        {
            throw new ArgumentException($"Invalid FEN '{fen}'.", nameof(fen));
        }

        var fenRankIndex = 8 - targetRank;
        var file = 0;
        foreach (var ch in ranks[fenRankIndex])
        {
            if (char.IsDigit(ch))
            {
                var empties = ch - '0';
                if (targetFile >= file && targetFile < file + empties)
                {
                    return '.';
                }

                file += empties;
                continue;
            }

            if (file == targetFile)
            {
                return ch;
            }

            file++;
        }

        return '.';
    }

    private static string PieceName(char fenSymbol) =>
        char.ToLowerInvariant(fenSymbol) switch
        {
            'k' => "king",
            'q' => "queen",
            'r' => "rook",
            'b' => "bishop",
            'n' => "knight",
            'p' => "pawn",
            _ => "empty"
        };

    internal static LlmThinkingOptions? ToThinkingOptions(
        string? thinkingBudget,
        bool includeThoughts) =>
        thinkingBudget switch
        {
            null when includeThoughts => new LlmThinkingOptions(IncludeThoughts: true),
            null => null,
            "dynamic" => LlmThinkingOptions.Dynamic(includeThoughts),
            "off" => LlmThinkingOptions.Off(includeThoughts),
            "0" => LlmThinkingOptions.Off(includeThoughts),
            var value when int.TryParse(value, out var tokens) && tokens > 0 =>
                LlmThinkingOptions.Budget(tokens, includeThoughts),
            _ => throw new InvalidOperationException($"Invalid thinking budget '{thinkingBudget}'.")
        };

    private const string AnswerJsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "square": {
              "type": "string"
            },
            "occupied": {
              "type": "boolean"
            },
            "color": {
              "type": "string",
              "enum": ["white", "black", "none"]
            },
            "piece": {
              "type": "string",
              "enum": ["king", "queen", "rook", "bishop", "knight", "pawn", "empty"]
            }
          },
          "required": ["square", "occupied", "color", "piece"]
        }
        """;
}

public sealed record ChessQuestMoveProbeAnswer(
    string Move,
    string? PublicReason = null,
    string? OriginSquare = null,
    string? DestinationSquare = null,
    string? Piece = null);

public sealed record ChessQuestLegalActionProbeTrial(
    int TrialNumber,
    int Seed,
    string Fen,
    IReadOnlyList<string> BoardLines,
    ChessQuestColor SideToMove,
    IReadOnlyList<string> LegalMoves);

public sealed record ChessQuestLegalActionProbeTrialResult(
    int TrialNumber,
    bool Passed,
    string? Move,
    string RawResponse,
    string? FailureReason,
    string? ProviderName = null,
    string? ResponseModelId = null,
    LlmFinishReason FinishReason = LlmFinishReason.Unknown,
    LlmUsage? Usage = null,
    IReadOnlyDictionary<string, string>? ResponseMetadata = null);

public sealed record ChessQuestLegalActionProbeSummary(
    int Trials,
    int Passed,
    int Failed,
    int Seed,
    int ScramblePlies,
    ChessQuestBoardProbePresentation Presentation,
    string ModelId,
    IReadOnlyList<ChessQuestLegalActionProbeTrialResult> Results);

public sealed class ChessQuestLegalActionProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILlmClient _client;

    public ChessQuestLegalActionProbeRunner(ILlmClient client)
    {
        _client = client;
    }

    public async Task<ChessQuestLegalActionProbeSummary> RunAsync(
        ChessQuestBoardProbeOptions options,
        Action<ChessQuestLegalActionProbeTrial, ChessQuestLegalActionProbeTrialResult>? onTrialCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ChessQuestLegalActionProbeTrialResult>(options.Trials);
        for (var index = 0; index < options.Trials; index++)
        {
            var trial = CreateTrial(
                unchecked(options.Seed + index * 7919),
                index + 1,
                options.ScramblePlies);
            var result = await RunTrialAsync(trial, options, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onTrialCompleted?.Invoke(trial, result);
        }

        return new ChessQuestLegalActionProbeSummary(
            Trials: results.Count,
            Passed: results.Count(result => result.Passed),
            Failed: results.Count(result => !result.Passed),
            Seed: options.Seed,
            ScramblePlies: options.ScramblePlies,
            Presentation: options.Presentation,
            ModelId: options.ModelId,
            Results: results);
    }

    public static ChessQuestLegalActionProbeTrial CreateTrial(
        int seed,
        int trialNumber,
        int scramblePlies)
    {
        var random = new Random(seed);
        var rules = new GeraChessRulesEngine(ChessQuestBoardProbeRunner.StartFen);
        for (var ply = 0; ply < scramblePlies; ply++)
        {
            var scrambleLegalMoves = rules.ListLegalMoves();
            if (scrambleLegalMoves.Count == 0 || rules.GetState().IsTerminal)
            {
                break;
            }

            var result = rules.TryPlayMove(scrambleLegalMoves[random.Next(scrambleLegalMoves.Count)].Uci);
            if (!result.Accepted)
            {
                break;
            }
        }

        var state = rules.GetState();
        return new ChessQuestLegalActionProbeTrial(
            TrialNumber: trialNumber,
            Seed: seed,
            Fen: rules.GetFen(),
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(rules.GetFen()),
            SideToMove: state.SideToMove,
            LegalMoves: rules.ListLegalMoves().Select(move => move.Uci).ToArray());
    }

    public static string BuildPrompt(
        ChessQuestLegalActionProbeTrial trial,
        ChessQuestBoardProbePresentation presentation)
    {
        var ownPieceInventory = BuildPieceInventory(trial.Fen, trial.SideToMove);
        var allPieceInventory = BuildPieceInventory(trial.Fen);
        var boardSection = presentation is ChessQuestBoardProbePresentation.Ascii or ChessQuestBoardProbePresentation.Both
            ? $"""

            ASCII board:
            {string.Join(Environment.NewLine, trial.BoardLines)}
            """
            : string.Empty;

        var fenSection = presentation is ChessQuestBoardProbePresentation.Fen or ChessQuestBoardProbePresentation.Both
            ? $"""

            FEN:
            {trial.Fen}
            """
            : string.Empty;

        return
            $$"""
            You are {{trial.SideToMove}} to move. Choose one valid legal move from the supplied current board.
            You are not given the legal move list. The host will verify legality after your answer.
            This is a scrambled game position, not necessarily a normal opening setup.
            Ground the answer in the board: identify the side to move, choose an origin square currently occupied by one of that side's pieces, consider how that exact piece legally moves, and account for check restrictions.
            Your candidate move's origin square must appear in this current-piece list:
            {{ownPieceInventory}}
            Current public piece inventory for both sides:
            {{allPieceInventory}}
            Do not use a default opening move unless it is legal in this exact position.
            Do not assume the king is on its starting square. Do not move from an empty square or from a square occupied by the opponent.
            Do not land on a square occupied by your own piece.
            Do not choose a move whose destination square contains the opposing king; kings are checked or checkmated, never captured.
            For knight moves, the knight may jump over pieces, but the destination square still cannot contain your own piece.
            For rooks, bishops, and queens, every square between origin and destination must be empty.
            This probe grades legal move validity, not strategic quality. Any legal move passes.
            Prefer an obvious simple legal move over an ambitious attack you cannot fully verify from the board. Avoid long queen, rook, or bishop moves unless every intervening square is visibly empty.
            Castling is a special legal move. If FEN castling rights are not shown, do not infer castling rights from the board diagram alone; choose a normal non-castling move.
            The "move" value must be coordinate UCI only: origin square followed by destination square, plus a promotion letter only when promoting.
            Do not use SAN, piece letters, capture markers, check symbols, or checkmate symbols.
            {{boardSection}}
            {{fenSection}}

            Return JSON only with fields:
            - originSquare: the occupied square of the piece you are moving
            - piece: the piece type on originSquare
            - destinationSquare: the destination square
            - move: one coordinate UCI move string
            - publicReason: one short public reason grounded in the current board; do not describe the final move as illegal
            """;
    }

    public static ChessQuestLegalActionProbeTrialResult Validate(
        ChessQuestLegalActionProbeTrial trial,
        string rawResponse)
    {
        ChessQuestMoveProbeAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<ChessQuestMoveProbeAnswer>(
                ChessQuestBoardProbeRunner.ExtractJson(rawResponse),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return Failure(trial, rawResponse, $"invalid_json: {exception.Message}");
        }

        if (answer is null || string.IsNullOrWhiteSpace(answer.Move))
        {
            return Failure(trial, rawResponse, "empty_move");
        }

        var move = answer.Move.Trim().ToLowerInvariant();
        if (!IsCoordinateUci(move))
        {
            return Failure(trial, rawResponse, $"invalid_uci_format: '{move}' is not coordinate UCI");
        }

        if (!string.IsNullOrWhiteSpace(answer.OriginSquare) &&
            !string.Equals(answer.OriginSquare.Trim(), move[..2], StringComparison.OrdinalIgnoreCase))
        {
            return Failure(trial, rawResponse, $"origin_mismatch: originSquare '{answer.OriginSquare}' does not match move origin '{move[..2]}'");
        }

        if (!string.IsNullOrWhiteSpace(answer.DestinationSquare) &&
            !string.Equals(answer.DestinationSquare.Trim(), move.Substring(2, 2), StringComparison.OrdinalIgnoreCase))
        {
            return Failure(trial, rawResponse, $"destination_mismatch: destinationSquare '{answer.DestinationSquare}' does not match move destination '{move.Substring(2, 2)}'");
        }

        if (TryGetPieceAt(trial.Fen, move[..2]) is not { } originPiece ||
            originPiece.Color != trial.SideToMove)
        {
            return Failure(trial, rawResponse, $"origin_square_not_owned: '{move[..2]}' is not occupied by a {trial.SideToMove} piece");
        }

        var destinationPiece = TryGetPieceAt(trial.Fen, move.Substring(2, 2));
        if (destinationPiece is not null && destinationPiece.Color == trial.SideToMove)
        {
            return Failure(trial, rawResponse, $"destination_occupied_by_own_piece: '{move.Substring(2, 2)}' is occupied by a {trial.SideToMove} piece");
        }

        if (destinationPiece is { Name: "king" })
        {
            return Failure(trial, rawResponse, $"destination_is_opponent_king: '{move.Substring(2, 2)}' contains the opposing king");
        }

        if (TryGetSlidingPathBlock(trial.Fen, move) is { } blockedSquare)
        {
            return Failure(trial, rawResponse, $"path_blocked_by_piece: sliding move '{move}' is blocked at '{blockedSquare}'");
        }

        var passed = trial.LegalMoves.Contains(move, StringComparer.Ordinal);
        return new ChessQuestLegalActionProbeTrialResult(
            TrialNumber: trial.TrialNumber,
            Passed: passed,
            Move: move,
            RawResponse: rawResponse,
            FailureReason: passed ? null : $"illegal_move: '{move}' is not legal for {trial.SideToMove} in the supplied position");
    }

    public static string SerializeSummary(ChessQuestLegalActionProbeSummary summary) =>
        JsonSerializer.Serialize(summary, JsonOptions);

    private async Task<ChessQuestLegalActionProbeTrialResult> RunTrialAsync(
        ChessQuestLegalActionProbeTrial trial,
        ChessQuestBoardProbeOptions options,
        CancellationToken cancellationToken)
    {
        var response = await _client.GenerateAsync(new LlmRequest(
                options.ModelId,
                [
                    new LlmMessage(
                        LlmMessageRole.System,
                        """
                        You are being tested as a chess actor. Solve from the supplied public board state only.
                        Choose one legal coordinate-UCI move for the side to move. Do not ask for or assume a legal move list.
                        Read the current board instead of using opening-pattern, king-safety, or castling defaults. Return only the requested JSON object.
                        """),
                    new LlmMessage(
                        LlmMessageRole.User,
                        BuildPrompt(trial, options.Presentation))
                ],
                GenerationOptions: new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: options.MaxOutputTokens,
                    Thinking: ChessQuestBoardProbeRunner.ToThinkingOptions(options.ThinkingBudget, options.IncludeThoughts)),
                StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: MoveAnswerJsonSchema)),
            cancellationToken).ConfigureAwait(false);

        return Validate(trial, response.StructuredJson ?? response.Text) with
        {
            ProviderName = response.ProviderName,
            ResponseModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ResponseMetadata = response.Metadata
        };
    }

    private static ChessQuestLegalActionProbeTrialResult Failure(
        ChessQuestLegalActionProbeTrial trial,
        string rawResponse,
        string reason) =>
        new(
            TrialNumber: trial.TrialNumber,
            Passed: false,
            Move: null,
            RawResponse: rawResponse,
            FailureReason: reason);

    internal const string MoveAnswerJsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "originSquare": { "type": "string" },
            "piece": { "type": "string" },
            "destinationSquare": { "type": "string" },
            "move": { "type": "string" },
            "publicReason": { "type": "string" }
          },
          "required": ["originSquare", "piece", "destinationSquare", "move"]
        }
        """;

    internal static bool IsCoordinateUci(string move)
    {
        var normalized = move.Trim().ToLowerInvariant();
        if (normalized.Length is not 4 and not 5)
        {
            return false;
        }

        if (normalized[0] is < 'a' or > 'h' ||
            normalized[2] is < 'a' or > 'h' ||
            normalized[1] is < '1' or > '8' ||
            normalized[3] is < '1' or > '8')
        {
            return false;
        }

        return normalized.Length == 4 ||
            normalized[4] is 'q' or 'r' or 'b' or 'n';
    }

    internal static string BuildPieceInventory(string fen, ChessQuestColor? colorFilter = null)
    {
        var pieces = EnumeratePieces(fen)
            .Where(piece => colorFilter is null || piece.Color == colorFilter)
            .GroupBy(piece => piece.Color)
            .OrderBy(group => group.Key == ChessQuestColor.White ? 0 : 1)
            .Select(group =>
            {
                var items = group
                    .GroupBy(piece => piece.Name)
                    .OrderBy(grouped => PieceSortKey(grouped.Key))
                    .Select(grouped => $"{Pluralize(grouped.Key)} {string.Join(",", grouped.Select(piece => piece.Square))}");
                return $"{group.Key}: {string.Join("; ", items)}";
            });

        return string.Join(Environment.NewLine, pieces);
    }

    internal static ChessQuestProbePiece? TryGetPieceAt(string fen, string square) =>
        EnumeratePieces(fen).FirstOrDefault(piece => string.Equals(piece.Square, square, StringComparison.OrdinalIgnoreCase));

    internal static string? TryGetSlidingPathBlock(string fen, string move)
    {
        if (move.Length < 4 || TryGetPieceAt(fen, move[..2]) is not { } originPiece)
        {
            return null;
        }

        if (originPiece.Name is not ("queen" or "rook" or "bishop"))
        {
            return null;
        }

        var fromFile = move[0] - 'a';
        var fromRank = move[1] - '1';
        var toFile = move[2] - 'a';
        var toRank = move[3] - '1';
        var fileDelta = toFile - fromFile;
        var rankDelta = toRank - fromRank;
        var straight = fileDelta == 0 || rankDelta == 0;
        var diagonal = Math.Abs(fileDelta) == Math.Abs(rankDelta);

        var validSlidingGeometry = originPiece.Name switch
        {
            "rook" => straight,
            "bishop" => diagonal,
            "queen" => straight || diagonal,
            _ => false
        };

        if (!validSlidingGeometry)
        {
            return null;
        }

        var fileStep = Math.Sign(fileDelta);
        var rankStep = Math.Sign(rankDelta);
        var file = fromFile + fileStep;
        var rank = fromRank + rankStep;
        while (file != toFile || rank != toRank)
        {
            var square = $"{(char)('a' + file)}{rank + 1}";
            if (TryGetPieceAt(fen, square) is not null)
            {
                return square;
            }

            file += fileStep;
            rank += rankStep;
        }

        return null;
    }

    internal static IReadOnlyList<ChessQuestProbePiece> EnumeratePieces(string fen)
    {
        var board = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var pieces = new List<ChessQuestProbePiece>();
        var rank = 8;
        var file = 0;
        foreach (var symbol in board)
        {
            if (symbol == '/')
            {
                rank--;
                file = 0;
                continue;
            }

            if (char.IsDigit(symbol))
            {
                file += symbol - '0';
                continue;
            }

            var color = char.IsUpper(symbol) ? ChessQuestColor.White : ChessQuestColor.Black;
            var square = $"{(char)('a' + file)}{rank}";
            pieces.Add(new ChessQuestProbePiece(color, PieceName(symbol), square));
            file++;
        }

        return pieces;
    }

    private static string PieceName(char symbol) =>
        char.ToLowerInvariant(symbol) switch
        {
            'k' => "king",
            'q' => "queen",
            'r' => "rook",
            'b' => "bishop",
            'n' => "knight",
            'p' => "pawn",
            _ => "piece"
        };

    private static int PieceSortKey(string piece) =>
        piece switch
        {
            "king" => 0,
            "queen" => 1,
            "rook" => 2,
            "bishop" => 3,
            "knight" => 4,
            "pawn" => 5,
            _ => 6
        };

    private static string Pluralize(string piece) =>
        piece == "king" ? "king" :
        piece == "queen" ? "queen" :
        piece + "s";
}

internal sealed record ChessQuestProbePiece(
    ChessQuestColor Color,
    string Name,
    string Square);

public sealed record ChessQuestStateProbeTrial(
    int TrialNumber,
    int Seed,
    ChessQuestStateProbeKind Kind,
    string Fen,
    IReadOnlyList<string> BoardLines,
    ChessQuestColor SideToMove,
    int Ply,
    string LegalityMove,
    bool LegalityExpected,
    string CaptureMove,
    bool CaptureExpected,
    string CapturedPieceExpected,
    bool SideToMoveInCheckExpected,
    int WhiteMaterialExpected,
    int BlackMaterialExpected,
    int MaterialDeltaForSideToMoveExpected,
    string PhaseExpected,
    IReadOnlyList<string> LegalMoves);

public sealed class ChessQuestStateProbeAnswer
{
    public bool? IsLegal { get; set; }
    public bool? IsCapture { get; set; }
    public string? CapturedPiece { get; set; }
    public bool? SideToMoveInCheck { get; set; }
    public int? WhiteMaterial { get; set; }
    public int? BlackMaterial { get; set; }
    public int? MaterialDeltaForSideToMove { get; set; }
    public string? Phase { get; set; }
    public string? PublicReason { get; set; }
    public ChessQuestStateProbeAnswer? Legality { get; set; }
    public ChessQuestStateProbeAnswer? Capture { get; set; }
    public ChessQuestStateProbeAnswer? Check { get; set; }
    public ChessQuestStateProbeAnswer? Material { get; set; }
    public ChessQuestStateProbeAnswer? PhaseSelection { get; set; }
}

public sealed record ChessQuestStateProbeTrialResult(
    int TrialNumber,
    ChessQuestStateProbeKind Kind,
    bool Passed,
    string RawResponse,
    IReadOnlyList<string> FailureReasons,
    string? ProviderName = null,
    string? ResponseModelId = null,
    LlmFinishReason FinishReason = LlmFinishReason.Unknown,
    LlmUsage? Usage = null,
    IReadOnlyDictionary<string, string>? ResponseMetadata = null);

public sealed record ChessQuestStateProbeSummary(
    int Trials,
    int Passed,
    int Failed,
    int Seed,
    int ScramblePlies,
    ChessQuestStateProbeKind Kind,
    ChessQuestBoardProbePresentation Presentation,
    string ModelId,
    IReadOnlyList<ChessQuestStateProbeTrialResult> Results);

public sealed class ChessQuestStateProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILlmClient _client;

    public ChessQuestStateProbeRunner(ILlmClient client)
    {
        _client = client;
    }

    public async Task<ChessQuestStateProbeSummary> RunAsync(
        ChessQuestBoardProbeOptions options,
        Action<ChessQuestStateProbeTrial, ChessQuestStateProbeTrialResult>? onTrialCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ChessQuestStateProbeTrialResult>(options.Trials);
        for (var index = 0; index < options.Trials; index++)
        {
            var trial = CreateTrial(
                unchecked(options.Seed + index * 7_919),
                index + 1,
                options.ScramblePlies,
                options.StateProbeKind);
            var result = await RunTrialAsync(trial, options, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onTrialCompleted?.Invoke(trial, result);
        }

        return new ChessQuestStateProbeSummary(
            Trials: results.Count,
            Passed: results.Count(result => result.Passed),
            Failed: results.Count(result => !result.Passed),
            Seed: options.Seed,
            ScramblePlies: options.ScramblePlies,
            Kind: options.StateProbeKind,
            Presentation: options.Presentation,
            ModelId: options.ModelId,
            Results: results);
    }

    public static ChessQuestStateProbeTrial CreateTrial(
        int seed,
        int trialNumber,
        int scramblePlies,
        ChessQuestStateProbeKind kind)
    {
        var random = new Random(seed);
        var rules = new GeraChessRulesEngine(ChessQuestBoardProbeRunner.StartFen);
        for (var ply = 0; ply < scramblePlies; ply++)
        {
            var scrambleLegalMoves = rules.ListLegalMoves();
            if (scrambleLegalMoves.Count == 0 || rules.GetState().IsTerminal)
            {
                break;
            }

            var result = rules.TryPlayMove(scrambleLegalMoves[random.Next(scrambleLegalMoves.Count)].Uci);
            if (!result.Accepted)
            {
                break;
            }
        }

        var state = rules.GetState();
        if (state.IsTerminal || rules.ListLegalMoves().Count == 0)
        {
            return CreateTrial(seed + 1, trialNumber, Math.Max(4, scramblePlies - 1), kind);
        }

        var fen = rules.GetFen();
        var legalMoves = rules.ListLegalMoves().Select(move => move.Uci).ToArray();
        var legalExpected = random.Next(2) == 0;
        var legalityMove = legalExpected
            ? legalMoves[random.Next(legalMoves.Length)]
            : CreateIllegalCoordinateMove(fen, state.SideToMove, legalMoves, random);
        legalExpected = legalMoves.Contains(legalityMove, StringComparer.Ordinal);

        var captureMove = ChooseCaptureProbeMove(fen, legalMoves, random);
        var capture = CaptureForMove(fen, captureMove);
        var material = MaterialTotals(fen);
        var delta = state.SideToMove == ChessQuestColor.White
            ? material.White - material.Black
            : material.Black - material.White;
        var sideToMoveInCheck = rules.IsKingInCheck(state.SideToMove);
        var phase = ExpectedPhase(
            state.Ply,
            sideToMoveInCheck,
            delta,
            legalMoves.Any(move => CaptureForMove(fen, move) is not null));

        return new ChessQuestStateProbeTrial(
            TrialNumber: trialNumber,
            Seed: seed,
            Kind: kind,
            Fen: fen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(fen),
            SideToMove: state.SideToMove,
            Ply: state.Ply,
            LegalityMove: legalityMove,
            LegalityExpected: legalExpected,
            CaptureMove: captureMove,
            CaptureExpected: capture is not null,
            CapturedPieceExpected: capture?.Piece ?? "none",
            SideToMoveInCheckExpected: sideToMoveInCheck,
            WhiteMaterialExpected: material.White,
            BlackMaterialExpected: material.Black,
            MaterialDeltaForSideToMoveExpected: delta,
            PhaseExpected: phase,
            LegalMoves: legalMoves);
    }

    public static string BuildPrompt(
        ChessQuestStateProbeTrial trial,
        ChessQuestBoardProbePresentation presentation)
    {
        var pieceInventory = ChessQuestLegalActionProbeRunner.BuildPieceInventory(trial.Fen);
        var boardSection = presentation is ChessQuestBoardProbePresentation.Ascii or ChessQuestBoardProbePresentation.Both
            ? $"""

            ASCII board:
            {string.Join(Environment.NewLine, trial.BoardLines)}
            """
            : string.Empty;

        var fenSection = presentation is ChessQuestBoardProbePresentation.Fen or ChessQuestBoardProbePresentation.Both
            ? $"""

            FEN:
            {trial.Fen}
            """
            : string.Empty;

        var shared =
            $$"""
            You are being tested on public chess state reasoning from one current board.
            Do not use opening defaults. Do not assume a legal move list unless it is explicitly supplied; it is not supplied here.
            Coordinate UCI means origin square followed by destination square, plus a promotion letter only when promoting.
            Material points are queen=9, rook=5, bishop=3, knight=3, pawn=1, king=0.
            Phase choices are opening, tactical, defense, recovery, conversion, or endgame.
            Phase selection is a context-engineering test: choose defense when the side to move is in check or materially behind, opening for early development, tactical for immediate forcing/capture opportunities, and conversion only with a real advantage.

            Side to move: {{trial.SideToMove}}
            Ply: {{trial.Ply}}
            Current public piece inventory:
            {{pieceInventory}}
            {{boardSection}}
            {{fenSection}}
            """;

        return trial.Kind switch
        {
            ChessQuestStateProbeKind.Legality =>
                shared +
                $$"""

                Question: Is proposed move {{trial.LegalityMove}} legal for {{trial.SideToMove}} in this exact position?
                Return JSON only with fields:
                - isLegal: true or false
                - publicReason: one short board-grounded reason
                """,
            ChessQuestStateProbeKind.Capture =>
                shared +
                $$"""

                Question: Does proposed move {{trial.CaptureMove}} capture a piece in this exact position?
                Return JSON only with fields:
                - isCapture: true or false
                - capturedPiece: "white_pawn", "black_queen", etc., or "none"
                - publicReason: one short board-grounded reason
                """,
            ChessQuestStateProbeKind.Check =>
                shared +
                """

                Question: Is the side to move currently in check?
                Return JSON only with fields:
                - sideToMoveInCheck: true or false
                - publicReason: one short board-grounded reason
                """,
            ChessQuestStateProbeKind.Material =>
                shared +
                """

                Question: Count current material points, excluding kings.
                Return JSON only with fields:
                - whiteMaterial: integer
                - blackMaterial: integer
                - materialDeltaForSideToMove: integer, side-to-move material minus opponent material
                - publicReason: one short board-grounded reason
                """,
            ChessQuestStateProbeKind.Phase =>
                shared +
                """

                Question: Choose the best current phase label for the side to move.
                Return JSON only with fields:
                - phase: one of "opening", "tactical", "defense", "recovery", "conversion", "endgame"
                - publicReason: one short reason using only public state
                """,
            _ =>
                shared +
                $$"""

                Answer all checks from this same board without changing the position:
                1. Is proposed move {{trial.LegalityMove}} legal for {{trial.SideToMove}}?
                2. Does proposed move {{trial.CaptureMove}} capture a piece, and if so what piece?
                3. Is the side to move currently in check?
                4. What are the material point totals, excluding kings?
                5. Which phase label best fits the current side to move?

                Return JSON only with fields:
                - legality: object with isLegal and publicReason
                - capture: object with isCapture, capturedPiece, and publicReason
                - check: object with sideToMoveInCheck and publicReason
                - material: object with whiteMaterial, blackMaterial, materialDeltaForSideToMove, and publicReason
                - phaseSelection: object with phase and publicReason
                """
        };
    }

    public static ChessQuestStateProbeTrialResult Validate(
        ChessQuestStateProbeTrial trial,
        string rawResponse)
    {
        ChessQuestStateProbeAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<ChessQuestStateProbeAnswer>(
                ChessQuestBoardProbeRunner.ExtractJson(rawResponse),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return Failure(trial, rawResponse, $"invalid_json: {exception.Message}");
        }

        if (answer is null)
        {
            return Failure(trial, rawResponse, "empty_answer");
        }

        var failures = new List<string>();
        if (trial.Kind is ChessQuestStateProbeKind.Legality or ChessQuestStateProbeKind.Stacked)
        {
            var value = answer.Legality?.IsLegal ?? answer.IsLegal;
            if (value is null)
            {
                failures.Add("missing_isLegal");
            }
            else if (value.Value != trial.LegalityExpected)
            {
                failures.Add($"legality expected {trial.LegalityExpected} for {trial.LegalityMove} got {value.Value}");
            }
        }

        if (trial.Kind is ChessQuestStateProbeKind.Capture or ChessQuestStateProbeKind.Stacked)
        {
            var captureAnswer = answer.Capture ?? answer;
            if (captureAnswer.IsCapture is null)
            {
                failures.Add("missing_isCapture");
            }
            else if (captureAnswer.IsCapture.Value != trial.CaptureExpected)
            {
                failures.Add($"capture expected {trial.CaptureExpected} for {trial.CaptureMove} got {captureAnswer.IsCapture.Value}");
            }

            var capturedPiece = NormalizeCapturedPiece(captureAnswer.CapturedPiece);
            if (trial.CaptureExpected && !CapturedPieceMatches(capturedPiece, trial.CapturedPieceExpected))
            {
                failures.Add($"capturedPiece expected {trial.CapturedPieceExpected} got {capturedPiece}");
            }

            if (!trial.CaptureExpected && capturedPiece is not "none")
            {
                failures.Add($"capturedPiece expected none got {capturedPiece}");
            }
        }

        if (trial.Kind is ChessQuestStateProbeKind.Check or ChessQuestStateProbeKind.Stacked)
        {
            var value = answer.Check?.SideToMoveInCheck ?? answer.SideToMoveInCheck;
            if (value is null)
            {
                failures.Add("missing_sideToMoveInCheck");
            }
            else if (value.Value != trial.SideToMoveInCheckExpected)
            {
                failures.Add($"sideToMoveInCheck expected {trial.SideToMoveInCheckExpected} got {value.Value}");
            }
        }

        if (trial.Kind is ChessQuestStateProbeKind.Material or ChessQuestStateProbeKind.Stacked)
        {
            var materialAnswer = answer.Material ?? answer;
            if (materialAnswer.WhiteMaterial != trial.WhiteMaterialExpected)
            {
                failures.Add($"whiteMaterial expected {trial.WhiteMaterialExpected} got {materialAnswer.WhiteMaterial?.ToString() ?? "null"}");
            }

            if (materialAnswer.BlackMaterial != trial.BlackMaterialExpected)
            {
                failures.Add($"blackMaterial expected {trial.BlackMaterialExpected} got {materialAnswer.BlackMaterial?.ToString() ?? "null"}");
            }

            if (materialAnswer.MaterialDeltaForSideToMove != trial.MaterialDeltaForSideToMoveExpected)
            {
                failures.Add($"materialDeltaForSideToMove expected {trial.MaterialDeltaForSideToMoveExpected} got {materialAnswer.MaterialDeltaForSideToMove?.ToString() ?? "null"}");
            }
        }

        if (trial.Kind is ChessQuestStateProbeKind.Phase or ChessQuestStateProbeKind.Stacked)
        {
            var phase = NormalizePhase(answer.PhaseSelection?.Phase ?? answer.Phase);
            if (string.IsNullOrWhiteSpace(phase))
            {
                failures.Add("missing_phase");
            }
            else if (!string.Equals(phase, trial.PhaseExpected, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"phase expected {trial.PhaseExpected} got {phase}");
            }
        }

        return new ChessQuestStateProbeTrialResult(
            TrialNumber: trial.TrialNumber,
            Kind: trial.Kind,
            Passed: failures.Count == 0,
            RawResponse: rawResponse,
            FailureReasons: failures);
    }

    public static string SerializeSummary(ChessQuestStateProbeSummary summary) =>
        JsonSerializer.Serialize(summary, JsonOptions);

    private async Task<ChessQuestStateProbeTrialResult> RunTrialAsync(
        ChessQuestStateProbeTrial trial,
        ChessQuestBoardProbeOptions options,
        CancellationToken cancellationToken)
    {
        var response = await _client.GenerateAsync(new LlmRequest(
                options.ModelId,
                [
                    new LlmMessage(
                        LlmMessageRole.System,
                        """
                        You are being tested on chess state reasoning from public board data.
                        Answer only the requested JSON object. Do not use hidden analysis, opening defaults, or unstated legal move lists.
                        """),
                    new LlmMessage(
                        LlmMessageRole.User,
                        BuildPrompt(trial, options.Presentation))
                ],
                GenerationOptions: new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: options.MaxOutputTokens,
                    Thinking: ChessQuestBoardProbeRunner.ToThinkingOptions(options.ThinkingBudget, options.IncludeThoughts)),
                StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: StateProbeAnswerJsonSchema(trial.Kind))),
            cancellationToken).ConfigureAwait(false);

        return Validate(trial, response.StructuredJson ?? response.Text) with
        {
            ProviderName = response.ProviderName,
            ResponseModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ResponseMetadata = response.Metadata
        };
    }

    private static ChessQuestStateProbeTrialResult Failure(
        ChessQuestStateProbeTrial trial,
        string rawResponse,
        string reason) =>
        new(
            TrialNumber: trial.TrialNumber,
            Kind: trial.Kind,
            Passed: false,
            RawResponse: rawResponse,
            FailureReasons: [reason]);

    private static string CreateIllegalCoordinateMove(
        string fen,
        ChessQuestColor sideToMove,
        IReadOnlyList<string> legalMoves,
        Random random)
    {
        var ownPieces = ChessQuestLegalActionProbeRunner.EnumeratePieces(fen)
            .Where(piece => piece.Color == sideToMove)
            .ToArray();
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var origin = ownPieces[random.Next(ownPieces.Length)].Square;
            var destination = RandomSquare(random);
            if (string.Equals(origin, destination, StringComparison.Ordinal))
            {
                continue;
            }

            var move = origin + destination;
            if (!legalMoves.Contains(move, StringComparer.Ordinal))
            {
                return move;
            }
        }

        return ownPieces[0].Square + ownPieces[0].Square;
    }

    private static string ChooseCaptureProbeMove(
        string fen,
        IReadOnlyList<string> legalMoves,
        Random random)
    {
        var captures = legalMoves
            .Where(move => CaptureForMove(fen, move) is not null)
            .ToArray();
        var nonCaptures = legalMoves
            .Where(move => CaptureForMove(fen, move) is null)
            .ToArray();

        if (captures.Length > 0 && (nonCaptures.Length == 0 || random.Next(2) == 0))
        {
            return captures[random.Next(captures.Length)];
        }

        return nonCaptures.Length > 0
            ? nonCaptures[random.Next(nonCaptures.Length)]
            : legalMoves[random.Next(legalMoves.Count)];
    }

    private static ChessProjectedCapture? CaptureForMove(
        string fen,
        string move)
    {
        var clone = new GeraChessRulesEngine(fen);
        var result = clone.TryPlayMove(move);
        return result.Accepted
            ? result.Captures.FirstOrDefault()
            : null;
    }

    private static (int White, int Black) MaterialTotals(string fen)
    {
        var white = 0;
        var black = 0;
        foreach (var piece in ChessQuestLegalActionProbeRunner.EnumeratePieces(fen))
        {
            var value = MaterialValue(piece.Name);
            if (piece.Color == ChessQuestColor.White)
            {
                white += value;
            }
            else
            {
                black += value;
            }
        }

        return (white, black);
    }

    private static int MaterialValue(string piece) =>
        piece switch
        {
            "queen" => 9,
            "rook" => 5,
            "bishop" => 3,
            "knight" => 3,
            "pawn" => 1,
            _ => 0
        };

    private static string ExpectedPhase(
        int ply,
        bool sideToMoveInCheck,
        int materialDelta,
        bool legalCaptureAvailable)
    {
        if (sideToMoveInCheck)
        {
            return "defense";
        }

        if (materialDelta <= -3)
        {
            return "recovery";
        }

        if (ply <= 20)
        {
            return "opening";
        }

        if (materialDelta >= 5)
        {
            return "conversion";
        }

        return legalCaptureAvailable ? "tactical" : "defense";
    }

    private static string NormalizeCapturedPiece(string? value)
    {
        var normalized = (value ?? "none").Trim().ToLowerInvariant();
        normalized = normalized.Replace(' ', '_').Replace("-", "_");
        return normalized switch
        {
            "" or "no" or "none" or "empty" or "no_piece" => "none",
            "pawn" => "pawn",
            "knight" => "knight",
            "bishop" => "bishop",
            "rook" => "rook",
            "queen" => "queen",
            "king" => "king",
            _ => normalized
        };
    }

    private static bool CapturedPieceMatches(
        string actual,
        string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ||
        expected.EndsWith("_" + actual, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePhase(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "defensive" => "defense",
            "stabilize" or "stabilization" => "defense",
            "counterplay" => "recovery",
            "convert" => "conversion",
            var normalized => normalized
        };

    private static string RandomSquare(Random random) =>
        $"{(char)('a' + random.Next(8))}{random.Next(1, 9)}";

    private static string StateProbeAnswerJsonSchema(ChessQuestStateProbeKind kind) =>
        kind switch
        {
            ChessQuestStateProbeKind.Legality =>
                """
                {
                  "type": "object",
                  "properties": {
                    "isLegal": { "type": "boolean" },
                    "publicReason": { "type": "string" }
                  },
                  "required": ["isLegal", "publicReason"]
                }
                """,
            ChessQuestStateProbeKind.Capture =>
                """
                {
                  "type": "object",
                  "properties": {
                    "isCapture": { "type": "boolean" },
                    "capturedPiece": { "type": "string" },
                    "publicReason": { "type": "string" }
                  },
                  "required": ["isCapture", "capturedPiece", "publicReason"]
                }
                """,
            ChessQuestStateProbeKind.Check =>
                """
                {
                  "type": "object",
                  "properties": {
                    "sideToMoveInCheck": { "type": "boolean" },
                    "publicReason": { "type": "string" }
                  },
                  "required": ["sideToMoveInCheck", "publicReason"]
                }
                """,
            ChessQuestStateProbeKind.Material =>
                """
                {
                  "type": "object",
                  "properties": {
                    "whiteMaterial": { "type": "integer" },
                    "blackMaterial": { "type": "integer" },
                    "materialDeltaForSideToMove": { "type": "integer" },
                    "publicReason": { "type": "string" }
                  },
                  "required": ["whiteMaterial", "blackMaterial", "materialDeltaForSideToMove", "publicReason"]
                }
                """,
            ChessQuestStateProbeKind.Phase =>
                """
                {
                  "type": "object",
                  "properties": {
                    "phase": { "type": "string", "enum": ["opening", "tactical", "defense", "recovery", "conversion", "endgame"] },
                    "publicReason": { "type": "string" }
                  },
                  "required": ["phase", "publicReason"]
                }
                """,
            _ => StackedStateProbeAnswerJsonSchema
        };

    private const string StackedStateProbeAnswerJsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "legality": {
              "type": "object",
              "properties": {
                "isLegal": { "type": "boolean" },
                "publicReason": { "type": "string" }
              },
              "required": ["isLegal", "publicReason"]
            },
            "capture": {
              "type": "object",
              "properties": {
                "isCapture": { "type": "boolean" },
                "capturedPiece": { "type": "string" },
                "publicReason": { "type": "string" }
              },
              "required": ["isCapture", "capturedPiece", "publicReason"]
            },
            "check": {
              "type": "object",
              "properties": {
                "sideToMoveInCheck": { "type": "boolean" },
                "publicReason": { "type": "string" }
              },
              "required": ["sideToMoveInCheck", "publicReason"]
            },
            "material": {
              "type": "object",
              "properties": {
                "whiteMaterial": { "type": "integer" },
                "blackMaterial": { "type": "integer" },
                "materialDeltaForSideToMove": { "type": "integer" },
                "publicReason": { "type": "string" }
              },
              "required": ["whiteMaterial", "blackMaterial", "materialDeltaForSideToMove", "publicReason"]
            },
            "phaseSelection": {
              "type": "object",
              "properties": {
                "phase": { "type": "string", "enum": ["opening", "tactical", "defense", "recovery", "conversion", "endgame"] },
                "publicReason": { "type": "string" }
              },
              "required": ["phase", "publicReason"]
            }
          },
          "required": ["legality", "capture", "check", "material", "phaseSelection"]
        }
        """;
}

public sealed record ChessQuestPuzzleProbeTrial(
    int TrialNumber,
    string PuzzleId,
    ChessQuestPuzzleProbeSource Source,
    string Objective,
    string Fen,
    IReadOnlyList<string> BoardLines,
    ChessQuestColor AgentColor,
    IReadOnlyList<string> AcceptedMoves,
    string? GenerationNote = null);

public sealed record ChessQuestPuzzleProbeTrialResult(
    int TrialNumber,
    string PuzzleId,
    bool Passed,
    string? Move,
    string RawResponse,
    string? FailureReason,
    string? ProviderName = null,
    string? ResponseModelId = null,
    LlmFinishReason FinishReason = LlmFinishReason.Unknown,
    LlmUsage? Usage = null,
    IReadOnlyDictionary<string, string>? ResponseMetadata = null);

public sealed record ChessQuestPuzzleProbeSummary(
    int Trials,
    int Passed,
    int Failed,
    ChessQuestBoardProbePresentation Presentation,
    string ModelId,
    IReadOnlyList<ChessQuestPuzzleProbeTrialResult> Results);

public sealed class ChessQuestPuzzleProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly ChessQuestPuzzleProbeTrial[] BuiltInPuzzles =
    [
        new(
            TrialNumber: 1,
            PuzzleId: "fools_mate_black_mate_in_one",
            Source: ChessQuestPuzzleProbeSource.BuiltIn,
            Objective: "Find the single coordinate-UCI move for Black that checkmates White.",
            Fen: "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2",
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen("rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2"),
            AgentColor: ChessQuestColor.Black,
            AcceptedMoves: ["d8h4"]),
        new(
            TrialNumber: 1,
            PuzzleId: "white_king_captures_queen",
            Source: ChessQuestPuzzleProbeSource.BuiltIn,
            Objective: "Find the only coordinate-UCI move for White that captures the undefended black queen.",
            Fen: "4k3/8/8/8/8/8/4q3/4K3 w - - 0 1",
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen("4k3/8/8/8/8/8/4q3/4K3 w - - 0 1"),
            AgentColor: ChessQuestColor.White,
            AcceptedMoves: ["e1e2"]),
        new(
            TrialNumber: 1,
            PuzzleId: "white_promotes_to_queen",
            Source: ChessQuestPuzzleProbeSource.BuiltIn,
            Objective: "Find the coordinate-UCI move for White that promotes the pawn to a queen.",
            Fen: "4k3/P7/8/8/8/8/8/4K3 w - - 0 1",
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen("4k3/P7/8/8/8/8/8/4K3 w - - 0 1"),
            AgentColor: ChessQuestColor.White,
            AcceptedMoves: ["a7a8q"]),
        new(
            TrialNumber: 1,
            PuzzleId: "black_rook_captures_queen",
            Source: ChessQuestPuzzleProbeSource.BuiltIn,
            Objective: "Find the only coordinate-UCI move for Black that captures the undefended white queen.",
            Fen: "r3k3/8/8/8/8/8/8/Q3K3 b - - 0 1",
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen("r3k3/8/8/8/8/8/8/Q3K3 b - - 0 1"),
            AgentColor: ChessQuestColor.Black,
            AcceptedMoves: ["a8a1"]),
        new(
            TrialNumber: 1,
            PuzzleId: "black_promotes_to_queen",
            Source: ChessQuestPuzzleProbeSource.BuiltIn,
            Objective: "Find the coordinate-UCI move for Black that promotes the pawn to a queen.",
            Fen: "4k3/8/8/8/8/8/p7/4K3 b - - 0 1",
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen("4k3/8/8/8/8/8/p7/4K3 b - - 0 1"),
            AgentColor: ChessQuestColor.Black,
            AcceptedMoves: ["a2a1q"])
    ];

    private readonly ILlmClient _client;

    public ChessQuestPuzzleProbeRunner(ILlmClient client)
    {
        _client = client;
    }

    public async Task<ChessQuestPuzzleProbeSummary> RunAsync(
        ChessQuestBoardProbeOptions options,
        Action<ChessQuestPuzzleProbeTrial, ChessQuestPuzzleProbeTrialResult>? onTrialCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ChessQuestPuzzleProbeTrialResult>(options.Trials);
        for (var index = 0; index < options.Trials; index++)
        {
            var puzzle = CreatePuzzle(options, index);
            var result = await RunTrialAsync(puzzle, options, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onTrialCompleted?.Invoke(puzzle, result);
        }

        return new ChessQuestPuzzleProbeSummary(
            Trials: results.Count,
            Passed: results.Count(result => result.Passed),
            Failed: results.Count(result => !result.Passed),
            Presentation: options.Presentation,
            ModelId: options.ModelId,
            Results: results);
    }

    internal static ChessQuestPuzzleProbeTrial CreatePuzzle(
        ChessQuestBoardProbeOptions options,
        int zeroBasedIndex)
    {
        var trialNumber = zeroBasedIndex + 1;
        return options.PuzzleSource switch
        {
            ChessQuestPuzzleProbeSource.Generated => CreateGeneratedPuzzle(
                trialNumber,
                options.Seed + zeroBasedIndex * 7_919,
                options.ScramblePlies),
            ChessQuestPuzzleProbeSource.RandomGenerated => CreateGeneratedRandomMaterialPuzzle(
                trialNumber,
                options.Seed + zeroBasedIndex * 7_919,
                options.ScramblePlies),
            ChessQuestPuzzleProbeSource.Mixed when zeroBasedIndex % 2 == 0 => CreateGeneratedPuzzle(
                trialNumber,
                options.Seed + zeroBasedIndex * 7_919,
                options.ScramblePlies),
            _ => BuiltInPuzzles[zeroBasedIndex % BuiltInPuzzles.Length] with
            {
                TrialNumber = trialNumber
            }
        };
    }

    public static string BuildPrompt(
        ChessQuestPuzzleProbeTrial trial,
        ChessQuestBoardProbePresentation presentation)
    {
        var pieceInventory = ChessQuestLegalActionProbeRunner.BuildPieceInventory(trial.Fen);
        var answerCardinality = trial.AcceptedMoves.Count == 1
            ? "There is exactly one accepted answer for this probe."
            : "There is an accepted best-move span for this probe; return any one top-scoring move that satisfies the objective.";
        var boardSection = presentation is ChessQuestBoardProbePresentation.Ascii or ChessQuestBoardProbePresentation.Both
            ? $"""

            ASCII board:
            {string.Join(Environment.NewLine, trial.BoardLines)}
            """
            : string.Empty;

        var fenSection = presentation is ChessQuestBoardProbePresentation.Fen or ChessQuestBoardProbePresentation.Both
            ? $"""

            FEN:
            {trial.Fen}
            """
            : string.Empty;

        return
            $$"""
            Puzzle: {{trial.PuzzleId}}
            Role: {{trial.AgentColor}}
            Objective: {{trial.Objective}}
            {{answerCardinality}}
            Solve the puzzle from the board state. Identify the moving side, select an origin square currently occupied by that side, and aim at the stated objective.
            For checkmate objectives, inspect candidate checking moves and the opponent king's legal escapes, captures, and blocks.
            Current public piece inventory:
            {{pieceInventory}}
            Return the best move that satisfies the objective, not a random legal move.
            The returned JSON must describe your final chosen answer, not a rejected candidate.
            The "move" value must be coordinate UCI only: origin square followed by destination square, plus a promotion letter only when promoting.
            Do not use SAN, piece letters, capture markers, check symbols, or checkmate symbols.
            If you intend a queen, rook, bishop, knight, king, or pawn move, encode the origin and destination squares, not the piece name.
            {{boardSection}}
            {{fenSection}}

            Return JSON only with fields:
            - originSquare: the occupied square of the piece you are moving
            - piece: the piece type on originSquare
            - destinationSquare: the destination square
            - move: the single coordinate UCI move that solves the puzzle
            - publicReason: one short public reason grounded in the current board
            """;
    }

    public static ChessQuestPuzzleProbeTrialResult Validate(
        ChessQuestPuzzleProbeTrial trial,
        string rawResponse)
    {
        ChessQuestMoveProbeAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<ChessQuestMoveProbeAnswer>(
                ChessQuestBoardProbeRunner.ExtractJson(rawResponse),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return Failure(trial, rawResponse, $"invalid_json: {exception.Message}");
        }

        if (answer is null || string.IsNullOrWhiteSpace(answer.Move))
        {
            return Failure(trial, rawResponse, "empty_move");
        }

        var move = answer.Move.Trim().ToLowerInvariant();
        if (!ChessQuestLegalActionProbeRunner.IsCoordinateUci(move))
        {
            return Failure(trial, rawResponse, $"invalid_uci_format: '{move}' is not coordinate UCI");
        }

        if (!string.IsNullOrWhiteSpace(answer.OriginSquare) &&
            !string.Equals(answer.OriginSquare.Trim(), move[..2], StringComparison.OrdinalIgnoreCase))
        {
            return Failure(trial, rawResponse, $"origin_mismatch: originSquare '{answer.OriginSquare}' does not match move origin '{move[..2]}'");
        }

        if (!string.IsNullOrWhiteSpace(answer.DestinationSquare) &&
            !string.Equals(answer.DestinationSquare.Trim(), move.Substring(2, 2), StringComparison.OrdinalIgnoreCase))
        {
            return Failure(trial, rawResponse, $"destination_mismatch: destinationSquare '{answer.DestinationSquare}' does not match move destination '{move.Substring(2, 2)}'");
        }

        if (ChessQuestLegalActionProbeRunner.TryGetPieceAt(trial.Fen, move[..2]) is not { } originPiece ||
            originPiece.Color != trial.AgentColor)
        {
            return Failure(trial, rawResponse, $"origin_square_not_owned: '{move[..2]}' is not occupied by a {trial.AgentColor} piece");
        }

        var destinationPiece = ChessQuestLegalActionProbeRunner.TryGetPieceAt(trial.Fen, move.Substring(2, 2));
        if (destinationPiece is not null && destinationPiece.Color == trial.AgentColor)
        {
            return Failure(trial, rawResponse, $"destination_occupied_by_own_piece: '{move.Substring(2, 2)}' is occupied by a {trial.AgentColor} piece");
        }

        if (destinationPiece is { Name: "king" })
        {
            return Failure(trial, rawResponse, $"destination_is_opponent_king: '{move.Substring(2, 2)}' contains the opposing king");
        }

        if (ChessQuestLegalActionProbeRunner.TryGetSlidingPathBlock(trial.Fen, move) is { } blockedSquare)
        {
            return Failure(trial, rawResponse, $"path_blocked_by_piece: sliding move '{move}' is blocked at '{blockedSquare}'");
        }

        var rules = new GeraChessRulesEngine(trial.Fen);
        var legal = rules.ListLegalMoves().Any(legalMove => legalMove.Uci == move);
        var accepted = legal && trial.AcceptedMoves.Contains(move, StringComparer.Ordinal);
        return new ChessQuestPuzzleProbeTrialResult(
            TrialNumber: trial.TrialNumber,
            PuzzleId: trial.PuzzleId,
            Passed: accepted,
            Move: move,
            RawResponse: rawResponse,
            FailureReason: accepted
                ? null
                : legal
                    ? trial.AcceptedMoves.Count == 1
                        ? $"wrong_move: '{move}' is legal but not the single accepted answer"
                        : $"wrong_move: '{move}' is legal but not in the accepted best-move span"
                    : $"illegal_move: '{move}' is not legal in the puzzle position");
    }

    public static string SerializeSummary(ChessQuestPuzzleProbeSummary summary) =>
        JsonSerializer.Serialize(summary, JsonOptions);

    public static ChessQuestPuzzleProbeTrial BuiltInPuzzle(string puzzleId = "fools_mate_black_mate_in_one") =>
        BuiltInPuzzles.Single(puzzle => string.Equals(puzzle.PuzzleId, puzzleId, StringComparison.Ordinal));

    internal static IReadOnlyList<ChessQuestPuzzleProbeTrial> BuiltInPuzzlesForTests() => BuiltInPuzzles;

    internal static ChessQuestPuzzleProbeTrial CreateGeneratedPuzzle(
        int trialNumber,
        int seed,
        int scramblePlies)
    {
        var random = new Random(seed);
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var fen = random.Next(2) == 0
                ? CreateSyntheticCaptureFen(random)
                : CreateSyntheticPromotionFen(random);
            GeraChessRulesEngine rules;
            try
            {
                rules = new GeraChessRulesEngine(fen);
            }
            catch
            {
                continue;
            }

            var state = rules.GetState();
            if (state.IsTerminal)
            {
                continue;
            }

            var candidates = ScorePuzzleCandidates(fen, state.SideToMove);
            var top = candidates.FirstOrDefault();
            if (top is null || top.Score <= 0)
            {
                continue;
            }

            var topBand = TopMoveBand(candidates);
            if (topBand.Count == 0 || topBand.Count > 4)
            {
                continue;
            }

            var objective = top.Kind switch
            {
                GeneratedPuzzleKind.Promotion =>
                    $"Generated rules-derived puzzle. Find the best coordinate-UCI move for {state.SideToMove} by the immediate promotion objective.",
                _ =>
                    $"Generated rules-derived puzzle. Find the best coordinate-UCI move for {state.SideToMove} by immediate material gain. Captures and promotions count; long-term engine evaluation is not used."
            };

            return new ChessQuestPuzzleProbeTrial(
                TrialNumber: trialNumber,
                PuzzleId: $"generated_{top.Kind.ToString().ToLowerInvariant()}_{seed}_{attempt}",
                Source: ChessQuestPuzzleProbeSource.Generated,
                Objective: objective,
                Fen: fen,
                BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(fen),
                AgentColor: state.SideToMove,
                AcceptedMoves: topBand.Select(candidate => candidate.Move).ToArray(),
                GenerationNote: $"seed={seed}; attempt={attempt}; score={top.Score}; acceptedBand={topBand.Count}; source=procedural_rules_derived_{top.Kind}");
        }

        return CreateGeneratedRandomMaterialPuzzle(trialNumber, seed, scramblePlies);
    }

    internal static ChessQuestPuzzleProbeTrial CreateGeneratedRandomMaterialPuzzle(
        int trialNumber,
        int seed,
        int scramblePlies)
    {
        var random = new Random(seed);
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var rules = new GeraChessRulesEngine(ChessQuestBoardProbeRunner.StartFen);
            var plies = Math.Max(4, scramblePlies + random.Next(-4, 5));
            for (var ply = 0; ply < plies; ply++)
            {
                var legalMoves = rules.ListLegalMoves();
                if (legalMoves.Count == 0 || rules.GetState().IsTerminal)
                {
                    break;
                }

                var result = rules.TryPlayMove(legalMoves[random.Next(legalMoves.Count)].Uci);
                if (!result.Accepted)
                {
                    break;
                }
            }

            var state = rules.GetState();
            if (state.IsTerminal)
            {
                continue;
            }

            var candidates = ScorePuzzleCandidates(rules.GetFen(), state.SideToMove);
            var top = candidates.FirstOrDefault();
            if (top is null || top.Score <= 0)
            {
                continue;
            }

            var topBand = TopMoveBand(candidates);
            if (topBand.Count == 0 || topBand.Count > 4)
            {
                continue;
            }

            var second = candidates.Skip(topBand.Count).FirstOrDefault();
            if (second is not null && top.Score - second.Score < 100)
            {
                continue;
            }

            var objective = top.Kind switch
            {
                GeneratedPuzzleKind.Checkmate =>
                    $"Generated rules-derived puzzle. Find the single coordinate-UCI move for {state.SideToMove} that immediately checkmates the opponent.",
                GeneratedPuzzleKind.Promotion =>
                    $"Generated rules-derived puzzle. Find the best coordinate-UCI move for {state.SideToMove} by the immediate promotion/material objective.",
                _ =>
                    $"Generated rules-derived puzzle. Find the best coordinate-UCI move for {state.SideToMove} by immediate material gain. Captures and promotions count; long-term engine evaluation is not used."
            };

            return new ChessQuestPuzzleProbeTrial(
                TrialNumber: trialNumber,
                PuzzleId: $"generated_{top.Kind.ToString().ToLowerInvariant()}_{seed}_{attempt}",
                Source: ChessQuestPuzzleProbeSource.RandomGenerated,
                Objective: objective,
                Fen: rules.GetFen(),
                BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(rules.GetFen()),
                AgentColor: state.SideToMove,
                AcceptedMoves: topBand.Select(candidate => candidate.Move).ToArray(),
                GenerationNote: $"seed={seed}; attempt={attempt}; score={top.Score}; acceptedBand={topBand.Count}; source=rules_derived_{top.Kind}");
        }

        throw new InvalidOperationException($"Unable to generate a unique ChessQuest puzzle for seed {seed} after 1000 attempts.");
    }

    private static string CreateSyntheticCaptureFen(Random random)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var sideToMove = random.Next(2) == 0 ? ChessQuestColor.White : ChessQuestColor.Black;
            var mover = Pick(random, ['q', 'r', 'b', 'n']);
            var target = Pick(random, ['q', 'r', 'b', 'n', 'p']);
            var origin = RandomSquare(random);
            if (!TryReachableDestination(random, origin, mover, out var destination))
            {
                continue;
            }

            if (target == 'p' && IsBackRank(destination))
            {
                continue;
            }

            var occupied = new HashSet<string>(StringComparer.Ordinal)
            {
                origin,
                destination
            };
            if (!TryPlaceKings(random, occupied, out var whiteKing, out var blackKing))
            {
                continue;
            }

            var pieces = new List<(string Square, char Piece)>
            {
                (whiteKing, 'K'),
                (blackKing, 'k'),
                (origin, PieceChar(sideToMove, mover)),
                (destination, PieceChar(Opposite(sideToMove), target))
            };

            AddDistractorPieces(random, sideToMove, occupied, pieces);
            var fen = FenFromPieces(sideToMove, pieces.ToArray());
            if (ScorePuzzleCandidates(fen, sideToMove).FirstOrDefault() is { Score: > 0 })
            {
                return fen;
            }
        }

        return CreateSyntheticPromotionFen(random);
    }

    private static string CreateSyntheticPromotionFen(Random random)
    {
        var file = (char)('a' + random.Next(8));
        if (random.Next(2) == 0)
        {
            var blackKingSquare = RandomBackRankKingSquare(random, ChessQuestColor.Black, avoidFile: file);
            var whiteKingSquare = RandomBackRankKingSquare(random, ChessQuestColor.White, avoidFile: file);
            return FenFromPieces(
                sideToMove: ChessQuestColor.White,
                (Square: whiteKingSquare, Piece: 'K'),
                (Square: blackKingSquare, Piece: 'k'),
                (Square: $"{file}7", Piece: 'P'));
        }
        else
        {
            var whiteKingSquare = RandomBackRankKingSquare(random, ChessQuestColor.White, avoidFile: file);
            var blackKingSquare = RandomBackRankKingSquare(random, ChessQuestColor.Black, avoidFile: file);
            return FenFromPieces(
                sideToMove: ChessQuestColor.Black,
                (Square: blackKingSquare, Piece: 'k'),
                (Square: whiteKingSquare, Piece: 'K'),
                (Square: $"{file}2", Piece: 'p'));
        }
    }

    private static bool TryReachableDestination(
        Random random,
        string origin,
        char mover,
        out string destination)
    {
        var originFile = origin[0] - 'a';
        var originRank = origin[1] - '1';
        var candidates = mover switch
        {
            'n' => KnightDestinations(originFile, originRank),
            'b' => SlidingDestinations(originFile, originRank, [(1, 1), (1, -1), (-1, 1), (-1, -1)]),
            'r' => SlidingDestinations(originFile, originRank, [(1, 0), (-1, 0), (0, 1), (0, -1)]),
            'q' => SlidingDestinations(originFile, originRank, [(1, 1), (1, -1), (-1, 1), (-1, -1), (1, 0), (-1, 0), (0, 1), (0, -1)]),
            _ => []
        };

        if (candidates.Count == 0)
        {
            destination = string.Empty;
            return false;
        }

        destination = candidates[random.Next(candidates.Count)];
        return true;
    }

    private static IReadOnlyList<string> KnightDestinations(int file, int rank)
    {
        (int File, int Rank)[] offsets =
        [
            (1, 2), (2, 1), (2, -1), (1, -2),
            (-1, -2), (-2, -1), (-2, 1), (-1, 2)
        ];
        return offsets
            .Select(offset => (File: file + offset.File, Rank: rank + offset.Rank))
            .Where(square => IsBoardSquare(square.File, square.Rank))
            .Select(square => ToSquare(square.File, square.Rank))
            .ToArray();
    }

    private static IReadOnlyList<string> SlidingDestinations(
        int file,
        int rank,
        IReadOnlyList<(int File, int Rank)> directions)
    {
        var result = new List<string>();
        foreach (var direction in directions)
        {
            var nextFile = file + direction.File;
            var nextRank = rank + direction.Rank;
            while (IsBoardSquare(nextFile, nextRank))
            {
                result.Add(ToSquare(nextFile, nextRank));
                nextFile += direction.File;
                nextRank += direction.Rank;
            }
        }

        return result;
    }

    private static bool TryPlaceKings(
        Random random,
        HashSet<string> occupied,
        out string whiteKing,
        out string blackKing)
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            whiteKing = RandomSquare(random);
            blackKing = RandomSquare(random);
            if (occupied.Contains(whiteKing) ||
                occupied.Contains(blackKing) ||
                string.Equals(whiteKing, blackKing, StringComparison.Ordinal) ||
                KingsAdjacent(whiteKing, blackKing))
            {
                continue;
            }

            occupied.Add(whiteKing);
            occupied.Add(blackKing);
            return true;
        }

        whiteKing = string.Empty;
        blackKing = string.Empty;
        return false;
    }

    private static void AddDistractorPieces(
        Random random,
        ChessQuestColor sideToMove,
        HashSet<string> occupied,
        List<(string Square, char Piece)> pieces)
    {
        var count = random.Next(0, 4);
        for (var index = 0; index < count; index++)
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var square = RandomSquare(random);
                if (!occupied.Add(square))
                {
                    continue;
                }

                var color = random.Next(2) == 0 ? sideToMove : Opposite(sideToMove);
                var piece = Pick(random, ['p', 'n', 'b', 'r']);
                if (piece == 'p' && IsBackRank(square))
                {
                    occupied.Remove(square);
                    continue;
                }

                pieces.Add((square, PieceChar(color, piece)));
                break;
            }
        }
    }

    private static string RandomBackRankKingSquare(
        Random random,
        ChessQuestColor color,
        char avoidFile)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var file = (char)('a' + random.Next(8));
            if (file == avoidFile)
            {
                continue;
            }

            return $"{file}{(color == ChessQuestColor.White ? '1' : '8')}";
        }

        return color == ChessQuestColor.White ? "e1" : "e8";
    }

    private static string RandomSquare(Random random) =>
        ToSquare(random.Next(8), random.Next(8));

    private static string ToSquare(int file, int rank) =>
        $"{(char)('a' + file)}{rank + 1}";

    private static bool IsBoardSquare(int file, int rank) =>
        file is >= 0 and < 8 && rank is >= 0 and < 8;

    private static bool KingsAdjacent(string first, string second) =>
        Math.Abs(first[0] - second[0]) <= 1 &&
        Math.Abs(first[1] - second[1]) <= 1;

    private static bool IsBackRank(string square) =>
        square[1] is '1' or '8';

    private static char PieceChar(ChessQuestColor color, char lowerPiece) =>
        color == ChessQuestColor.White
            ? char.ToUpperInvariant(lowerPiece)
            : char.ToLowerInvariant(lowerPiece);

    private static ChessQuestColor Opposite(ChessQuestColor color) =>
        color == ChessQuestColor.White ? ChessQuestColor.Black : ChessQuestColor.White;

    private static char Pick(Random random, IReadOnlyList<char> values) =>
        values[random.Next(values.Count)];

    private static string FenFromPieces(
        ChessQuestColor sideToMove,
        params (string Square, char Piece)[] pieces)
    {
        var board = new char[8, 8];
        foreach (var item in pieces)
        {
            var file = item.Square[0] - 'a';
            var rank = item.Square[1] - '1';
            board[rank, file] = item.Piece;
        }

        var ranks = new List<string>(8);
        for (var rank = 7; rank >= 0; rank--)
        {
            var empty = 0;
            var text = string.Empty;
            for (var file = 0; file < 8; file++)
            {
                var piece = board[rank, file];
                if (piece == '\0')
                {
                    empty++;
                    continue;
                }

                if (empty > 0)
                {
                    text += empty.ToString();
                    empty = 0;
                }

                text += piece;
            }

            if (empty > 0)
            {
                text += empty.ToString();
            }

            ranks.Add(text);
        }

        return $"{string.Join('/', ranks)} {(sideToMove == ChessQuestColor.White ? "w" : "b")} - - 0 1";
    }

    private static IReadOnlyList<GeneratedPuzzleCandidate> ScorePuzzleCandidates(
        string fen,
        ChessQuestColor sideToMove)
    {
        var rules = new GeraChessRulesEngine(fen);
        return rules.ListLegalMoves()
            .Select(move => ScorePuzzleCandidate(fen, sideToMove, move.Uci))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Move, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<GeneratedPuzzleCandidate> TopMoveBand(
        IReadOnlyList<GeneratedPuzzleCandidate> candidates)
    {
        var top = candidates.FirstOrDefault();
        return top is null
            ? []
            : candidates
                .Where(candidate => candidate.Score == top.Score)
                .ToArray();
    }

    private static GeneratedPuzzleCandidate ScorePuzzleCandidate(
        string fen,
        ChessQuestColor sideToMove,
        string move)
    {
        var clone = new GeraChessRulesEngine(fen);
        var result = clone.TryPlayMove(move);
        if (!result.Accepted || result.Move is null)
        {
            return new GeneratedPuzzleCandidate(move, 0, GeneratedPuzzleKind.Material);
        }

        var state = clone.GetState();
        if (state.TerminalState?.Winner == sideToMove &&
            state.TerminalState.Reason.Contains("Checkmate", StringComparison.OrdinalIgnoreCase))
        {
            return new GeneratedPuzzleCandidate(result.Move, 1_000_000, GeneratedPuzzleKind.Checkmate);
        }

        var captureValue = result.Captures.Sum(capture => PieceValue(capture.Piece));
        var promotionValue = move.Length == 5 ? PromotionValue(move[4]) : 0;
        var score = captureValue + promotionValue;
        var kind = promotionValue > captureValue
            ? GeneratedPuzzleKind.Promotion
            : GeneratedPuzzleKind.Material;

        return new GeneratedPuzzleCandidate(result.Move, score, kind);
    }

    private static int PieceValue(string projectedPiece)
    {
        var piece = projectedPiece.Split('_').LastOrDefault() ?? string.Empty;
        return piece switch
        {
            "pawn" => 100,
            "knight" => 320,
            "bishop" => 330,
            "rook" => 500,
            "queen" => 900,
            _ => 0
        };
    }

    private static int PromotionValue(char promotion) =>
        char.ToLowerInvariant(promotion) switch
        {
            'q' => 850,
            'r' => 450,
            'b' => 280,
            'n' => 270,
            _ => 0
        };

    private enum GeneratedPuzzleKind
    {
        Material,
        Promotion,
        Checkmate
    }

    private sealed record GeneratedPuzzleCandidate(
        string Move,
        int Score,
        GeneratedPuzzleKind Kind);

    private async Task<ChessQuestPuzzleProbeTrialResult> RunTrialAsync(
        ChessQuestPuzzleProbeTrial trial,
        ChessQuestBoardProbeOptions options,
        CancellationToken cancellationToken)
    {
        var response = await _client.GenerateAsync(new LlmRequest(
                options.ModelId,
                [
                    new LlmMessage(
                        LlmMessageRole.System,
                        """
                        You are being tested as a chess puzzle solver. Use the supplied public board state to solve the stated objective.
                        Return one coordinate-UCI move only through the requested JSON object. Do not output SAN or prose outside JSON.
                        """),
                    new LlmMessage(
                        LlmMessageRole.User,
                        BuildPrompt(trial, options.Presentation))
                ],
                GenerationOptions: new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: options.MaxOutputTokens,
                    Thinking: ChessQuestBoardProbeRunner.ToThinkingOptions(options.ThinkingBudget, options.IncludeThoughts)),
                StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: ChessQuestLegalActionProbeRunner.MoveAnswerJsonSchema)),
            cancellationToken).ConfigureAwait(false);

        return Validate(trial, response.StructuredJson ?? response.Text) with
        {
            ProviderName = response.ProviderName,
            ResponseModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            ResponseMetadata = response.Metadata
        };
    }

    private static ChessQuestPuzzleProbeTrialResult Failure(
        ChessQuestPuzzleProbeTrial trial,
        string rawResponse,
        string reason) =>
        new(
            TrialNumber: trial.TrialNumber,
            PuzzleId: trial.PuzzleId,
            Passed: false,
            Move: null,
            RawResponse: rawResponse,
            FailureReason: reason);
}

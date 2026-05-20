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

public sealed record ChessQuestBoardProbeOptions(
    int Trials,
    int Seed,
    int ScramblePlies,
    ChessQuestBoardProbePresentation Presentation,
    ChessQuestBoardProbeTargetMode TargetMode,
    ChessQuestPuzzleProbeSource PuzzleSource,
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
            var legalMoves = rules.ListLegalMoves();
            if (legalMoves.Count == 0 || rules.GetState().IsTerminal)
            {
                break;
            }

            var move = legalMoves[random.Next(legalMoves.Count)].Uci;
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

    private static IReadOnlyList<ChessQuestProbePiece> EnumeratePieces(string fen)
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
            var rules = new GeraChessRulesEngine(fen);
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
                GenerationNote: $"seed={seed}; attempt={attempt}; score={top.Score}; acceptedBand={topBand.Count}; source=synthetic_rules_derived_{top.Kind}");
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
        var templates = new[]
        {
            "4k3/q7/8/8/8/8/8/R3K3 w - - 0 1",
            "4k3/8/8/8/8/7q/8/2B1K3 w - - 0 1",
            "4k3/8/8/8/8/5q2/8/4K1N1 w - - 0 1",
            "r3k3/8/8/8/8/8/Q7/4K3 b - - 0 1",
            "2b1k3/8/8/8/8/7Q/8/4K3 b - - 0 1",
            "4k1n1/8/5Q2/8/8/8/8/4K3 b - - 0 1"
        };

        return templates[random.Next(templates.Length)];
    }

    private static string CreateSyntheticPromotionFen(Random random)
    {
        var file = (char)('a' + random.Next(8));
        if (random.Next(2) == 0)
        {
            var blackKingSquare = file == 'h' ? "a8" : "h8";
            return FenFromPieces(
                sideToMove: ChessQuestColor.White,
                (Square: "e1", Piece: 'K'),
                (Square: blackKingSquare, Piece: 'k'),
                (Square: $"{file}7", Piece: 'P'));
        }
        else
        {
            var whiteKingSquare = file == 'h' ? "a1" : "h1";
            return FenFromPieces(
                sideToMove: ChessQuestColor.Black,
                (Square: "e8", Piece: 'k'),
                (Square: whiteKingSquare, Piece: 'K'),
                (Square: $"{file}2", Piece: 'p'));
        }
    }

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

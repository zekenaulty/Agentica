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

public sealed record ChessQuestBoardProbeOptions(
    int Trials,
    int Seed,
    int ScramblePlies,
    ChessQuestBoardProbePresentation Presentation,
    ChessQuestBoardProbeTargetMode TargetMode,
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
    string? PublicReason = null);

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
            You are {{trial.SideToMove}} to move. Produce one legal chess move in UCI notation from the supplied current board.
            You are not given the legal move list. The host will verify legality after your answer.
            {{boardSection}}
            {{fenSection}}

            Return JSON only:
            {
              "move": "e2e4",
              "publicReason": "one short public reason"
            }
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
                        You are being tested as a chess actor. Choose one legal UCI move from public board state only.
                        Do not ask for a legal move list. Do not output SAN. Return only the requested JSON object.
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
            "move": { "type": "string" },
            "publicReason": { "type": "string" }
          },
          "required": ["move"]
        }
        """;
}

public sealed record ChessQuestPuzzleProbeTrial(
    int TrialNumber,
    string PuzzleId,
    string Objective,
    string Fen,
    IReadOnlyList<string> BoardLines,
    ChessQuestColor AgentColor,
    IReadOnlyList<string> AcceptedMoves);

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
            Objective: "Find the single UCI move for Black that checkmates White.",
            Fen: "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2",
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen("rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2"),
            AgentColor: ChessQuestColor.Black,
            AcceptedMoves: ["d8h4"])
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
            var puzzle = BuiltInPuzzles[index % BuiltInPuzzles.Length] with
            {
                TrialNumber = index + 1
            };
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

    public static string BuildPrompt(
        ChessQuestPuzzleProbeTrial trial,
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
            Puzzle: {{trial.PuzzleId}}
            Role: {{trial.AgentColor}}
            Objective: {{trial.Objective}}
            There is exactly one accepted answer for this probe.
            {{boardSection}}
            {{fenSection}}

            Return JSON only:
            {
              "move": "d8h4",
              "publicReason": "one short public reason"
            }
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
                    ? $"wrong_move: '{move}' is legal but not the single accepted answer"
                    : $"illegal_move: '{move}' is not legal in the puzzle position");
    }

    public static string SerializeSummary(ChessQuestPuzzleProbeSummary summary) =>
        JsonSerializer.Serialize(summary, JsonOptions);

    public static ChessQuestPuzzleProbeTrial BuiltInPuzzle(string puzzleId = "fools_mate_black_mate_in_one") =>
        BuiltInPuzzles.Single(puzzle => string.Equals(puzzle.PuzzleId, puzzleId, StringComparison.Ordinal));

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
                        You are being tested as a chess puzzle solver. Return one UCI move only through the requested JSON object.
                        Use the supplied public board state. Do not output SAN or prose outside JSON.
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

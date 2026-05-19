using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed record ChessQuestStrategyProjectionResult(
    ChessQuestStrategyProjection Projection,
    string Prompt,
    string RawResponse,
    string? ProviderName,
    string? ResponseModelId,
    LlmFinishReason FinishReason,
    LlmUsage? Usage,
    IReadOnlyDictionary<string, string>? ResponseMetadata);

public sealed class ChessQuestStrategyProjectionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILlmClient _client;

    public ChessQuestStrategyProjectionRunner(ILlmClient client)
    {
        _client = client;
    }

    public async Task<ChessQuestStrategyProjectionResult> ProjectAsync(
        ChessQuestSession session,
        string phase,
        int maxAgentTurns,
        string modelId,
        string? thinkingBudget,
        bool includeThoughts,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(session, phase, maxAgentTurns);
        var request = new LlmRequest(
            modelId,
            [
                new LlmMessage(
                    LlmMessageRole.System,
                    """
                    You are the ChessQuest orchestration tier. Produce a public strategy projection for a bounded chess phase.
                    This projection shapes intent only. It is not a chess engine, not a best-move oracle, and not proof of game truth.
                    Do not include engine scores, best-move rankings, principal variations, opponent policy guesses, hidden solution data, or forced-mate claims.
                    The active run must treat chessFrame, legal move receipts, and chess.project_line verification as authoritative.
                    Return JSON only.
                    """),
                new LlmMessage(LlmMessageRole.User, prompt)
            ],
            GenerationOptions: new LlmGenerationOptions(
                Temperature: 0,
                MaxOutputTokens: maxOutputTokens,
                Thinking: ToThinkingOptions(thinkingBudget, includeThoughts)),
            StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: ProjectionJsonSchema));

        var response = await _client.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = response.StructuredJson ?? response.Text;
        var projection = ParseProjection(
            raw,
            session.Scenario.AgentColor,
            phase,
            source: $"llm:{response.ModelId}");

        return new ChessQuestStrategyProjectionResult(
            projection,
            prompt,
            raw,
            response.ProviderName,
            response.ModelId,
            response.FinishReason,
            response.Usage,
            response.Metadata);
    }

    public static ChessQuestStrategyProjectionResult Deterministic(
        ChessQuestSession session,
        string phase,
        int maxAgentTurns,
        string source = "deterministic_orchestration")
    {
        var prompt = BuildPrompt(session, phase, maxAgentTurns);
        var projection = ChessQuestPhaseTracker.DefaultProjection(session, phase, source);
        return new ChessQuestStrategyProjectionResult(
            projection,
            prompt,
            RawResponse: JsonSerializer.Serialize(projection, JsonOptions),
            ProviderName: "deterministic",
            ResponseModelId: "deterministic",
            FinishReason: LlmFinishReason.Stop,
            Usage: null,
            ResponseMetadata: null);
    }

    public static string BuildPrompt(
        ChessQuestSession session,
        string phase,
        int maxAgentTurns)
    {
        var state = session.CurrentState;
        return
            $$"""
            Create a public ChessQuest strategy projection for this bounded phase.

            Session:
            - agentColor: {{session.Scenario.AgentColor}}
            - opponentColor: {{(session.Scenario.AgentColor == ChessQuestColor.White ? ChessQuestColor.Black : ChessQuestColor.White)}}
            - sideToMove: {{state.SideToMove}}
            - agentToMove: {{state.SideToMove == session.Scenario.AgentColor}}
            - objective: {{session.Scenario.PublicObjective}}
            - phase: {{phase}}
            - phaseMaxAgentTurns: {{maxAgentTurns}}
            - ply: {{state.Ply}}

            Board:
            {{ChessQuestRenderer.RenderBoardFromFen(state.Fen)}}

            FEN:
            {{state.Fen}}

            Required JSON shape:
            {
              "phase": "{{phase}}",
              "strategyName": "short public strategy name",
              "strategyIntent": "one sentence public intent",
              "activeObjectives": ["2-5 public phase objectives"],
              "stopTriggers": ["2-6 stop or replan triggers"],
              "progressSignals": ["2-5 observable progress signals"],
              "verificationRules": ["2-5 rules that keep chess truth with the referee/tools"]
            }

            Constraints:
            - Do not choose a move.
            - Do not rank moves.
            - Do not include engine scores or best lines.
            - Do not assert checkmate or forced wins.
            - Include a verification rule requiring chess.project_line before check/checkmate claims.
            """;
    }

    public static ChessQuestStrategyProjection ParseProjection(
        string raw,
        ChessQuestColor agentColor,
        string fallbackPhase,
        string source)
    {
        var json = ExtractJson(raw);
        var contract = JsonSerializer.Deserialize<ProjectionJsonContract>(json, JsonOptions)
            ?? throw new JsonException("Strategy projection response was empty.");
        var phase = Clean(contract.Phase, fallbackPhase);
        var strategyName = Clean(contract.StrategyName, "public phase strategy");
        var strategyIntent = Clean(
            contract.StrategyIntent,
            "Play legal moves that advance the bounded phase while preserving chess truth through referee tools.");

        return new ChessQuestStrategyProjection(
            Kind: "ChessQuestStrategyProjection",
            ProjectionId: $"strategy_projection_{Guid.NewGuid():N}"[..31],
            CreatedAt: DateTimeOffset.UtcNow,
            Source: source,
            AgentColor: agentColor,
            Phase: phase,
            StrategyName: strategyName,
            StrategyIntent: strategyIntent,
            ActiveObjectives: CleanList(
                contract.ActiveObjectives,
                [
                    "select legal moves that advance the phase objective",
                    "use public board state and receipts as authoritative context"
                ],
                maxItems: 5),
            StopTriggers: CleanList(
                contract.StopTriggers,
                [
                    "terminal game state",
                    "phase turn budget exhausted",
                    "strategy no longer matches legal board state"
                ],
                maxItems: 6),
            ProgressSignals: CleanList(
                contract.ProgressSignals,
                [
                    "legal move committed",
                    "phase report records receipt-backed progress"
                ],
                maxItems: 5),
            VerificationRules: EnsureProjectionVerificationRules(CleanList(
                contract.VerificationRules,
                [
                    "chessFrame is authoritative board truth",
                    "legal move receipts override strategy claims",
                    "use chess.project_line to verify check and checkmate claims"
                ],
                maxItems: 5)));
    }

    private static IReadOnlyList<string> EnsureProjectionVerificationRules(IReadOnlyList<string> rules)
    {
        var result = rules.ToList();
        if (!result.Any(rule => rule.Contains("project_line", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("use chess.project_line to verify check and checkmate claims");
        }

        if (!result.Any(rule => rule.Contains("authoritative", StringComparison.OrdinalIgnoreCase) ||
                                rule.Contains("receipt", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("chessFrame and legal move receipts override strategy claims");
        }

        return result.Take(6).ToArray();
    }

    private static IReadOnlyList<string> CleanList(
        IReadOnlyList<string>? values,
        IReadOnlyList<string> fallback,
        int maxItems)
    {
        var cleaned = (values ?? [])
            .Select(value => Clean(value, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string Clean(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ReplaceLineEndings(" ");
    }

    private static string ExtractJson(string rawResponse)
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

    private static LlmThinkingOptions? ToThinkingOptions(
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

    private sealed record ProjectionJsonContract(
        string? Phase,
        string? StrategyName,
        string? StrategyIntent,
        IReadOnlyList<string>? ActiveObjectives,
        IReadOnlyList<string>? StopTriggers,
        IReadOnlyList<string>? ProgressSignals,
        IReadOnlyList<string>? VerificationRules);

    private const string ProjectionJsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "phase": {
              "type": "string"
            },
            "strategyName": {
              "type": "string"
            },
            "strategyIntent": {
              "type": "string"
            },
            "activeObjectives": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "stopTriggers": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "progressSignals": {
              "type": "array",
              "items": {
                "type": "string"
              }
            },
            "verificationRules": {
              "type": "array",
              "items": {
                "type": "string"
              }
            }
          },
          "required": [
            "phase",
            "strategyName",
            "strategyIntent",
            "activeObjectives",
            "stopTriggers",
            "progressSignals",
            "verificationRules"
          ]
        }
        """;
}

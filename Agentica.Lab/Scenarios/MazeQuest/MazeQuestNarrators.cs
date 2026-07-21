using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Lab.Scenarios.MazeQuest;

public interface IMazeQuestTurnNarrator
{
    string Narrate(MazeQuestTurnEnvelope turn, CancellationToken cancellationToken);
}

public sealed class NullMazeQuestTurnNarrator : IMazeQuestTurnNarrator
{
    public static NullMazeQuestTurnNarrator Instance { get; } = new();

    private NullMazeQuestTurnNarrator()
    {
    }

    public string Narrate(MazeQuestTurnEnvelope turn, CancellationToken cancellationToken) =>
        string.Empty;
}

public sealed class DeterministicMazeQuestTurnNarrator : IMazeQuestTurnNarrator
{
    public static DeterministicMazeQuestTurnNarrator Instance { get; } = new();

    private DeterministicMazeQuestTurnNarrator()
    {
    }

    public string Narrate(MazeQuestTurnEnvelope turn, CancellationToken cancellationToken)
    {
        if (string.Equals(turn.ReceiptStatus, "Refused", StringComparison.OrdinalIgnoreCase))
        {
            return $"{turn.ToolId} was refused: {turn.ReceiptMessage}";
        }

        return turn.ToolId switch
        {
            MazeQuestToolIds.GetState =>
                $"The agent checks the stage before acting. Active objective: {turn.ActiveObjectiveId}.",
            MazeQuestToolIds.RenderMap =>
                "The agent refreshes the visible fog-of-war map to stay grounded.",
            MazeQuestToolIds.Scan =>
                $"The agent scans nearby cells from ({turn.Position.X}, {turn.Position.Y}).",
            MazeQuestToolIds.SenseObjective =>
                turn.ObjectiveSignal is null
                    ? "The agent asks for a fresh objective signal."
                    : $"The agent senses the objective as {turn.ObjectiveSignal.DistanceBand} to the {turn.ObjectiveSignal.Bearing} with warmth {turn.ObjectiveSignal.Warmth:0.00}.",
            MazeQuestToolIds.EvaluateMoves =>
                BestMove(turn) is { } bestMove
                    ? $"The agent compares local moves; {bestMove.Direction} looks strongest because it is {bestMove.ObjectiveDelta} and reveals {bestMove.FrontierGain} cells."
                    : "The agent compares local moves and waits for a legal option.",
            MazeQuestToolIds.Move =>
                $"The agent advances to ({turn.Position.X}, {turn.Position.Y}); health {turn.Health}, energy {turn.Energy}.",
            MazeQuestToolIds.Take =>
                $"The agent takes the current objective object. Inventory: {InventoryText(turn)}.",
            MazeQuestToolIds.Use =>
                $"The agent uses the current-cell target and updates objective progress. Inventory: {InventoryText(turn)}.",
            MazeQuestToolIds.Rest =>
                $"The agent rests briefly; health {turn.Health}, energy {turn.Energy}.",
            MazeQuestToolIds.CompleteObjective =>
                turn.ArtifactKind == "mazequest.objective_completed"
                    ? "The host emitted the completion artifact; the run now has proof of success."
                    : "The agent asks the host to check objective completion.",
            _ => $"{turn.ToolId} completed with status {turn.ReceiptStatus}."
        };
    }

    private static MazeMoveEvaluation? BestMove(MazeQuestTurnEnvelope turn) =>
        turn.MoveEvaluations
            .Where(move => move.Legal)
            .OrderByDescending(move => move.ObjectiveDelta == "warmer")
            .ThenBy(move => move.VisibleRisk)
            .ThenByDescending(move => move.FrontierGain)
            .FirstOrDefault();

    private static string InventoryText(MazeQuestTurnEnvelope turn) =>
        turn.Inventory.Count == 0 ? "empty" : string.Join(", ", turn.Inventory);
}

public sealed class LlmMazeQuestTurnNarrator : IMazeQuestTurnNarrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly ILlmClient _client;
    private readonly string _modelId;

    static LlmMazeQuestTurnNarrator()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public LlmMazeQuestTurnNarrator(ILlmClient client, string? modelId)
    {
        _client = client;
        _modelId = string.IsNullOrWhiteSpace(modelId) ? GeminiModelId.Flash25 : modelId;
    }

    public string Narrate(MazeQuestTurnEnvelope turn, CancellationToken cancellationToken)
    {
        try
        {
            var response = _client.GenerateAsync(
                    new LlmRequest(
                        ModelId: _modelId,
                        Messages:
                        [
                            new LlmMessage(
                                LlmMessageRole.System,
                                "You narrate a MazeQuest tool turn for a console viewer. Use one concise sentence. Use only the supplied public turn data. Never claim success unless the receipt or artifact proves it."),
                            new LlmMessage(
                                LlmMessageRole.User,
                                JsonSerializer.Serialize(turn with { Narration = string.Empty }, JsonOptions))
                        ],
                        GenerationOptions: new LlmGenerationOptions(
                            Temperature: 0.2,
                            MaxOutputTokens: 96,
                            Thinking: LlmThinkingOptions.Off())))
                .GetAwaiter()
                .GetResult();

            var text = response.Text.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? DeterministicMazeQuestTurnNarrator.Instance.Narrate(turn, cancellationToken)
                : OneLine(text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DeterministicMazeQuestTurnNarrator.Instance.Narrate(turn, cancellationToken);
        }
    }

    private static string OneLine(string text)
    {
        var normalized = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return normalized.Length <= 220 ? normalized : normalized[..220].TrimEnd() + "...";
    }
}

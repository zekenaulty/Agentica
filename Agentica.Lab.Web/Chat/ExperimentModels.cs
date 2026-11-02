using System.Text.Json;

namespace Agentica.Lab.Web.Chat;

public sealed record ExperimentSettings
{
    public string Mode { get; init; } = "clean-room";
    public string Model { get; init; } = "gpt-6-luna";
    public string? ReasoningEffort { get; init; } = "high";
    public string Instructions { get; init; } = "";
    public bool ReasoningSummary { get; init; }
    public bool AmbientWebSearch { get; init; }
    public int MaxWebSearchCalls { get; init; } = 3;
    public int MaxOutputTokens { get; init; } = 4096;
    public int ContextWindowTokens { get; init; } = 131072;
    public int MaxInputCharacters { get; init; } = 80000;
    public int MaxHistoryMessages { get; init; } = 32;
    public string HistoryPolicy { get; init; } = "stop";
    public string ContextDepth { get; init; } = "normal";
    public int MaxToolResultCharacters { get; init; } = 4000;
    public int MaxToolEvidenceCharacters { get; init; } = 8000;
    public int MaxSteps { get; init; } = 12;
    public int MaxContinuations { get; init; } = 4;
    public int MaxProviderCalls { get; init; } = 12;
    public int TimeoutSeconds { get; init; } = 180;
    public IReadOnlyList<string> EnabledTools { get; init; } = [];
    public bool MemoryEnabled { get; init; }
}

public sealed record ExperimentMessage(string Id, string Role, string Content, DateTimeOffset CreatedAt);
public sealed record ExperimentConversation(string Id, string Title, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int SettingsRevision, ExperimentSettings Settings,
    IReadOnlyList<ExperimentTurn> Turns);
public sealed record ExperimentTurn(string Id, string ConversationId, int Sequence, string UserText,
    string AssistantText, string Status, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt,
    int SettingsRevision, ExperimentSettings Settings, string Continuity,
    string? ErrorCode = null, string? ErrorMessage = null, object? Usage = null,
    int TraceCount = 0, int DroppedTraceCount = 0);
public sealed record ExperimentEvent(long Sequence, string Type, DateTimeOffset Time,
    object? Data = null, string? CallId = null, string? ItemId = null);
public sealed record ExperimentTraceInput(string Type, object? Data = null,
    string? CallId = null, string? ItemId = null);
public sealed record ExperimentExecutionContext(string ConversationId, ExperimentTurn Turn,
    IReadOnlyList<ExperimentMessage> History, string WorkspaceDirectory,
    Func<ExperimentTraceInput, Task> Trace, Func<string, Task> Output);
public sealed record ExperimentExecutionResult(string Status, string? AssistantText = null,
    object? Usage = null, string? ErrorCode = null, string? ErrorMessage = null);
public interface IExperimentExecutor
{
    Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken);
}
public sealed record ExperimentCapability(string Id, string Name, string Description,
    string Effect, string Source, string Version = "1");
public sealed record ExperimentContextManifest(string ConversationId, string TurnId,
    IReadOnlyList<string> SelectedMessageIds, IReadOnlyList<string> OmittedMessageIds,
    int SelectedCharacters, int EstimatedInputUnits, int InputAllowance, string Estimator,
    string HistoryPolicy, string Continuity, bool MemoryEnabled = false);
public sealed record ExperimentPreparedContext(IReadOnlyList<ExperimentMessage> Messages,
    ExperimentContextManifest Manifest);
public sealed record ExperimentCreateRequest(string? Title = null);
public sealed record ExperimentRenameRequest(string Title);
public sealed record ExperimentSettingsUpdate(int ExpectedRevision, ExperimentSettings Settings);
public sealed record ExperimentSendRequest(string Content, string? RequestId = null);

public sealed class ExperimentException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

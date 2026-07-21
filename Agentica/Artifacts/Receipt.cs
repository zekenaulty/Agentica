namespace Agentica.Artifacts;

public sealed record Receipt(
    string ReceiptId,
    string StepId,
    string ToolId,
    ReceiptStatus Status,
    string Message,
    DateTimeOffset At,
    IReadOnlyDictionary<string, object?> Data);

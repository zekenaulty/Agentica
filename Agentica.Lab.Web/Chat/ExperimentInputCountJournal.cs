using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Lab.Web.Chat;

public sealed record ExperimentInputCountRecord(LlmPreparedRequestDescriptor Request, JsonElement? Evidence);
public interface IExperimentInputCountJournal
{
    ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken);
    ValueTask ObserveAsync(LlmPreparedRequestDescriptor request, JsonElement evidence);
}
public interface IExperimentInputCountJournalConsumer
{
    void BindInputCountJournal(IExperimentInputCountJournal journal);
}
public interface IExperimentInputCountEvidenceSource
{
    IReadOnlyList<ExperimentInputCountRecord> CountAttempts { get; }
}

// Explicit constructor compatibility for isolated component consumers. Product
// composition binds ExperimentStore; this holder does not claim durable custody.
internal sealed class ExperimentComponentCountJournal : IExperimentInputCountJournal
{
    public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask ObserveAsync(LlmPreparedRequestDescriptor request, JsonElement evidence) => ValueTask.CompletedTask;
}

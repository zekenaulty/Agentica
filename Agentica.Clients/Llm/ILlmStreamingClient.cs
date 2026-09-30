namespace Agentica.Clients.Llm;

/// <summary>Streams provider output while retaining a terminal response for validation and usage.</summary>
public interface ILlmStreamingClient : ILlmClient
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
}

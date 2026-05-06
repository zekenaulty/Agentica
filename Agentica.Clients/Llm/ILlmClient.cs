namespace Agentica.Clients.Llm;

public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
}

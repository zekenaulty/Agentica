namespace Agentica.Clients.OpenAI;

public sealed record OpenAiResponsesClientOptions(
    string? ApiKey = null,
    string DefaultModelId = "gpt-4.1",
    Uri? Endpoint = null)
{
    /// <summary>Requests opaque replay state for stateless reasoning continuity; never public thought text.</summary>
    public bool IncludeEncryptedReasoning { get; init; } = true;

    public static Uri DefaultEndpoint { get; } =
        new("https://api.openai.com/v1/responses");

    public static OpenAiResponsesClientOptions FromEnvironment(
        string defaultModelId = "gpt-4.1") =>
        new(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), defaultModelId);
}

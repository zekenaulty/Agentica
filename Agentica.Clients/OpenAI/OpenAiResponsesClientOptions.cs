namespace Agentica.Clients.OpenAI;

public sealed record OpenAiResponsesClientOptions(
    string? ApiKey = null,
    string DefaultModelId = "gpt-4.1",
    Uri? Endpoint = null)
{
    public static Uri DefaultEndpoint { get; } =
        new("https://api.openai.com/v1/responses");

    public static OpenAiResponsesClientOptions FromEnvironment(
        string defaultModelId = "gpt-4.1") =>
        new(Environment.GetEnvironmentVariable("OPENAI_API_KEY"), defaultModelId);
}

namespace Agentica.Clients.Xai;

public sealed record XaiResponsesClientOptions(
    string? ApiKey = null,
    string DefaultModelId = "grok-4.7",
    Uri? Endpoint = null)
{
    public static Uri DefaultEndpoint { get; } =
        new("https://api.x.ai/v1/responses");

    public static XaiResponsesClientOptions FromEnvironment(
        string defaultModelId = "grok-4.7") =>
        new(Environment.GetEnvironmentVariable("XAI_API_KEY"), defaultModelId);
}

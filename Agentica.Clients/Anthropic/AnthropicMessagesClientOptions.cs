namespace Agentica.Clients.Anthropic;

public sealed record AnthropicMessagesClientOptions(
    string? ApiKey = null,
    string DefaultModelId = "claude-sonnet-4-6",
    Uri? Endpoint = null)
{
    public static Uri DefaultEndpoint { get; } =
        new("https://api.anthropic.com/v1/messages");

    public static AnthropicMessagesClientOptions FromEnvironment(
        string defaultModelId = "claude-sonnet-4-6") =>
        new(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), defaultModelId);
}

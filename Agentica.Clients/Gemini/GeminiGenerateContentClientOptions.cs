namespace Agentica.Clients.Gemini;

/// <summary>Developer API streaming transport. Endpoint overrides are host configuration.</summary>
public sealed record GeminiGenerateContentClientOptions(
    string? ApiKey = null,
    string DefaultModelId = GeminiModelId.Flash25,
    Uri? Endpoint = null)
{
    public static GeminiGenerateContentClientOptions FromEnvironment(
        string defaultModelId = GeminiModelId.Flash25) =>
        new(Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"), defaultModelId);
}

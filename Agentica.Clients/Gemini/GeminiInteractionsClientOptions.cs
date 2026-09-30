namespace Agentica.Clients.Gemini;

public sealed record GeminiInteractionsClientOptions(
    string? ApiKey = null,
    string DefaultModelId = GeminiModelId.Flash25,
    Uri? Endpoint = null)
{
    public static Uri DefaultEndpoint { get; } =
        new("https://generativelanguage.googleapis.com/v1beta/interactions");

    public static GeminiInteractionsClientOptions FromEnvironment(
        string defaultModelId = GeminiModelId.Flash25) =>
        new(
            Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
            defaultModelId);
}

namespace Agentica.Clients.Gemini;

public sealed record GeminiClientOptions(
    string? ApiKey = null,
    string DefaultModelId = GeminiModelId.Flash25,
    bool UseVertexAi = false,
    string? Project = null,
    string? Location = null)
{
    public static GeminiClientOptions FromEnvironment(string defaultModelId = GeminiModelId.Flash25) =>
        new(
            ApiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
            DefaultModelId: defaultModelId,
            UseVertexAi: string.Equals(
                Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            Project: Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT"),
            Location: Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION"));
}

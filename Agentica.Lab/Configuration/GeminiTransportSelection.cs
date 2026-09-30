using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Lab.Configuration;

internal static class GeminiTransportSelection
{
    public static bool UseInteractions
    {
        get
        {
            var selected = Environment.GetEnvironmentVariable("AGENTICA_GEMINI_API");
            if (string.Equals(selected, "legacy", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(selected, "interactions", StringComparison.OrdinalIgnoreCase))
                return true;
            // Vertex remains on its existing SDK path unless Developer API is selected.
            return !string.Equals(
                Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI"),
                "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static ILlmClient Create(string modelId)
    {
        if (!UseInteractions)
        {
            return new RetryingLlmClient(
                new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
                new LlmRetryOptions(CallTimeout: TimeSpan.FromMinutes(10)));
        }

        if (string.Equals(Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI"),
            "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Stateless Interactions mode uses the Gemini Developer API; disable Vertex AI for this run.");
        }

        return new GeminiInteractionsLlmClient(
            GeminiInteractionsClientOptions.FromEnvironment(modelId));
    }

}

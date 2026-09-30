namespace Agentica.Clients.Ollama;

public sealed record OllamaClientOptions(
    Uri? Endpoint = null,
    string? DefaultModelId = null)
{
    public static Uri DefaultEndpoint { get; } =
        new("http://127.0.0.1:11434/api/chat");

    public static OllamaClientOptions FromEnvironment(string? modelId = null)
    {
        var configured = Environment.GetEnvironmentVariable("AGENTICA_OLLAMA_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new OllamaClientOptions(new Uri(configured, UriKind.Absolute), modelId);
        }
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (!string.IsNullOrWhiteSpace(host) &&
            !host.Contains("://", StringComparison.Ordinal))
        {
            host = "http://" + host;
        }
        return string.IsNullOrWhiteSpace(host)
            ? new OllamaClientOptions(DefaultModelId: modelId)
            : new OllamaClientOptions(
                new Uri(new Uri(host.TrimEnd('/') + "/", UriKind.Absolute), "api/chat"),
                modelId);
    }
}

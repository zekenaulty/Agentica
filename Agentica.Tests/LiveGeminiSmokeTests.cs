using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class LiveGeminiSmokeTests
{
    private const string RunLiveTestsVariable = "AGENTICA_RUN_LIVE_LLM_TESTS";

    [Fact]
    public async Task Live_gemini_generation_smoke_test()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        LoadSolutionRootEnvironmentFile();

        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        Assert.False(
            string.IsNullOrWhiteSpace(apiKey),
            $"{RunLiveTestsVariable}=true but no Gemini API key was found. Set GEMINI_API_KEY or GOOGLE_API_KEY in the solution-root .env file or local environment.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var client = new GeminiLlmClient(new GeminiClientOptions(
            ApiKey: apiKey,
            DefaultModelId: GeminiModelId.Flash25,
            UseVertexAi: false));

        var response = await client.GenerateAsync(
            new LlmRequest(
                ModelId: GeminiModelId.Flash25,
                Messages:
                [
                    new LlmMessage(
                        LlmMessageRole.System,
                        "Return JSON only. Do not wrap JSON in markdown."),
                    new LlmMessage(
                        LlmMessageRole.User,
                        "Return exactly one compact JSON object with status set to ok and provider set to gemini.")
                ],
                GenerationOptions: new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: 64,
                    Thinking: LlmThinkingOptions.Off()),
                StructuredOutput: new LlmStructuredOutputOptions()),
            timeout.Token);

        Assert.Equal(GeminiLlmClient.ProviderName, response.ProviderName);
        Assert.Equal(GeminiModelId.Flash25, response.ModelId);

        var json = response.StructuredJson ?? response.Text;
        Assert.False(string.IsNullOrWhiteSpace(json));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("gemini", document.RootElement.GetProperty("provider").GetString());
    }

    private static bool LiveTestsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(RunLiveTestsVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static void LoadSolutionRootEnvironmentFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "Agentica.slnx");
            var envPath = Path.Combine(directory.FullName, ".env");
            if (File.Exists(solutionPath))
            {
                if (File.Exists(envPath))
                {
                    LoadEnvironmentFile(envPath);
                }

                return;
            }

            directory = directory.Parent;
        }
    }

    private static void LoadEnvironmentFile(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

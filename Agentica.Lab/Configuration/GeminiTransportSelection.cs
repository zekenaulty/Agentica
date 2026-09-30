using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Lab.Configuration;

internal static class GeminiTransportSelection
{
    public static bool UseInteractions => string.Equals(
        Environment.GetEnvironmentVariable("AGENTICA_GEMINI_API"),
        "interactions",
        StringComparison.OrdinalIgnoreCase);

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

internal sealed class GeminiStreamTelemetryReporter
{
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private int _outputCharacters;
    private bool _firstOutputSeen;

    public void Report(LlmStreamEvent item)
    {
        switch (item.Kind)
        {
            case LlmStreamEventKind.Activity:
                if (item.Text == "interaction.created")
                {
                    _clock.Restart();
                    _outputCharacters = 0;
                    _firstOutputSeen = false;
                }
                Console.Error.WriteLine($"[gemini] {item.Text}");
                break;
            case LlmStreamEventKind.TextDelta:
                _outputCharacters += item.Text?.Length ?? 0;
                if (!_firstOutputSeen)
                {
                    _firstOutputSeen = true;
                    Console.Error.WriteLine(
                        $"[gemini] first output at {_clock.ElapsedMilliseconds} ms");
                }
                else if (_outputCharacters / 512 !=
                         (_outputCharacters - (item.Text?.Length ?? 0)) / 512)
                {
                    Console.Error.WriteLine(
                        $"[gemini] streaming {_outputCharacters} planner characters; " +
                        $"elapsed={_clock.ElapsedMilliseconds} ms");
                }
                break;
            case LlmStreamEventKind.ThoughtSummaryDelta:
                Console.Error.WriteLine($"[gemini] thought summary: {item.Text}");
                break;
            case LlmStreamEventKind.Completed:
                Console.Error.WriteLine(
                    $"[gemini] completed in {_clock.ElapsedMilliseconds} ms; " +
                    $"plannerChars={_outputCharacters}; " +
                    $"input={item.Response?.Usage?.PromptTokens?.ToString() ?? "unknown"}; " +
                    $"output={item.Response?.Usage?.OutputTokens?.ToString() ?? "unknown"}; " +
                    $"thought={item.Response?.Usage?.ThinkingTokens?.ToString() ?? "unknown"}; " +
                    $"finish={item.Response?.FinishReason}");
                break;
        }
    }
}

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

internal sealed class StreamTelemetryReporter(string provider)
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _outputCharacters;
    private bool _firstOutputSeen;
    private bool _inCall;

    public void Report(LlmStreamEvent item)
    {
        if (item.Kind == LlmStreamEventKind.Started || !_inCall)
        {
            _clock.Restart();
            _outputCharacters = 0;
            _firstOutputSeen = false;
            _inCall = true;
        }
        switch (item.Kind)
        {
            case LlmStreamEventKind.Started:
                Console.Error.WriteLine($"[{provider}] call started");
                break;
            case LlmStreamEventKind.Activity:
                Console.Error.WriteLine($"[{provider}] {item.Text}");
                break;
            case LlmStreamEventKind.TextDelta:
                _outputCharacters += item.Text?.Length ?? 0;
                if (!_firstOutputSeen)
                {
                    _firstOutputSeen = true;
                    Console.Error.WriteLine(
                        $"[{provider}] first output at {_clock.ElapsedMilliseconds} ms");
                }
                else if (_outputCharacters / 512 !=
                         (_outputCharacters - (item.Text?.Length ?? 0)) / 512)
                {
                    Console.Error.WriteLine(
                        $"[{provider}] streaming {_outputCharacters} planner characters; " +
                        $"elapsed={_clock.ElapsedMilliseconds} ms");
                }
                break;
            case LlmStreamEventKind.ThoughtSummaryDelta:
                Console.Error.WriteLine($"[{provider}] thought summary: {item.Text}");
                break;
            case LlmStreamEventKind.Completed:
                Console.Error.WriteLine(
                    $"[{provider}] completed in {_clock.ElapsedMilliseconds} ms; " +
                    $"plannerChars={_outputCharacters}; " +
                    $"input={item.Response?.Usage?.PromptTokens?.ToString() ?? "unknown"}; " +
                    $"output={item.Response?.Usage?.OutputTokens?.ToString() ?? "unknown"}; " +
                    $"thought={item.Response?.Usage?.ThinkingTokens?.ToString() ?? "unknown"}; " +
                    $"finish={item.Response?.FinishReason}");
                _inCall = false;
                break;
            case LlmStreamEventKind.Failed:
            case LlmStreamEventKind.Cancelled:
                Console.Error.WriteLine(
                    $"[{provider}] {item.Kind.ToString().ToLowerInvariant()} " +
                    $"after {_clock.ElapsedMilliseconds} ms; " +
                    $"reason={item.Text ?? "unknown"}; plannerChars={_outputCharacters}");
                _inCall = false;
                break;
        }
    }
}

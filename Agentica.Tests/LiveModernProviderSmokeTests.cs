using System.Runtime.CompilerServices;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class LiveProviderFactAttribute : FactAttribute
{
    public LiveProviderFactAttribute(string provider,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var enabled = Environment.GetEnvironmentVariable("AGENTICA_RUN_LIVE_PROVIDERS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (!enabled.Contains(provider, StringComparer.OrdinalIgnoreCase))
            Skip = $"Opt in with AGENTICA_RUN_LIVE_PROVIDERS containing {provider}.";
    }
}

/// <summary>Explicitly opted-in, two-call smoke tests. Never log output, thoughts, carriers or keys.</summary>
[Collection(ProcessEnvironmentTestCollection.Name)]
public sealed class LiveModernProviderSmokeTests(ITestOutputHelper output)
{
    [LiveProviderFact("gemini")]
    public Task Gemini_stateless_stream_and_signed_follow_up() => RunAsync("gemini");

    [LiveProviderFact("openai")]
    public Task OpenAi_stateless_stream_and_encrypted_follow_up() => RunAsync("openai");

    [LiveProviderFact("ollama")]
    public Task Ollama_native_stream_and_thinking_follow_up() => RunAsync("ollama");

    [LiveProviderFact("anthropic")]
    public Task Anthropic_stream_and_signed_follow_up() => RunAsync("anthropic");

    [LiveProviderFact("xai")]
    public Task Xai_stateless_stream_and_encrypted_follow_up() => RunAsync("xai");

    private async Task RunAsync(string provider)
    {
        var model = Environment.GetEnvironmentVariable($"AGENTICA_LIVE_{provider.ToUpperInvariant()}_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "Select an explicit model for a live provider test.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        ILlmClient client = provider switch
        {
            "gemini" => new GeminiInteractionsLlmClient(
                GeminiInteractionsClientOptions.FromEnvironment(model), http),
            "openai" => new OpenAiResponsesLlmClient(
                OpenAiResponsesClientOptions.FromEnvironment(model), http),
            "ollama" => new OllamaLlmClient(OllamaClientOptions.FromEnvironment(model), http),
            "anthropic" => new AnthropicMessagesLlmClient(
                AnthropicMessagesClientOptions.FromEnvironment(model), http),
            "xai" => new XaiResponsesLlmClient(XaiResponsesClientOptions.FromEnvironment(model), http),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var requestedEffort = Environment.GetEnvironmentVariable($"AGENTICA_LIVE_{provider.ToUpperInvariant()}_EFFORT")
            ?? (provider == "gemini" ? "dynamic" : "low");
        var thinking = Thinking(requestedEffort);
        const string instruction = "Follow the verification instructions. Keep every reply under ten words.";
        var word = "cedar" + Guid.NewGuid().ToString("N")[..8];
        var firstRequest = new LlmRequest(model,
            [new(LlmMessageRole.System, instruction),
             new(LlmMessageRole.User, $"Remember the verification word {word}. Reply ready.")],
            new LlmGenerationOptions(MaxOutputTokens: 2048, Thinking: thinking));
        LlmResponse? first = null;
        LlmResponse? second = null;
        try
        {
            first = await GenerateAsync(client, firstRequest, provider, 1, timeout.Token);
            Assert.NotNull(first.NativeContinuation);
            second = await GenerateAsync(client, firstRequest with
            {
                Messages = [new(LlmMessageRole.System, instruction),
                    new(LlmMessageRole.User, "Return the verification word only.")],
                NativeContinuation = first.NativeContinuation
            }, provider, 2, timeout.Token);
            Assert.NotNull(second.NativeContinuation);
            // A boolean assertion avoids printing provider output when verification fails.
            Assert.True(second.Text.Contains(word, StringComparison.OrdinalIgnoreCase),
                "The native follow-up did not preserve the first turn's verification word.");
        }
        finally
        {
            second?.NativeContinuation?.Dispose();
            first?.NativeContinuation?.Dispose();
        }
    }

    private static LlmThinkingOptions Thinking(string value)
    {
        if (string.Equals(value, "dynamic", StringComparison.OrdinalIgnoreCase))
            return LlmThinkingOptions.Dynamic();
        Assert.True(Enum.TryParse<LlmReasoningEffort>(value, ignoreCase: true, out var effort) &&
            Enum.IsDefined(effort), "Select a named reasoning effort or dynamic for the live test model.");
        return LlmThinkingOptions.AtEffort(effort);
    }

    private async Task<LlmResponse> GenerateAsync(ILlmClient client, LlmRequest request,
        string provider, int turn, CancellationToken cancellationToken)
    {
        using var feed = new LlmTelemetryFeed(provider);
        var progressBeforeTerminal = 0;
        var reasoningActivity = 0;
        var terminal = false;
        LlmResponse? response = null;
        var accepted = false;
        try
        {
            response = await LlmStreamCompletion.GenerateAsync(client, request, item =>
            {
                feed.Report(item);
                if (item.Kind == LlmStreamEventKind.Completed) terminal = true;
                if (!terminal && item.Kind is LlmStreamEventKind.Activity or LlmStreamEventKind.TextDelta
                        or LlmStreamEventKind.ThoughtSummaryDelta)
                    progressBeforeTerminal++;
                if (item.Kind == LlmStreamEventKind.Activity &&
                    item.Text?.Contains("think", StringComparison.OrdinalIgnoreCase) == true)
                    reasoningActivity++;
            }, cancellationToken);
            Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
            Assert.False(string.IsNullOrWhiteSpace(response.Text));
            Assert.True(terminal && progressBeforeTerminal > 0, "Expected live progress before completion.");
            Assert.NotNull(feed.LastTerminal?.FirstOutputElapsedMs);
            output.WriteLine($"provider={provider} model={request.ModelId} turn={turn} " +
                $"progressEvents={progressBeforeTerminal} thinkingEvents={reasoningActivity} " +
                $"elapsedMs={feed.LastTerminal?.ElapsedMs} firstOutputMs={feed.LastTerminal?.FirstOutputElapsedMs} " +
                $"outputChars={response.Text.Length} inputTokens={response.Usage?.PromptTokens} " +
                $"outputTokens={response.Usage?.OutputTokens} reasoningTokens={response.Usage?.ThinkingTokens} " +
                $"continuation={response.NativeContinuation is not null}");
            accepted = true;
            return response;
        }
        finally
        {
            feed.Complete();
            if (!accepted) response?.NativeContinuation?.Dispose();
        }
    }
}

using Agentica.Clients.Llm;
using Google.GenAI.Types;

namespace Agentica.Clients.Gemini;

public static class GeminiResponseMapper
{
    public static LlmResponse Map(string modelId, GenerateContentResponse response)
    {
        var text = response.Text ?? string.Empty;
        var parts = response.Parts ?? [];

        var thoughtSummaries = parts
            .Where(part => part.Thought == true && !string.IsNullOrWhiteSpace(part.Text))
            .Select(part => new LlmThoughtSummary(
                Text: part.Text!,
                Provider: GeminiLlmClient.ProviderName,
                Signature: part.ThoughtSignature is null ? null : Convert.ToBase64String(part.ThoughtSignature)))
            .ToArray();

        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.Concat(
                parts
                    .Where(part => part.Thought != true && !string.IsNullOrWhiteSpace(part.Text))
                    .Select(part => part.Text));
        }

        var candidate = response.Candidates?.FirstOrDefault();

        return new LlmResponse(
            ProviderName: GeminiLlmClient.ProviderName,
            ModelId: modelId,
            Text: text,
            StructuredJson: text,
            ThoughtSummaries: thoughtSummaries,
            Usage: response.UsageMetadata is null
                ? null
                : new LlmUsage(
                    PromptTokens: response.UsageMetadata.PromptTokenCount,
                    OutputTokens: response.UsageMetadata.CandidatesTokenCount,
                    ThinkingTokens: response.UsageMetadata.ThoughtsTokenCount,
                    TotalTokens: response.UsageMetadata.TotalTokenCount,
                    CachedPromptTokens: response.UsageMetadata.CachedContentTokenCount,
                    ToolUsePromptTokens: response.UsageMetadata.ToolUsePromptTokenCount),
            FinishReason: MapFinishReason(candidate?.FinishReason?.ToString()),
            Metadata: response.ResponseId is null
                ? null
                : new Dictionary<string, string>
                {
                    ["responseId"] = response.ResponseId
                });
    }

    private static LlmFinishReason MapFinishReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason))
        {
            return LlmFinishReason.Unknown;
        }

        return finishReason.ToUpperInvariant() switch
        {
            "STOP" => LlmFinishReason.Stop,
            "MAX_TOKENS" => LlmFinishReason.MaxTokens,
            "SAFETY" => LlmFinishReason.Safety,
            "BLOCKLIST" => LlmFinishReason.Blocked,
            "PROHIBITED_CONTENT" => LlmFinishReason.Blocked,
            "SPII" => LlmFinishReason.Blocked,
            "MALFORMED_FUNCTION_CALL" => LlmFinishReason.Error,
            _ => LlmFinishReason.Other
        };
    }
}

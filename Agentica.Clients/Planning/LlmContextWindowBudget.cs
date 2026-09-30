using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Planning;

/// <summary>Host-declared window and capacity reserved outside planner input.</summary>
public sealed record LlmContextWindowBudget(
    int WindowTokens,
    int ReservedOutputTokens,
    int ReservedReasoningTokens = 0,
    int ReservedToolResultTokens = 2048,
    int SafetyMarginTokens = 2048)
{
    public int InputAllowanceTokens
    {
        get
        {
            var allowance = (long)WindowTokens - ReservedOutputTokens -
                ReservedReasoningTokens - ReservedToolResultTokens - SafetyMarginTokens;
            if (WindowTokens <= 0 || ReservedOutputTokens < 0 ||
                ReservedReasoningTokens < 0 || ReservedToolResultTokens < 0 ||
                SafetyMarginTokens < 0 || allowance <= 0)
                throw new LlmPlannerException(
                    "Context window reserves leave no planner input capacity.");
            return (int)allowance;
        }
    }
}

/// <summary>Counts or estimates tokens for a logical provider request.</summary>
public interface ILlmInputTokenEstimator
{
    string Name { get; }

    long EstimateTokens(LlmRequest request);
}

/// <summary>Portable, deliberately conservative proxy. It is not a provider tokenizer.</summary>
public sealed class Utf8ByteTokenProxy : ILlmInputTokenEstimator
{
    public static Utf8ByteTokenProxy Instance { get; } = new();

    public string Name => "utf8-request-byte-proxy-v1";

    public long EstimateTokens(LlmRequest request) =>
        (long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request)) + 512;
}

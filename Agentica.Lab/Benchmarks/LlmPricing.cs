using Agentica.Clients.Llm;

namespace Agentica.Lab.Benchmarks;

public sealed record LlmModelPricing(
    string ModelId,
    decimal UncachedInputUsdPerMillionTokens,
    decimal CachedInputUsdPerMillionTokens,
    decimal OutputUsdPerMillionTokens);

public sealed record LlmPricingSnapshot(
    string SnapshotId,
    DateOnly ReviewedOn,
    string SourceUrl,
    IReadOnlyList<LlmModelPricing> Models);

public sealed record LlmCallCost(
    string PricingSnapshotId,
    string ModelId,
    long UncachedInputTokens,
    long CachedInputTokens,
    long BillableOutputTokens,
    decimal UncachedInputCostUsd,
    decimal CachedInputCostUsd,
    decimal OutputCostUsd,
    decimal TotalCostUsd);

public static class ProductProofPricing
{
    public const string SnapshotId = "gemini-api-standard-pricing-2026-07-21-v2";
    public const string SourceUrl = "https://ai.google.dev/gemini-api/docs/pricing";
    public const string Gemini25FlashModelId = "gemini-2.5-flash";

    public static LlmPricingSnapshot Current { get; } = new(
        SnapshotId,
        new DateOnly(2026, 7, 21),
        SourceUrl,
        Array.AsReadOnly(
        [
            new LlmModelPricing(
                Gemini25FlashModelId,
                UncachedInputUsdPerMillionTokens: 0.30m,
                CachedInputUsdPerMillionTokens: 0.03m,
                OutputUsdPerMillionTokens: 2.50m)
        ]));
}

public static class LlmCostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;

    public static bool TryCalculate(
        string modelId,
        LlmUsage? usage,
        LlmPricingSnapshot snapshot,
        out LlmCallCost cost)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);

        var matchingModels = snapshot.Models
            .Where(model => string.Equals(model.ModelId, modelId, StringComparison.Ordinal))
            .ToArray();
        if (matchingModels.Length != 1 ||
            usage?.PromptTokens is not int promptTokens ||
            usage.OutputTokens is not int outputTokens ||
            promptTokens < 0 ||
            outputTokens < 0 ||
            usage.ThinkingTokens is < 0 ||
            usage.CachedPromptTokens is < 0)
        {
            cost = default!;
            return false;
        }

        var price = matchingModels[0];
        var cachedInputTokens = usage.CachedPromptTokens ?? 0;
        if (cachedInputTokens > promptTokens)
        {
            cost = default!;
            return false;
        }

        var uncachedInputTokens = promptTokens - cachedInputTokens;
        var thinkingTokens = usage.ThinkingTokens ?? 0;
        var billableOutputTokens = checked((long)outputTokens + thinkingTokens);
        var uncachedInputCost = uncachedInputTokens * price.UncachedInputUsdPerMillionTokens / TokensPerMillion;
        var cachedInputCost = cachedInputTokens * price.CachedInputUsdPerMillionTokens / TokensPerMillion;
        var outputCost = billableOutputTokens * price.OutputUsdPerMillionTokens / TokensPerMillion;
        cost = new LlmCallCost(
            snapshot.SnapshotId,
            modelId,
            uncachedInputTokens,
            cachedInputTokens,
            billableOutputTokens,
            uncachedInputCost,
            cachedInputCost,
            outputCost,
            uncachedInputCost + cachedInputCost + outputCost);
        return true;
    }

    private static void ValidateSnapshot(LlmPricingSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId) ||
            snapshot.ReviewedOn == default ||
            !Uri.TryCreate(snapshot.SourceUrl, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps ||
            snapshot.Models is null ||
            snapshot.Models.Count == 0)
        {
            throw new ArgumentException("Pricing must come from a named, dated, HTTPS-reviewed snapshot.", nameof(snapshot));
        }

        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in snapshot.Models)
        {
            if (string.IsNullOrWhiteSpace(model.ModelId) ||
                model.UncachedInputUsdPerMillionTokens < 0 ||
                model.CachedInputUsdPerMillionTokens < 0 ||
                model.OutputUsdPerMillionTokens < 0 ||
                !modelIds.Add(model.ModelId))
            {
                throw new ArgumentException("Pricing snapshot models must be unique exact ids with nonnegative prices.", nameof(snapshot));
            }
        }
    }
}

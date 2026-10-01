using System.Security.Cryptography;
using System.Text;
using Agentica.Clients.Llm;
using Agentica.Planning;

namespace Agentica.Clients.Planning;

/// <summary>Keeps policy, objective, capabilities, and newest evidence intact;
/// selects host-authored compact frame projections and trims older evidence to fit.</summary>
internal static class PlanningPromptCompiler
{
    internal sealed record Result(string Prompt, LlmInputCompilationReceipt Receipt);

    public static Result Compile(
        PlanningRequest request,
        int maxInputCharacters,
        LlmPlannerOptions options,
        string instruction,
        LlmStructuredOutputOptions structuredOutput,
        Func<PlanningRequest, string> buildPrompt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(buildPrompt);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(options);
        if (maxInputCharacters < 8192)
            throw new LlmPlannerException("Planner input ceiling must be at least 8192 characters.");
        var observations = request.Observations.ToList();
        var receipts = request.Receipts.ToList();
        var frames = request.ContextFrames.ToList();
        var originalObservations = observations.ToArray();
        var originalReceipts = receipts.ToArray();
        var originalFrames = frames.ToArray();
        var compactedFrames = new bool[frames.Count];
        var compactionOrder = CompactionOrder(frames);
        var nextCompaction = 0;
        var prompt = buildPrompt(request);
        var tokenAllowance = options.ContextWindowBudget?.InputAllowanceTokens;
        var estimatedTokens = EstimateTokens(instruction, prompt, structuredOutput,
            options);
        while ((long)instruction.Length + prompt.Length > maxInputCharacters ||
               tokenAllowance is { } allowance && estimatedTokens > allowance)
        {
            var frameToCompact = nextCompaction < compactionOrder.Length
                ? compactionOrder[nextCompaction++]
                : -1;
            var canTrimObservation = observations.Count > 1;
            var canTrimReceipt = receipts.Count > 1;
            if (frameToCompact >= 0)
            {
                var frame = frames[frameToCompact];
                frames[frameToCompact] = frame with
                {
                    Payload = frame.CompactPayload!,
                    CompactPayload = null
                };
                compactedFrames[frameToCompact] = true;
            }
            else if (!canTrimObservation && !canTrimReceipt)
                throw new LlmPlannerException(
                    "Mandatory planning context exceeds the configured input budget.");
            else if (canTrimObservation && (!canTrimReceipt ||
                SerializeLength(observations[0]) >= SerializeLength(receipts[0])))
                observations.RemoveAt(0);
            else
                receipts.RemoveAt(0);
            prompt = buildPrompt(request with
            {
                ContextFrames = frames.ToArray(),
                Observations = observations.ToArray(),
                Receipts = receipts.ToArray()
            });
            estimatedTokens = EstimateTokens(instruction, prompt, structuredOutput,
                options);
        }

        var omittedObservationCount = originalObservations.Length - observations.Count;
        var omittedReceiptCount = originalReceipts.Length - receipts.Count;
        var decisions = originalObservations.Select((item, index) => new LlmInputDecision(
                "observation", item.ObservationId, index >= omittedObservationCount))
            .Concat(originalReceipts.Select((item, index) => new LlmInputDecision(
                "receipt", item.ReceiptId, index >= omittedReceiptCount)))
            .Concat(originalFrames.Select((item, index) => new LlmInputDecision(
                "frame", item.FrameId, true,
                compactedFrames[index] ? "compact" : "full")))
            .ToArray();
        var inputHash = ComputeInputHash([instruction, prompt]);
        return new Result(prompt, new LlmInputCompilationReceipt(
            maxInputCharacters, instruction.Length + prompt.Length,
            inputHash, decisions, tokenAllowance, estimatedTokens,
            tokenAllowance is null ? null : options.InputTokenEstimator.Name));
    }

    internal static string ComputeInputHash(IEnumerable<string> messages, string? nativeHistory = null)
    {
        var input = string.Join("\n", messages);
        if (nativeHistory is not null) input += "\n" + nativeHistory;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static bool RepairFitsTokens(
        LlmRequest request,
        LlmPlannerOptions options)
    {
        if (options.ContextWindowBudget is null) return true;
        if (options.InputTokenEstimator is null ||
            string.IsNullOrWhiteSpace(options.InputTokenEstimator.Name))
            throw new LlmPlannerException("Planner input token estimator is invalid.");
        var estimate = options.InputTokenEstimator.EstimateTokens(request);
        if (estimate < 0)
            throw new LlmPlannerException(
                "Planner input token estimator returned a negative count.");
        return estimate <= options.ContextWindowBudget.InputAllowanceTokens;
    }

    private static long? EstimateTokens(string instruction, string prompt,
        LlmStructuredOutputOptions structuredOutput,
        LlmPlannerOptions options)
    {
        if (options.ContextWindowBudget is null) return null;
        if (options.InputTokenEstimator is null ||
            string.IsNullOrWhiteSpace(options.InputTokenEstimator.Name))
            throw new LlmPlannerException("Planner input token estimator is invalid.");
        var estimate = options.InputTokenEstimator.EstimateTokens(new LlmRequest(
            options.ModelId,
            [new LlmMessage(LlmMessageRole.System, instruction),
             new LlmMessage(LlmMessageRole.User, prompt)],
            options.GenerationOptions,
            structuredOutput));
        if (estimate < 0)
            throw new LlmPlannerException("Planner input token estimator returned a negative count.");
        return estimate;
    }

    private static int SerializeLength(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value).Length;

    private static int[] CompactionOrder(IReadOnlyList<PlanningFrame> frames)
    {
        var candidates = new List<(int Index, int Savings)>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.CompactPayload is null) continue;
            var compact = frame with
            {
                Payload = frame.CompactPayload,
                CompactPayload = null
            };
            var savings = SerializeLength(frame) - SerializeLength(compact);
            if (savings > 0) candidates.Add((index, savings));
        }
        return candidates
            .OrderByDescending(item => item.Savings)
            .ThenBy(item => item.Index)
            .Select(item => item.Index)
            .ToArray();
    }
}

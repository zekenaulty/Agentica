using System.Security.Cryptography;
using System.Text;
using Agentica.Clients.Llm;
using Agentica.Planning;

namespace Agentica.Clients.Planning;

/// <summary>Keeps policy, objective, active frames, capabilities, and newest evidence intact;
/// trims only older observations and receipts until the host input character ceiling fits.</summary>
internal static class PlanningPromptCompiler
{
    internal sealed record Result(string Prompt, LlmInputCompilationReceipt Receipt);

    public static Result Compile(
        PlanningRequest request,
        int maxInputCharacters,
        string instruction,
        Func<PlanningRequest, string> buildPrompt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(buildPrompt);
        ArgumentNullException.ThrowIfNull(instruction);
        if (maxInputCharacters < 8192)
            throw new LlmPlannerException("Planner input ceiling must be at least 8192 characters.");
        var observations = request.Observations.ToList();
        var receipts = request.Receipts.ToList();
        var originalObservations = observations.ToArray();
        var originalReceipts = receipts.ToArray();
        var prompt = buildPrompt(request);
        while ((long)instruction.Length + prompt.Length > maxInputCharacters)
        {
            var canTrimObservation = observations.Count > 1;
            var canTrimReceipt = receipts.Count > 1;
            if (!canTrimObservation && !canTrimReceipt)
                throw new LlmPlannerException(
                    "Mandatory planning context exceeds the configured input ceiling.");
            if (canTrimObservation && (!canTrimReceipt ||
                SerializeLength(observations[0]) >= SerializeLength(receipts[0])))
                observations.RemoveAt(0);
            else
                receipts.RemoveAt(0);
            prompt = buildPrompt(request with
            {
                Observations = observations.ToArray(),
                Receipts = receipts.ToArray()
            });
        }

        var omittedObservationCount = originalObservations.Length - observations.Count;
        var omittedReceiptCount = originalReceipts.Length - receipts.Count;
        var decisions = originalObservations.Select((item, index) => new LlmInputDecision(
                "observation", item.ObservationId, index >= omittedObservationCount))
            .Concat(originalReceipts.Select((item, index) => new LlmInputDecision(
                "receipt", item.ReceiptId, index >= omittedReceiptCount)))
            .ToArray();
        var inputHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(instruction + "\n" + prompt)));
        return new Result(prompt, new LlmInputCompilationReceipt(
            maxInputCharacters, instruction.Length + prompt.Length,
            inputHash, decisions));
    }

    private static int SerializeLength(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value).Length;
}

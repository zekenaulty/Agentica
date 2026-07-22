namespace Agentica.Outcomes;

public sealed record OutcomeEnvelope(
    RunOutcome Outcome,
    OutcomeReport Report,
    ReceiptEnvelope Receipts,
    DetailEnvelope Details)
{
    /// <summary>
    /// Complete earlier attempt envelopes in chronological order. The containing envelope is the
    /// final attempt and is intentionally not repeated here.
    /// </summary>
    public IReadOnlyList<OutcomeEnvelope> PriorAttempts { get; init; } = [];
}

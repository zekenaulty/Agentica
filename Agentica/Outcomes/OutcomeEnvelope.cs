namespace Agentica.Outcomes;

public sealed record OutcomeEnvelope(
    RunOutcome Outcome,
    OutcomeReport Report,
    ReceiptEnvelope Receipts,
    DetailEnvelope Details);

using Agentica.Artifacts;

namespace Agentica.Outcomes;

public sealed record ReceiptEnvelope(
    IReadOnlyList<Receipt> Items);

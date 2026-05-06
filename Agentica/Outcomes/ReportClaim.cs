using Agentica.Observations;

namespace Agentica.Outcomes;

public sealed record ReportClaim(
    string Text,
    IReadOnlyList<EvidenceRef> Evidence);

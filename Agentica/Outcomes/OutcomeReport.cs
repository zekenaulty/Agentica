namespace Agentica.Outcomes;

public sealed record OutcomeReport(
    string ReportId,
    string Summary,
    IReadOnlyList<ReportClaim> Claims);

using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.Outcomes;

public interface IOutcomeReporter
{
    OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers);
}

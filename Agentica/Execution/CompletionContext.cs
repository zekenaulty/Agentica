using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Runs;

namespace Agentica.Execution;

public sealed record CompletionContext(
    string RunId,
    int AttemptNumber,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<Receipt> Receipts,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Artifact> Artifacts)
{
    internal static CompletionContext From(AgenticaRun run) =>
        new(
            run.RunId,
            run.AttemptNumber,
            Array.AsReadOnly(run.CompletedSteps.ToArray()),
            Array.AsReadOnly(run.Receipts.ToArray()),
            Array.AsReadOnly(run.Observations.ToArray()),
            Array.AsReadOnly(run.Artifacts.ToArray()));
}

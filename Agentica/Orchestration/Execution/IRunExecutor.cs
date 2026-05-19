using Agentica.Outcomes;
using Agentica.Requests;

namespace Agentica.Orchestration.Execution;

public interface IRunExecutor
{
    Task<OutcomeEnvelope> RunAsync(
        RunRequest request,
        CancellationToken cancellationToken = default);
}

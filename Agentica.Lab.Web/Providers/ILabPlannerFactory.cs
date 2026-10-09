using Agentica.Clients.Llm;
using Agentica.Planning;

namespace Agentica.Lab.Web.Providers;

public interface ILabPlannerFactory
{
    IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent);

    IReadOnlyList<ProviderMetadata> GetProviders();
}

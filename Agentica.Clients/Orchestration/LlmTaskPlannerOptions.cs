using Agentica.Clients.Llm;

namespace Agentica.Clients.Orchestration;

public sealed record LlmTaskPlannerOptions(
    string ModelId,
    LlmGenerationOptions? GenerationOptions = null)
{
    public static LlmTaskPlannerOptions Default { get; } = new("gemini-2.5-pro");
}

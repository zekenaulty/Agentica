using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Planning;

public sealed record LlmPlannerOptions(
    string ModelId = GeminiModelId.Flash25,
    LlmGenerationOptions? GenerationOptions = null)
{
    public static LlmPlannerOptions Default { get; } =
        new(GenerationOptions: new LlmGenerationOptions(Temperature: 0, MaxOutputTokens: 4096));
}

using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Planning;

public sealed record LlmPlannerOptions(
    string ModelId = GeminiModelId.Flash25,
    LlmGenerationOptions? GenerationOptions = null,
    int InvalidJsonRepairAttempts = 2,
    int MaxRepairPayloadCharacters = 8000)
{
    public const int DefaultMaxOutputTokens = 12_288;

    public static LlmPlannerOptions Default { get; } =
        new(GenerationOptions: new LlmGenerationOptions(Temperature: 0, MaxOutputTokens: DefaultMaxOutputTokens));
}

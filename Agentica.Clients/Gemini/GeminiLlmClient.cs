using System.Text.Json;
using Agentica.Clients.Llm;
using Google.GenAI;
using Google.GenAI.Types;

namespace Agentica.Clients.Gemini;

public sealed class GeminiLlmClient : ILlmClient
{
    public const string ProviderName = "Gemini";

    private readonly GeminiClientOptions _options;

    public GeminiLlmClient(GeminiClientOptions? options = null)
    {
        _options = options ?? GeminiClientOptions.FromEnvironment();
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId
            : request.ModelId;

        var client = CreateClient();
        var config = CreateConfig(request);
        var prompt = BuildPrompt(request.Messages);

        try
        {
            var response = await client.Models
                .GenerateContentAsync(model: modelId, contents: prompt, config: config, cancellationToken)
                .ConfigureAwait(false);

            return GeminiResponseMapper.Map(modelId, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new LlmClientException(
                ProviderName,
                $"Gemini generation was canceled by the provider before the caller token was canceled: {exception.Message}",
                exception,
                LlmClientErrorKind.Transient,
                attempts: 1,
                errorClass: "operation_canceled");
        }
        catch (LlmClientException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var classification = GeminiExceptionClassifier.Classify(exception);
            throw new LlmClientException(
                ProviderName,
                $"Gemini generation failed. provider={ProviderName}; errorKind={classification.ErrorKind}; statusCode={classification.StatusCode?.ToString() ?? "none"}; errorClass={classification.ErrorClass}; message={GeminiExceptionClassifier.SafeMessage(exception)}",
                exception,
                classification.ErrorKind,
                classification.StatusCode,
                attempts: 1,
                classification.ErrorClass);
        }
    }

    private Client CreateClient()
    {
        if (_options.UseVertexAi)
        {
            return new Client(
                vertexAI: true,
                project: _options.Project,
                location: _options.Location);
        }

        var apiKey = _options.ApiKey
            ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? System.Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmClientException(
                ProviderName,
                "Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.",
                errorKind: LlmClientErrorKind.Authentication,
                errorClass: "missing_api_key");
        }

        return new Client(apiKey: apiKey);
    }

    internal static GenerateContentConfig CreateConfig(LlmRequest request)
    {
        var systemInstruction = BuildSystemInstruction(request.Messages);
        var config = new GenerateContentConfig
        {
            SystemInstruction = string.IsNullOrWhiteSpace(systemInstruction)
                ? null
                : new Content
                {
                    Parts =
                    [
                        new Part { Text = systemInstruction }
                    ]
                },
            Temperature = request.GenerationOptions?.Temperature,
            MaxOutputTokens = request.GenerationOptions?.MaxOutputTokens,
            ResponseMimeType = request.StructuredOutput?.ResponseMimeType,
            ResponseJsonSchema = ParseJsonSchema(request.StructuredOutput?.JsonSchema),
            ThinkingConfig = GeminiThinkingOptionsMapper.ToSdk(request.GenerationOptions?.Thinking)
        };

        return config;
    }

    private static object? ParseJsonSchema(string? jsonSchema)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(jsonSchema);
        }
        catch (JsonException exception)
        {
            throw new LlmClientException(
                ProviderName,
                "Gemini structured output schema must be valid JSON.",
                exception,
                LlmClientErrorKind.BadRequest,
                errorClass: "invalid_json_schema");
        }
    }

    private static string BuildSystemInstruction(IReadOnlyList<LlmMessage> messages)
    {
        var systemMessages = messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content));

        return string.Join(System.Environment.NewLine + System.Environment.NewLine, systemMessages);
    }

    private static string BuildPrompt(IReadOnlyList<LlmMessage> messages)
    {
        var promptMessages = messages
            .Where(message => message.Role is not LlmMessageRole.System and not LlmMessageRole.Developer)
            .Select(message => $"{message.Role}: {message.Content}")
            .Where(content => !string.IsNullOrWhiteSpace(content));

        return string.Join(System.Environment.NewLine + System.Environment.NewLine, promptMessages);
    }
}

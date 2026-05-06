using Agentica.Clients.Llm;
using Google.GenAI;
using Google.GenAI.Types;
using System.Net;
using System.Net.Http;

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
            var classification = ClassifyException(exception);
            throw new LlmClientException(
                ProviderName,
                $"Gemini generation failed: {exception.Message}",
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

    private static GeminiErrorClassification ClassifyException(Exception exception)
    {
        var statusCode = FindStatusCode(exception);
        if (statusCode is not null)
        {
            return new GeminiErrorClassification(
                ErrorKind: ClassifyStatusCode(statusCode.Value),
                StatusCode: statusCode,
                ErrorClass: $"http_{statusCode.Value}");
        }

        var message = exception.ToString();
        if (ContainsAny(message, "API key", "unauthenticated", "permission denied", "401", "403"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.Authentication, null, "authentication");
        }

        if (ContainsAny(message, "INVALID_ARGUMENT", "bad request", "400", "schema", "request payload"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.BadRequest, null, "bad_request");
        }

        if (ContainsAny(message, "RESOURCE_EXHAUSTED", "rate limit", "quota", "429"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.RateLimited, null, "rate_limited");
        }

        if (ContainsAny(message, "UNAVAILABLE", "DEADLINE_EXCEEDED", "timeout", "temporar", "operation was canceled"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.Transient, null, "transient");
        }

        if (exception is HttpRequestException)
        {
            return new GeminiErrorClassification(LlmClientErrorKind.Network, null, "network");
        }

        return new GeminiErrorClassification(LlmClientErrorKind.Unknown, null, exception.GetType().Name);
    }

    private static LlmClientErrorKind ClassifyStatusCode(int statusCode) =>
        statusCode switch
        {
            401 or 403 => LlmClientErrorKind.Authentication,
            400 or 404 or 422 => LlmClientErrorKind.BadRequest,
            429 => LlmClientErrorKind.RateLimited,
            500 or 502 or 503 or 504 => LlmClientErrorKind.ServerError,
            _ => LlmClientErrorKind.Unknown
        };

    private static int? FindStatusCode(Exception exception)
    {
        if (exception is HttpRequestException httpRequestException &&
            httpRequestException.StatusCode is { } httpStatusCode)
        {
            return (int)httpStatusCode;
        }

        foreach (var propertyName in new[] { "StatusCode", "Status", "Code" })
        {
            var property = exception.GetType().GetProperty(propertyName);
            var value = property?.GetValue(exception);
            if (value is int intValue)
            {
                return intValue;
            }

            if (value is HttpStatusCode statusCode)
            {
                return (int)statusCode;
            }
        }

        return exception.InnerException is null ? null : FindStatusCode(exception.InnerException);
    }

    private static bool ContainsAny(string value, params string[] patterns) =>
        patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static GenerateContentConfig CreateConfig(LlmRequest request)
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
            ThinkingConfig = GeminiThinkingOptionsMapper.ToSdk(request.GenerationOptions?.Thinking)
        };

        return config;
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

    private sealed record GeminiErrorClassification(
        LlmClientErrorKind ErrorKind,
        int? StatusCode,
        string ErrorClass);
}

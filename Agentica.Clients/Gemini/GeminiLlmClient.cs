using System.Text.Json;
using Agentica.Clients.Llm;
using Google.GenAI;
using Google.GenAI.Types;

namespace Agentica.Clients.Gemini;

public sealed class GeminiLlmClient : ILlmClient
{
    public const string ProviderName = "Gemini";

    private static readonly HttpClient SharedHttpClient = LlmProviderTransport.CreateDefaultClient();
    private readonly GeminiClientOptions _options;
    private readonly HttpClient _httpClient;

    public GeminiLlmClient(GeminiClientOptions? options = null)
        : this(options, SharedHttpClient) { }

    public GeminiLlmClient(GeminiClientOptions? options, HttpClient httpClient)
    {
        _options = options ?? GeminiClientOptions.FromEnvironment();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId
            : request.ModelId;
        var recorder = LlmAttemptRecorder.Start(request, ProviderName, modelId);
        try
        {
            if (request.NativeContinuation is not null || request.Messages.Any(message =>
                    message.Role is LlmMessageRole.Assistant or LlmMessageRole.Tool))
            {
                throw new LlmClientException(ProviderName,
                    "Native Gemini history requires exact provider steps; use the Interactions adapter.",
                    errorKind: LlmClientErrorKind.BadRequest,
                    errorClass: "native_history_required");
            }


            using var client = CreateClient(request, recorder);
            var config = CreateConfig(request);
            var prompt = BuildPrompt(request.Messages);

            var response = await client.Models
                .GenerateContentAsync(model: modelId, contents: prompt, config: config, cancellationToken)
                .ConfigureAwait(false);

            var mapped = GeminiResponseMapper.Map(modelId, response);
            recorder.Observe(providerReportedModelId: response.ModelVersion,
                providerResponseId: response.ResponseId, usage: mapped.Usage, finishReason: mapped.FinishReason,
                dispatchCertainty: LlmDispatchCertainty.ResponseObserved);
            cancellationToken.ThrowIfCancellationRequested();
            return mapped with { AttemptEvidence = recorder.Finish(LlmAttemptOutcome.Succeeded) };
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested &&
            ClientExceptionBoundary.IsRecoverable(exception))
        {
            LlmProviderAttempt.RetainFailure(recorder, exception);
            throw;
        }
        catch (OperationCanceledException exception) when (
            ClientExceptionBoundary.IsRecoverable(exception))
        {
            recorder.Finish(LlmAttemptOutcome.Cancelled, "cancelled");
            throw new LlmClientException(
                ProviderName,
                $"Gemini generation was canceled by the provider before the caller token was canceled: {exception.Message}",
                exception,
                LlmClientErrorKind.Transient,
                attempts: 1,
                errorClass: "operation_canceled") { AttemptEvidence = recorder.Evidence };
        }
        catch (LlmClientException exception)
        {
            LlmProviderAttempt.RetainFailure(recorder, exception);
            throw;
        }
        catch (Exception exception) when (ClientExceptionBoundary.IsRecoverable(exception))
        {
            var classification = GeminiExceptionClassifier.Classify(exception);
            LlmProviderAttempt.RetainFailure(recorder, exception);
            throw new LlmClientException(
                ProviderName,
                $"Gemini generation failed. provider={ProviderName}; errorKind={classification.ErrorKind}; statusCode={classification.StatusCode?.ToString() ?? "none"}; errorClass={classification.ErrorClass}; message={GeminiExceptionClassifier.SafeMessage(exception)}",
                exception,
                classification.ErrorKind,
                classification.StatusCode,
                attempts: 1,
                classification.ErrorClass) { AttemptEvidence = recorder.Evidence };
        }
    }

    private Client CreateClient(LlmRequest request, LlmAttemptRecorder recorder)
    {
        var transport = new ClientOptions
        {
            HttpClientFactory = () => new HttpClient(new SdkAttemptHandler(_httpClient, request, recorder))
        };
        if (_options.UseVertexAi)
        {
            return new Client(
                vertexAI: true,
                project: _options.Project,
                location: _options.Location, clientOptions: transport);
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

        return new Client(vertexAI: false, apiKey: apiKey, clientOptions: transport);
    }

    private sealed class SdkAttemptHandler(HttpClient transport, LlmRequest request,
        LlmAttemptRecorder recorder) : HttpMessageHandler
    {
        private int _sent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _sent, 1) != 0)
                throw new InvalidOperationException("The SDK attempted a second inference dispatch.");
            // HttpClient has already marked the SDK message sent. Detach an exact
            // message copy before passing through the host-owned observed transport.
            using var detached = new HttpRequestMessage(message.Method, message.RequestUri)
            { Version = message.Version, VersionPolicy = message.VersionPolicy };
            foreach (var header in message.Headers)
                detached.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (message.Content is not null)
            {
                detached.Content = new ByteArrayContent(await message.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                foreach (var header in message.Content.Headers)
                    detached.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            var response = await LlmProviderTransport.SendAsync(transport, detached, request, recorder,
                cancellationToken).ConfigureAwait(false);
            try
            {
                // Buffer once, just as the SDK's ordinary SendAsync does. Observe structural
                // facts before the SDK's content mapping can fail or caller cancellation wins.
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var parsed = JsonDocument.Parse(json);
                    LlmProviderObservations.GenerateContent(recorder, parsed.RootElement);
                }
                catch (JsonException) { /* Malformed content supplies no parsed structural facts. */ }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
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

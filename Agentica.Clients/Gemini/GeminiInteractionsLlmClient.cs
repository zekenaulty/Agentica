using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Gemini;

/// <summary>
/// Stateless, streamed Gemini Developer API interaction for one bounded text generation.
/// The caller owns context. Native multi-turn thought history and provider tool calls require
/// a separate exact-step contract and are deliberately rejected here.
/// </summary>
public sealed class GeminiInteractionsLlmClient : ILlmStreamingClient
{
    private const int MaxEventCharacters = 1_048_576;
    private const int MaxOutputCharacters = 4_194_304;
    private const int MaxSummaryCharacters = 262_144;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly GeminiInteractionsClientOptions _options;
    private readonly HttpClient _httpClient;

    public GeminiInteractionsLlmClient(
        GeminiInteractionsClientOptions? options = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? GeminiInteractionsClientOptions.FromEnvironment();
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        LlmResponse? completed = null;
        await foreach (var item in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind == LlmStreamEventKind.Completed)
            {
                completed = item.Response;
            }
        }

        return completed ?? throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var apiKey = _options.ApiKey
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw Failure("missing_api_key", LlmClientErrorKind.Authentication);
        }

        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId
            : request.ModelId;
        var body = BuildRequestBody(request, modelId);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Endpoint ?? GeminiInteractionsClientOptions.DefaultEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        message.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmClientException(
                GeminiLlmClient.ProviderName,
                "Gemini Interactions transport failed.",
                exception,
                LlmClientErrorKind.Network,
                errorClass: "transport_failure");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmClientException(
                    GeminiLlmClient.ProviderName,
                    $"Gemini Interactions returned HTTP {(int)response.StatusCode}.",
                    errorKind: ClassifyStatus(response.StatusCode),
                    statusCode: (int)response.StatusCode,
                    errorClass: "http_status");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var text = new StringBuilder();
            var summaries = new List<LlmThoughtSummary>();
            var summaryCharacters = 0;
            var stepKinds = new Dictionary<int, string>();
            string? interactionId = null;
            string? eventName = null;
            var data = new StringBuilder();
            var completed = false;

            while (true)
            {
                var nextLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (nextLine is null && data.Length == 0)
                {
                    break;
                }
                // SSE allows the final event to end at EOF without a trailing blank line.
                var line = nextLine ?? string.Empty;
                if (line.Length > MaxEventCharacters || data.Length + line.Length > MaxEventCharacters)
                {
                    throw Failure("event_too_large", LlmClientErrorKind.BadRequest);
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0 && data.ToString() != "[DONE]")
                    {
                        using var envelope = ParseEvent(data.ToString());
                        var root = envelope.RootElement;
                        var kind = GetString(root, "event_type") ?? eventName;
                        switch (kind)
                        {
                            case "interaction.created":
                                interactionId = GetString(GetObject(root, "interaction"), "id")
                                    ?? interactionId;
                                yield return new LlmStreamEvent(
                                    LlmStreamEventKind.Activity,
                                    "interaction.created");
                                break;
                            case "step.start":
                                var index = GetInt(root, "index");
                                var stepKind = GetString(GetObject(root, "step"), "type");
                                if (index is not null && stepKind is not null)
                                {
                                    stepKinds[index.Value] = stepKind;
                                }
                                if (stepKind == "function_call")
                                {
                                    throw Failure("unexpected_function_call", LlmClientErrorKind.BadRequest);
                                }
                                if (stepKind is "thought" or "model_output")
                                {
                                    yield return new LlmStreamEvent(
                                        LlmStreamEventKind.Activity,
                                        stepKind);
                                }
                                break;
                            case "step.delta":
                                var stepIndex = GetInt(root, "index");
                                var delta = GetObject(root, "delta");
                                var deltaKind = GetString(delta, "type");
                                var currentStep = stepIndex is not null &&
                                    stepKinds.TryGetValue(stepIndex.Value, out var found)
                                        ? found
                                        : null;
                                if (currentStep == "model_output" && deltaKind == "text")
                                {
                                    var chunk = GetString(delta, "text");
                                    if (!string.IsNullOrEmpty(chunk))
                                    {
                                        if (text.Length + chunk.Length > MaxOutputCharacters)
                                        {
                                            throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                                        }
                                        text.Append(chunk);
                                        yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, chunk);
                                    }
                                }
                                else if (currentStep == "thought" && deltaKind == "thought_summary")
                                {
                                    var chunk = GetString(GetObject(delta, "content"), "text")
                                        ?? GetString(delta, "text");
                                    if (!string.IsNullOrEmpty(chunk))
                                    {
                                        if (summaryCharacters + chunk.Length > MaxSummaryCharacters)
                                        {
                                            throw Failure("summary_too_large", LlmClientErrorKind.BadRequest);
                                        }
                                        summaryCharacters += chunk.Length;
                                        summaries.Add(new LlmThoughtSummary(
                                            chunk,
                                            GeminiLlmClient.ProviderName));
                                        yield return new LlmStreamEvent(
                                            LlmStreamEventKind.ThoughtSummaryDelta,
                                            chunk);
                                    }
                                }
                                else if (deltaKind == "arguments_delta")
                                {
                                    throw Failure("unexpected_function_call", LlmClientErrorKind.BadRequest);
                                }
                                // Opaque thought signatures are not projected into generic text or receipts.
                                break;
                            case "interaction.completed":
                                if (completed)
                                {
                                    throw Failure("duplicate_completion", LlmClientErrorKind.Transient);
                                }
                                var interaction = GetObject(root, "interaction");
                                interactionId = GetString(interaction, "id") ?? interactionId;
                                var status = GetString(interaction, "status");
                                if (status is not ("completed" or "incomplete"))
                                {
                                    throw Failure("interaction_" + (status ?? "unknown"),
                                        LlmClientErrorKind.Unknown);
                                }
                                var resultText = text.ToString();
                                var finish = status == "incomplete"
                                    ? LlmFinishReason.MaxTokens
                                    : LlmFinishReason.Stop;
                                var usage = GetObject(interaction, "usage");
                                var result = new LlmResponse(
                                    GeminiLlmClient.ProviderName,
                                    modelId,
                                    resultText,
                                    request.StructuredOutput is null ? null : resultText,
                                    summaries.AsReadOnly(),
                                    new LlmUsage(
                                        GetInt(usage, "total_input_tokens"),
                                        GetInt(usage, "total_output_tokens"),
                                        GetInt(usage, "total_thought_tokens"),
                                        GetInt(usage, "total_tokens"),
                                        GetInt(usage, "total_cached_tokens"),
                                        GetInt(usage, "total_tool_use_tokens")),
                                    finish,
                                    new Dictionary<string, string>(StringComparer.Ordinal)
                                    {
                                        ["gemini.interaction.id"] = interactionId ?? string.Empty,
                                        ["gemini.interaction.status"] = status,
                                        ["gemini.interaction.store"] = "false"
                                    });
                                completed = true;
                                yield return new LlmStreamEvent(
                                    LlmStreamEventKind.Completed,
                                    Response: result);
                                break;
                            case "error":
                                throw Failure("stream_error", LlmClientErrorKind.Unknown);
                        }
                    }
                    eventName = null;
                    data.Clear();
                    if (nextLine is null)
                    {
                        break;
                    }
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }
                    data.Append(line[5..].TrimStart());
                }
            }

            if (!completed)
            {
                throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
            }
        }
    }

    internal static Dictionary<string, object?> BuildRequestBody(LlmRequest request, string modelId)
    {
        if (request.GenerationOptions?.Temperature is not null)
        {
            throw Failure("unsupported_temperature", LlmClientErrorKind.BadRequest);
        }
        if (request.GenerationOptions?.Thinking?.ThinkingBudgetTokens is > 0 or 0)
        {
            throw Failure("unsupported_thinking_budget", LlmClientErrorKind.BadRequest);
        }
        if (request.Messages.Any(message => message.Role is LlmMessageRole.Assistant or LlmMessageRole.Tool))
        {
            throw Failure("native_history_required", LlmClientErrorKind.BadRequest);
        }

        var instruction = string.Join("\n\n", request.Messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content)));
        var input = string.Join("\n\n", request.Messages
            .Where(message => message.Role == LlmMessageRole.User)
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content)));
        if (string.IsNullOrWhiteSpace(input))
        {
            throw Failure("empty_input", LlmClientErrorKind.BadRequest);
        }

        var config = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.GenerationOptions?.MaxOutputTokens is { } maxTokens)
        {
            if (maxTokens <= 0)
            {
                throw Failure("invalid_max_output_tokens", LlmClientErrorKind.BadRequest);
            }
            config["max_output_tokens"] = maxTokens;
        }
        if (request.GenerationOptions?.Thinking?.IncludeThoughts == true)
        {
            config["thinking_summaries"] = "auto";
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false
        };
        if (instruction.Length > 0)
        {
            body["system_instruction"] = instruction;
        }
        if (config.Count > 0)
        {
            body["generation_config"] = config;
        }
        if (request.StructuredOutput is { } structured)
        {
            if (structured.ResponseMimeType != "application/json")
            {
                throw Failure("unsupported_response_format", LlmClientErrorKind.BadRequest);
            }
            var format = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "text",
                ["mime_type"] = structured.ResponseMimeType
            };
            if (!string.IsNullOrWhiteSpace(structured.JsonSchema))
            {
                try
                {
                    format["schema"] = JsonSerializer.Deserialize<JsonElement>(structured.JsonSchema);
                }
                catch (JsonException exception)
                {
                    throw new LlmClientException(
                        GeminiLlmClient.ProviderName,
                        "Gemini Interactions schema must be valid JSON.",
                        exception,
                        LlmClientErrorKind.BadRequest,
                        errorClass: "invalid_json_schema");
                }
            }
            body["response_format"] = format;
        }
        return body;
    }

    private static JsonDocument ParseEvent(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new LlmClientException(
                GeminiLlmClient.ProviderName,
                "Gemini Interactions returned malformed stream data.",
                exception,
                LlmClientErrorKind.Transient,
                errorClass: "malformed_event");
        }
    }

    private static JsonElement GetObject(JsonElement source, string key) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var result)
            ? result
            : default;

    private static string? GetString(JsonElement source, string key)
    {
        var value = GetObject(source, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? GetInt(JsonElement source, string key)
    {
        var value = GetObject(source, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static LlmClientErrorKind ClassifyStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmClientErrorKind.Authentication,
        HttpStatusCode.TooManyRequests => LlmClientErrorKind.RateLimited,
        >= HttpStatusCode.InternalServerError => LlmClientErrorKind.ServerError,
        _ => LlmClientErrorKind.BadRequest
    };

    private static LlmClientException Failure(string code, LlmClientErrorKind kind) =>
        new(GeminiLlmClient.ProviderName, $"Gemini Interactions failed: {code}.",
            errorKind: kind, errorClass: code);
}

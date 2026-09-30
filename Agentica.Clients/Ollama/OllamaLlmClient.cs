using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Ollama;

/// <summary>Native Ollama chat streaming for one bounded generation.</summary>
public sealed class OllamaLlmClient : ILlmStreamingClient
{
    public const string ProviderName = "ollama";
    private const int MaxLineCharacters = 1_048_576;
    private const int MaxOutputCharacters = 4_194_304;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly OllamaClientOptions _options;

    public OllamaLlmClient(OllamaClientOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new OllamaClientOptions();
        _httpClient = httpClient ?? SharedHttpClient;
        var endpoint = _options.Endpoint ?? OllamaClientOptions.DefaultEndpoint;
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "Ollama endpoint must use HTTPS or loopback HTTP.", nameof(options));
        }
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        LlmResponse? result = null;
        await foreach (var item in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind == LlmStreamEventKind.Completed)
            {
                result = item.Response;
            }
        }
        return result ?? throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId
            : request.ModelId;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw Failure("missing_model", LlmClientErrorKind.BadRequest);
        }
        var body = BuildRequestBody(request, model);
        using var message = new HttpRequestMessage(
            HttpMethod.Post, _options.Endpoint ?? OllamaClientOptions.DefaultEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmClientException(ProviderName, "Ollama transport failed.",
                exception, LlmClientErrorKind.Network,
                errorClass: "transport_failure");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmClientException(ProviderName,
                    $"Ollama returned HTTP {(int)response.StatusCode}.",
                    errorKind: ClassifyStatus(response.StatusCode),
                    statusCode: (int)response.StatusCode,
                    errorClass: "http_status");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var output = new StringBuilder();
            var nativeThinking = new StringBuilder();
            var thinkingCharacters = 0;
            var thinkingStarted = false;
            var completed = false;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                if (line.Length > MaxLineCharacters || completed)
                {
                    throw Failure(completed ? "data_after_completion" : "chunk_too_large",
                        LlmClientErrorKind.Transient);
                }
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException exception)
                {
                    throw new LlmClientException(ProviderName,
                        "Ollama returned malformed stream data.", exception,
                        LlmClientErrorKind.Transient, errorClass: "malformed_chunk");
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("error", out var error) &&
                        error.ValueKind == JsonValueKind.String)
                    {
                        throw Failure("stream_error", LlmClientErrorKind.Unknown);
                    }
                    var chunk = GetObject(root, "message");
                    var toolCalls = GetObject(chunk, "tool_calls");
                    if (toolCalls.ValueKind == JsonValueKind.Array &&
                        toolCalls.GetArrayLength() > 0)
                    {
                        throw Failure("unexpected_tool_call", LlmClientErrorKind.BadRequest);
                    }
                    var thinking = GetString(chunk, "thinking");
                    if (!string.IsNullOrEmpty(thinking))
                    {
                        if (thinking.Length > MaxOutputCharacters - thinkingCharacters)
                        {
                            throw Failure("thinking_too_large", LlmClientErrorKind.BadRequest);
                        }
                        thinkingCharacters += thinking.Length;
                        nativeThinking.Append(thinking);
                        if (!thinkingStarted)
                        {
                            thinkingStarted = true;
                            yield return new LlmStreamEvent(
                                LlmStreamEventKind.Activity, "thinking.started");
                        }
                        else if (thinkingCharacters / 1024 !=
                            (thinkingCharacters - thinking.Length) / 1024)
                        {
                            yield return new LlmStreamEvent(
                                LlmStreamEventKind.Activity,
                                $"thinking.characters={thinkingCharacters}");
                        }
                    }
                    var text = GetString(chunk, "content");
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (output.Length + text.Length > MaxOutputCharacters)
                        {
                            throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                        }
                        output.Append(text);
                        yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, text);
                    }
                    if (GetBool(root, "done") == true)
                    {
                        completed = true;
                        var reason = GetString(root, "done_reason");
                        var promptTokens = GetInt(root, "prompt_eval_count");
                        var outputTokens = GetInt(root, "eval_count");
                        var finishReason = reason switch
                        {
                            "length" => LlmFinishReason.MaxTokens,
                            null or "stop" => LlmFinishReason.Stop,
                            _ => LlmFinishReason.Unknown
                        };
                        var continuation = finishReason == LlmFinishReason.Stop &&
                                           output.Length > 0
                            ? CreateContinuation(request, model, body, output.ToString(),
                                nativeThinking.ToString())
                            : null;
                        var result = new LlmResponse(
                            ProviderName, model, output.ToString(),
                            request.StructuredOutput is null ? null : output.ToString(),
                            Usage: new LlmUsage(promptTokens, outputTokens,
                                TotalTokens: promptTokens + outputTokens,
                                CachedPromptTokens: GetInt(root, "prompt_eval_cached_count")),
                            FinishReason: finishReason,
                            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["ollama.doneReason"] = reason ?? string.Empty,
                                ["ollama.thinkingCharacters"] =
                                    thinkingCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["ollama.continuation.available"] =
                                    (continuation is not null).ToString()
                            },
                            NativeContinuation: continuation);
                        yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                            Response: result);
                    }
                }
            }
            if (!completed)
            {
                throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
            }
        }
    }

    internal static Dictionary<string, object?> BuildRequestBody(LlmRequest request, string model)
    {
        if (request.Messages.Any(message =>
            message.Role is LlmMessageRole.Assistant or LlmMessageRole.Tool))
        {
            throw Failure("native_history_required", LlmClientErrorKind.BadRequest);
        }
        if (request.GenerationOptions?.Thinking?.ThinkingBudgetTokens is > 0)
        {
            throw Failure("unsupported_thinking_budget", LlmClientErrorKind.BadRequest);
        }
        var system = string.Join("\n\n", request.Messages
            .Where(message => message.Role == LlmMessageRole.System)
            .Select(message => message.Content));
        var userMessages = request.Messages
            .Where(message => message.Role == LlmMessageRole.User).ToArray();
        if (userMessages.Length == 0)
        {
            throw Failure("empty_input", LlmClientErrorKind.BadRequest);
        }
        var messages = new List<object>();
        if (request.NativeContinuation is { } continuation)
        {
            if (continuation.ProviderName != ProviderName ||
                continuation.ModelId != model ||
                continuation.SystemInstruction != system ||
                userMessages.Length != 1 ||
                request.Messages.Any(message => message.Role is not
                    (LlmMessageRole.System or LlmMessageRole.User)))
                throw Failure("continuation_binding_mismatch", LlmClientErrorKind.BadRequest);
            using var previous = JsonDocument.Parse(continuation.HistoryStepsJson);
            messages.AddRange(previous.RootElement.EnumerateArray()
                .Select(item => (object)item.Clone()));
            messages.Add(new { role = "user", content = userMessages[0].Content });
        }
        else
        {
            messages.AddRange(request.Messages.Select(message => (object)new
            {
                role = message.Role switch
                {
                    LlmMessageRole.System => "system",
                    LlmMessageRole.User => "user",
                    _ => throw Failure("unsupported_role", LlmClientErrorKind.BadRequest)
                },
                content = message.Content
            }));
        }
        if (JsonSerializer.Serialize(messages).Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw Failure("continuation_too_large", LlmClientErrorKind.BadRequest);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true
        };
        if (request.StructuredOutput is { } structured)
        {
            if (structured.ResponseMimeType != "application/json")
            {
                throw Failure("unsupported_response_format", LlmClientErrorKind.BadRequest);
            }
            if (string.IsNullOrWhiteSpace(structured.JsonSchema))
            {
                body["format"] = "json";
            }
            else
            {
                try
                {
                    body["format"] = JsonSerializer.Deserialize<JsonElement>(structured.JsonSchema);
                }
                catch (JsonException exception)
                {
                    throw new LlmClientException(ProviderName,
                        "Ollama received an invalid structured-output schema.",
                        exception, LlmClientErrorKind.BadRequest,
                        errorClass: "invalid_json_schema");
                }
            }
        }
        var generation = request.GenerationOptions;
        var options = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (generation?.Temperature is { } temperature)
        {
            options["temperature"] = temperature;
        }
        if (generation?.MaxOutputTokens is { } maxTokens)
        {
            if (maxTokens <= 0)
            {
                throw Failure("invalid_max_output_tokens", LlmClientErrorKind.BadRequest);
            }
            options["num_predict"] = maxTokens;
        }
        if (options.Count > 0)
        {
            body["options"] = options;
        }
        if (generation?.Thinking is { } thought &&
            (thought.ThinkingBudgetTokens == 0 ||
             thought.ThinkingBudgetTokens == LlmThinkingOptions.DynamicBudget ||
             thought.IncludeThoughts))
        {
            body["think"] = thought.ThinkingBudgetTokens != 0;
        }
        return body;
    }

    private static LlmNativeContinuation? CreateContinuation(
        LlmRequest request,
        string model,
        Dictionary<string, object?> requestBody,
        string answer,
        string thinking)
    {
        var history = JsonSerializer.SerializeToElement(requestBody["messages"]);
        var messages = history.EnumerateArray().Select(item => (object)item.Clone()).ToList();
        messages.Add(thinking.Length == 0
            ? new { role = "assistant", content = answer } as object
            : new { role = "assistant", content = answer, thinking });
        var json = JsonSerializer.Serialize(messages);
        if (json.Length > LlmNativeContinuation.MaxPayloadCharacters)
            return null;
        var system = string.Join("\n\n", request.Messages
            .Where(message => message.Role == LlmMessageRole.System)
            .Select(message => message.Content));
        return new LlmNativeContinuation(ProviderName, model, system, json);
    }

    private static JsonElement GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) ? value : default;

    private static string? GetString(JsonElement element, string name)
    {
        var value = GetObject(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        var value = GetObject(element, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        var value = GetObject(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? count : null;
    }

    private static LlmClientErrorKind ClassifyStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmClientErrorKind.Authentication,
        HttpStatusCode.TooManyRequests => LlmClientErrorKind.RateLimited,
        >= HttpStatusCode.InternalServerError => LlmClientErrorKind.ServerError,
        _ => LlmClientErrorKind.BadRequest
    };

    private static LlmClientException Failure(string code, LlmClientErrorKind kind) =>
        new(ProviderName, $"Ollama failed: {code}.",
            errorKind: kind, errorClass: code);
}

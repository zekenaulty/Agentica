using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

/// <summary>Streaming, stateless Responses API text adapter. Native output items stay in the
/// private continuation channel; provider tool calls require a separate execution contract.</summary>
public class OpenAiResponsesLlmClient : ILlmStreamingClient
{
    public const string ProviderName = "openai";
    private const int MaxEventCharacters = 4_194_304;
    private const int MaxOutputCharacters = 4_194_304;
    private const int MaxSummaryCharacters = 262_144;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly OpenAiResponsesClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly string _providerName;
    private readonly string _keyEnvironmentVariable;
    private readonly bool _includeEncryptedReasoning;
    private readonly string _metadataPrefix;

    public OpenAiResponsesLlmClient(
        OpenAiResponsesClientOptions? options = null, HttpClient? httpClient = null)
        : this(options ?? OpenAiResponsesClientOptions.FromEnvironment(), httpClient,
            ProviderName, "OPENAI_API_KEY", includeEncryptedReasoning: false)
    {
    }

    protected OpenAiResponsesLlmClient(
        OpenAiResponsesClientOptions options, HttpClient? httpClient,
        string providerName, string keyEnvironmentVariable,
        bool includeEncryptedReasoning)
    {
        _options = options;
        _httpClient = httpClient ?? SharedHttpClient;
        _providerName = providerName;
        _keyEnvironmentVariable = keyEnvironmentVariable;
        _includeEncryptedReasoning = includeEncryptedReasoning;
        _metadataPrefix = providerName;
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request, CancellationToken cancellationToken = default)
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
        var key = _options.ApiKey ?? Environment.GetEnvironmentVariable(_keyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw Failure("missing_api_key", LlmClientErrorKind.Authentication);
        }
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId : request.ModelId;
        var body = BuildRequestBody(request, modelId);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            _options.Endpoint ?? OpenAiResponsesClientOptions.DefaultEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8,
                "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmClientException(_providerName, "Responses transport failed.",
                exception, LlmClientErrorKind.Network, errorClass: "transport_failure");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmClientException(_providerName,
                    $"Responses returned HTTP {(int)response.StatusCode}.",
                    errorKind: ClassifyStatus(response.StatusCode),
                    statusCode: (int)response.StatusCode, errorClass: "http_status");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var text = new StringBuilder();
            var summaries = new List<LlmThoughtSummary>();
            var summarySize = 0;
            var outputItems = new SortedDictionary<int, JsonElement>();
            var reasoningActivitySent = false;
            string? eventName = null;
            var data = new StringBuilder();
            var completed = false;
            while (true)
            {
                var nextLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (nextLine is null && data.Length == 0) break;
                var line = nextLine ?? string.Empty;
                if (line.Length > MaxEventCharacters ||
                    data.Length + line.Length > MaxEventCharacters)
                {
                    throw Failure("event_too_large", LlmClientErrorKind.BadRequest);
                }
                if (line.Length == 0)
                {
                    if (data.Length > 0 && data.ToString() != "[DONE]")
                    {
                        using var parsed = ParseEvent(data.ToString());
                        var root = parsed.RootElement;
                        var kind = GetString(root, "type") ?? eventName;
                        if (completed && kind is not null)
                        {
                            throw Failure("data_after_completion", LlmClientErrorKind.Transient);
                        }
                        switch (kind)
                        {
                            case "response.created":
                            case "response.in_progress":
                                yield return new LlmStreamEvent(LlmStreamEventKind.Activity, kind);
                                break;
                            case "response.output_text.delta":
                                var chunk = GetString(root, "delta");
                                if (chunk is not null)
                                {
                                    if (chunk.Length > MaxOutputCharacters - text.Length)
                                        throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                                    text.Append(chunk);
                                    yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, chunk);
                                }
                                break;
                            case "response.reasoning_summary_text.delta":
                                var summary = GetString(root, "delta");
                                if (summary is not null)
                                {
                                    if (summary.Length > MaxSummaryCharacters - summarySize)
                                        throw Failure("summary_too_large", LlmClientErrorKind.BadRequest);
                                    summarySize += summary.Length;
                                    summaries.Add(new LlmThoughtSummary(summary, _providerName));
                                    yield return new LlmStreamEvent(
                                        LlmStreamEventKind.ThoughtSummaryDelta, summary);
                                }
                                break;
                            case "response.reasoning_text.delta":
                                if (!reasoningActivitySent)
                                {
                                    reasoningActivitySent = true;
                                    yield return new LlmStreamEvent(
                                        LlmStreamEventKind.Activity, "reasoning.streaming");
                                }
                                break;
                            case "response.output_item.done":
                                var index = GetInt(root, "output_index");
                                var item = GetObject(root, "item");
                                if (index is null || index < 0 || item.ValueKind != JsonValueKind.Object ||
                                    !outputItems.TryAdd(index.Value, item.Clone()))
                                    throw Failure("invalid_output_item", LlmClientErrorKind.Transient);
                                break;
                            case "response.completed":
                                var terminal = GetObject(root, "response");
                                if (GetString(terminal, "status") != "completed")
                                    throw Failure("response_not_completed", LlmClientErrorKind.Transient);
                                var output = GetObject(terminal, "output");
                                if (output.ValueKind != JsonValueKind.Array)
                                {
                                    if (outputItems.Count == 0)
                                        throw Failure("missing_output", LlmClientErrorKind.Transient);
                                    if (outputItems.Keys.Where((value, position) => value != position).Any())
                                        throw Failure("missing_output_item", LlmClientErrorKind.Transient);
                                    output = JsonSerializer.SerializeToElement(outputItems.Values);
                                }
                                var finalText = ExtractTextAndValidate(output);
                                if (text.Length > 0 && !string.Equals(text.ToString(), finalText,
                                        StringComparison.Ordinal))
                                    throw Failure("output_mismatch", LlmClientErrorKind.Transient);
                                if (finalText.Length > MaxOutputCharacters)
                                    throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                                var usage = GetObject(terminal, "usage");
                                var inputDetails = GetObject(usage, "input_tokens_details");
                                var outputDetails = GetObject(usage, "output_tokens_details");
                                var native = CreateContinuation(request, modelId, output);
                                completed = true;
                                yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                                    Response: new LlmResponse(_providerName, modelId, finalText,
                                        request.StructuredOutput is null ? null : finalText,
                                        summaries.AsReadOnly(),
                                        new LlmUsage(GetInt(usage, "input_tokens"),
                                            GetInt(usage, "output_tokens"),
                                            GetInt(outputDetails, "reasoning_tokens"),
                                            GetInt(usage, "total_tokens"),
                                            GetInt(inputDetails, "cached_tokens")),
                                        LlmFinishReason.Stop,
                                        new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            [$"{_metadataPrefix}.response.id"] = GetString(terminal, "id") ?? string.Empty,
                                            [$"{_metadataPrefix}.response.status"] = "completed",
                                            [$"{_metadataPrefix}.response.store"] = "false",
                                            [$"{_metadataPrefix}.continuation.available"] =
                                                (native is not null).ToString()
                                        }, native));
                                break;
                            case "response.failed":
                            case "response.incomplete":
                            case "error":
                                throw Failure(kind, LlmClientErrorKind.Unknown);
                        }
                    }
                    eventName = null;
                    data.Clear();
                    if (nextLine is null) break;
                    continue;
                }
                if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventName = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line[5..].TrimStart());
                }
            }
            if (!completed) throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
        }
    }

    internal Dictionary<string, object?> BuildRequestBody(LlmRequest request, string modelId)
    {
        if (request.Messages.Any(message => message.Role is LlmMessageRole.Assistant or LlmMessageRole.Tool))
            throw Failure("native_history_required", LlmClientErrorKind.BadRequest);
        if (request.GenerationOptions?.Temperature is not null)
            throw Failure("unsupported_temperature", LlmClientErrorKind.BadRequest);
        if (request.GenerationOptions?.Thinking?.ThinkingBudgetTokens is > 0 or 0)
            throw Failure("unsupported_thinking_budget", LlmClientErrorKind.BadRequest);
        var instruction = string.Join("\n\n", request.Messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        var userMessages = request.Messages.Where(message => message.Role == LlmMessageRole.User)
            .Where(message => !string.IsNullOrWhiteSpace(message.Content)).ToArray();
        if (userMessages.Length == 0) throw Failure("empty_input", LlmClientErrorKind.BadRequest);

        var input = new List<object>();
        if (request.NativeContinuation is { } continuation)
        {
            if (continuation.ProviderName != _providerName || continuation.ModelId != modelId ||
                continuation.SystemInstruction != instruction || userMessages.Length != 1)
                throw Failure("continuation_binding_mismatch", LlmClientErrorKind.BadRequest);
            using var history = JsonDocument.Parse(continuation.HistoryStepsJson);
            input.AddRange(history.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
        }
        foreach (var user in userMessages)
            input.Add(new { role = "user", content = user.Content });
        if (JsonSerializer.Serialize(input).Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw Failure("continuation_too_large", LlmClientErrorKind.BadRequest);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false
        };
        if (instruction.Length > 0) body["instructions"] = instruction;
        if (_includeEncryptedReasoning)
            body["include"] = new[] { "reasoning.encrypted_content" };
        if (request.GenerationOptions?.MaxOutputTokens is { } maxTokens)
        {
            if (maxTokens <= 0) throw Failure("invalid_max_output_tokens", LlmClientErrorKind.BadRequest);
            body["max_output_tokens"] = maxTokens;
        }
        if (request.GenerationOptions?.Thinking?.IncludeThoughts == true)
            body["reasoning"] = new { summary = "auto" };
        if (request.StructuredOutput is { } structured)
        {
            if (structured.ResponseMimeType != "application/json")
                throw Failure("unsupported_response_format", LlmClientErrorKind.BadRequest);
            if (!string.IsNullOrWhiteSpace(structured.JsonSchema))
            {
                try { using var _ = JsonDocument.Parse(structured.JsonSchema); }
                catch (JsonException exception)
                {
                    throw new LlmClientException(_providerName, "Responses schema must be valid JSON.",
                        exception, LlmClientErrorKind.BadRequest, errorClass: "invalid_json_schema");
                }
            }
            body["text"] = new { format = new { type = "json_object" } };
        }
        return body;
    }

    private string ExtractTextAndValidate(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Array)
            throw Failure("missing_output", LlmClientErrorKind.Transient);
        if (output.GetArrayLength() == 0)
            throw Failure("missing_output", LlmClientErrorKind.Transient);
        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            var kind = GetString(item, "type");
            if (kind == "reasoning") continue;
            if (kind != "message" || GetString(item, "role") != "assistant")
                throw Failure("unsupported_output_item", LlmClientErrorKind.BadRequest);
            var content = GetObject(item, "content");
            if (content.ValueKind != JsonValueKind.Array)
                throw Failure("invalid_output_message", LlmClientErrorKind.Transient);
            foreach (var part in content.EnumerateArray())
            {
                if (GetString(part, "type") == "refusal")
                    throw Failure("refusal", LlmClientErrorKind.Safety);
                if (GetString(part, "type") != "output_text")
                    throw Failure("unsupported_output_content", LlmClientErrorKind.BadRequest);
                var chunk = GetString(part, "text") ?? string.Empty;
                if (chunk.Length > MaxOutputCharacters - text.Length)
                    throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                text.Append(chunk);
            }
        }
        return text.ToString();
    }

    private LlmNativeContinuation? CreateContinuation(
        LlmRequest request, string modelId, JsonElement output)
    {
        if (output.GetArrayLength() == 0) return null;
        foreach (var item in output.EnumerateArray())
        {
            if (GetString(item, "type") == "reasoning" &&
                string.IsNullOrWhiteSpace(GetString(item, "encrypted_content")))
                return null;
        }
        var instruction = string.Join("\n\n", request.Messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        var history = new List<object>();
        if (request.NativeContinuation is { } previous)
        {
            using var parsed = JsonDocument.Parse(previous.HistoryStepsJson);
            history.AddRange(parsed.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
        }
        history.AddRange(request.Messages.Where(message => message.Role == LlmMessageRole.User &&
            !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => (object)new { role = "user", content = message.Content }));
        history.AddRange(output.EnumerateArray().Select(item => (object)item.Clone()));
        var json = JsonSerializer.Serialize(history);
        return json.Length <= LlmNativeContinuation.MaxPayloadCharacters
            ? new LlmNativeContinuation(_providerName, modelId, instruction, json)
            : null;
    }

    private JsonDocument ParseEvent(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException exception)
        {
            throw new LlmClientException(_providerName, "Responses returned malformed stream data.",
                exception, LlmClientErrorKind.Transient, errorClass: "malformed_event");
        }
    }
    private static JsonElement GetObject(JsonElement source, string key) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var value)
            ? value : default;
    private static string? GetString(JsonElement source, string key)
    {
        var value = GetObject(source, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int? GetInt(JsonElement source, string key)
    {
        var value = GetObject(source, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;
    }
    private static LlmClientErrorKind ClassifyStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmClientErrorKind.Authentication,
        HttpStatusCode.TooManyRequests => LlmClientErrorKind.RateLimited,
        >= HttpStatusCode.InternalServerError => LlmClientErrorKind.ServerError,
        _ => LlmClientErrorKind.BadRequest
    };
    private LlmClientException Failure(string code, LlmClientErrorKind kind) =>
        new(_providerName, $"Responses failed: {code}.",
            errorKind: kind, errorClass: code);
}

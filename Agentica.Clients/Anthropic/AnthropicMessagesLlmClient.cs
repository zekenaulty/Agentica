using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Anthropic;

/// <summary>Streaming Messages API adapter with private, signed thinking block replay.</summary>
public sealed class AnthropicMessagesLlmClient : ILlmStreamingClient
{
    public const string ProviderName = "anthropic";
    private const int MaxEventCharacters = 4_194_304;
    private const int MaxOutputCharacters = 4_194_304;
    private const int MaxSummaryCharacters = 262_144;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly AnthropicMessagesClientOptions _options;
    private readonly HttpClient _httpClient;

    public AnthropicMessagesLlmClient(
        AnthropicMessagesClientOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? AnthropicMessagesClientOptions.FromEnvironment();
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request, CancellationToken cancellationToken = default)
    {
        LlmResponse? completed = null;
        await foreach (var item in StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind == LlmStreamEventKind.Completed) completed = item.Response;
        }
        return completed ?? throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = _options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw Failure("missing_api_key", LlmClientErrorKind.Authentication);
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId : request.ModelId;
        var body = BuildRequestBody(request, modelId);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            _options.Endpoint ?? AnthropicMessagesClientOptions.DefaultEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8,
                "application/json")
        };
        message.Headers.TryAddWithoutValidation("x-api-key", key);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        message.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmClientException(ProviderName, "Anthropic transport failed.",
                exception, LlmClientErrorKind.Network, errorClass: "transport_failure");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new LlmClientException(ProviderName,
                    $"Anthropic Messages returned HTTP {(int)response.StatusCode}.",
                    errorKind: ClassifyStatus(response.StatusCode),
                    statusCode: (int)response.StatusCode, errorClass: "http_status");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var blocks = new SortedDictionary<int, NativeBlock>();
            var output = new StringBuilder();
            var summaries = new List<LlmThoughtSummary>();
            var summarySize = 0;
            string? messageId = null;
            string? stopReason = null;
            int? inputTokens = null;
            int? outputTokens = null;
            int? cacheReadTokens = null;
            string? eventName = null;
            var data = new StringBuilder();
            var started = false;
            var completed = false;
            while (true)
            {
                var nextLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (nextLine is null && data.Length == 0) break;
                var line = nextLine ?? string.Empty;
                if (line.Length > MaxEventCharacters ||
                    data.Length + line.Length > MaxEventCharacters)
                    throw Failure("event_too_large", LlmClientErrorKind.BadRequest);
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        using var parsed = ParseEvent(data.ToString());
                        var root = parsed.RootElement;
                        var kind = GetString(root, "type") ?? eventName;
                        if (completed)
                            throw Failure("data_after_completion", LlmClientErrorKind.Transient);
                        switch (kind)
                        {
                            case "message_start":
                                if (started) throw Failure("duplicate_message_start", LlmClientErrorKind.Transient);
                                started = true;
                                var initial = GetObject(root, "message");
                                messageId = GetString(initial, "id");
                                var initialUsage = GetObject(initial, "usage");
                                inputTokens = GetInt(initialUsage, "input_tokens");
                                outputTokens = GetInt(initialUsage, "output_tokens");
                                cacheReadTokens = GetInt(initialUsage, "cache_read_input_tokens");
                                yield return new LlmStreamEvent(LlmStreamEventKind.Activity,
                                    "message_start");
                                break;
                            case "content_block_start":
                                var startIndex = GetInt(root, "index");
                                var initialBlock = GetObject(root, "content_block");
                                if (!started || startIndex is null || startIndex < 0 ||
                                    initialBlock.ValueKind != JsonValueKind.Object ||
                                    blocks.ContainsKey(startIndex.Value))
                                    throw Failure("invalid_block_start", LlmClientErrorKind.Transient);
                                blocks[startIndex.Value] = new NativeBlock(initialBlock);
                                break;
                            case "content_block_delta":
                                var index = GetInt(root, "index");
                                if (index is null || !blocks.TryGetValue(index.Value, out var block))
                                    throw Failure("unknown_block_delta", LlmClientErrorKind.Transient);
                                var delta = GetObject(root, "delta");
                                var deltaType = GetString(delta, "type");
                                block.Apply(delta);
                                if (deltaType == "text_delta")
                                {
                                    var chunk = GetString(delta, "text") ?? string.Empty;
                                    if (chunk.Length > MaxOutputCharacters - output.Length)
                                        throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                                    output.Append(chunk);
                                    yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, chunk);
                                }
                                else if (deltaType == "thinking_delta")
                                {
                                    var chunk = GetString(delta, "thinking") ?? string.Empty;
                                    if (chunk.Length > MaxSummaryCharacters - summarySize)
                                        throw Failure("summary_too_large", LlmClientErrorKind.BadRequest);
                                    summarySize += chunk.Length;
                                    summaries.Add(new LlmThoughtSummary(chunk, ProviderName));
                                    yield return new LlmStreamEvent(
                                        LlmStreamEventKind.ThoughtSummaryDelta, chunk);
                                }
                                break;
                            case "content_block_stop":
                                var stopIndex = GetInt(root, "index");
                                if (stopIndex is null || !blocks.TryGetValue(stopIndex.Value, out var stopped))
                                    throw Failure("unknown_block_stop", LlmClientErrorKind.Transient);
                                stopped.Stop();
                                break;
                            case "message_delta":
                                stopReason = GetString(GetObject(root, "delta"), "stop_reason")
                                    ?? stopReason;
                                var finalUsage = GetObject(root, "usage");
                                // These are cumulative totals; omitted fields retain the last value.
                                inputTokens = GetInt(finalUsage, "input_tokens") ?? inputTokens;
                                outputTokens = GetInt(finalUsage, "output_tokens") ?? outputTokens;
                                cacheReadTokens = GetInt(finalUsage, "cache_read_input_tokens")
                                    ?? cacheReadTokens;
                                break;
                            case "message_stop":
                                if (!started || stopReason is null || blocks.Count == 0 ||
                                    blocks.Keys.Where((value, position) => value != position).Any() ||
                                    blocks.Values.Any(value => !value.IsStopped))
                                    throw Failure("stream_incomplete", LlmClientErrorKind.Transient);
                                if (stopReason is not ("end_turn" or "max_tokens"))
                                    throw Failure("unexpected_stop_reason", LlmClientErrorKind.BadRequest);
                                var nativeBlocks = blocks.Values.Select(value => value.Complete()).ToArray();
                                var finalText = ExtractTextAndValidate(nativeBlocks);
                                if (output.Length > 0 && output.ToString() != finalText)
                                    throw Failure("output_mismatch", LlmClientErrorKind.Transient);
                                var continuation = stopReason == "end_turn"
                                    ? CreateContinuation(request, modelId, nativeBlocks) : null;
                                completed = true;
                                yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                                    Response: new LlmResponse(ProviderName, modelId, finalText,
                                        request.StructuredOutput is null ? null : finalText,
                                        summaries.AsReadOnly(),
                                        new LlmUsage(inputTokens, outputTokens, null,
                                            inputTokens is not null && outputTokens is not null
                                                ? inputTokens + outputTokens : null,
                                            cacheReadTokens),
                                        stopReason == "end_turn"
                                            ? LlmFinishReason.Stop : LlmFinishReason.MaxTokens,
                                        new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            ["anthropic.message.id"] = messageId ?? string.Empty,
                                            ["anthropic.stop_reason"] = stopReason,
                                            ["anthropic.continuation.available"] =
                                                (continuation is not null).ToString()
                                        }, continuation));
                                break;
                            case "error":
                                throw Failure("stream_error", LlmClientErrorKind.Unknown);
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

    internal static Dictionary<string, object?> BuildRequestBody(LlmRequest request, string modelId)
    {
        if (request.Messages.Any(message => message.Role is LlmMessageRole.Assistant or LlmMessageRole.Tool))
            throw Failure("native_history_required", LlmClientErrorKind.BadRequest);
        if (request.GenerationOptions?.Temperature is not null)
            throw Failure("unsupported_temperature", LlmClientErrorKind.BadRequest);
        var system = string.Join("\n\n", request.Messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content).Where(value => !string.IsNullOrWhiteSpace(value)));
        var userMessages = request.Messages.Where(message => message.Role == LlmMessageRole.User &&
            !string.IsNullOrWhiteSpace(message.Content)).ToArray();
        if (userMessages.Length == 0) throw Failure("empty_input", LlmClientErrorKind.BadRequest);
        var messages = new List<object>();
        if (request.NativeContinuation is { } continuation)
        {
            if (continuation.ProviderName != ProviderName || continuation.ModelId != modelId ||
                continuation.SystemInstruction != system || userMessages.Length != 1)
                throw Failure("continuation_binding_mismatch", LlmClientErrorKind.BadRequest);
            using var previous = JsonDocument.Parse(continuation.HistoryStepsJson);
            messages.AddRange(previous.RootElement.EnumerateArray()
                .Select(item => (object)item.Clone()));
        }
        messages.AddRange(userMessages.Select(item => (object)new
        {
            role = "user",
            content = item.Content
        }));
        if (JsonSerializer.Serialize(messages).Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw Failure("continuation_too_large", LlmClientErrorKind.BadRequest);

        var maxTokens = request.GenerationOptions?.MaxOutputTokens ?? 4096;
        if (maxTokens <= 0) throw Failure("invalid_max_output_tokens", LlmClientErrorKind.BadRequest);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["stream"] = true
        };
        if (system.Length > 0) body["system"] = system;
        var thinking = request.GenerationOptions?.Thinking;
        var effort = thinking?.GetEffortValue(ProviderName);
        if (thinking?.Effort is LlmReasoningEffort.None or LlmReasoningEffort.Minimal)
            throw Failure("unsupported_reasoning_effort", LlmClientErrorKind.BadRequest);
        if (effort is not null) body["output_config"] = new { effort };
        if (thinking?.ThinkingBudgetTokens is 0)
        {
            if (thinking.IncludeThoughts)
                throw Failure("disabled_thinking_summary", LlmClientErrorKind.BadRequest);
            body["thinking"] = new { type = "disabled" };
        }
        else if (thinking?.ThinkingBudgetTokens is > 0)
        {
            var budget = thinking.ThinkingBudgetTokens.Value;
            if (budget < 1024 || budget >= maxTokens)
                throw Failure("invalid_thinking_budget", LlmClientErrorKind.BadRequest);
            body["thinking"] = new
            {
                type = "enabled",
                budget_tokens = budget,
                display = thinking.IncludeThoughts ? "summarized" : "omitted"
            };
        }
        else if (thinking is not null)
        {
            body["thinking"] = new
            {
                type = "adaptive",
                display = thinking.IncludeThoughts ? "summarized" : "omitted"
            };
        }
        if (request.StructuredOutput is { } structured)
        {
            if (structured.ResponseMimeType != "application/json")
                throw Failure("unsupported_response_format", LlmClientErrorKind.BadRequest);
            if (!string.IsNullOrWhiteSpace(structured.JsonSchema))
            {
                try { using var _ = JsonDocument.Parse(structured.JsonSchema); }
                catch (JsonException exception)
                {
                    throw new LlmClientException(ProviderName, "Anthropic schema must be valid JSON.",
                        exception, LlmClientErrorKind.BadRequest, errorClass: "invalid_json_schema");
                }
            }
            // Workflow schemas allow arbitrary tool-input objects. The API's constrained JSON
            // schema subset cannot represent them; host-side workflow validation remains binding.
        }
        return body;
    }

    private static string ExtractTextAndValidate(IReadOnlyList<JsonObject> blocks)
    {
        var output = new StringBuilder();
        foreach (var block in blocks)
        {
            var type = block["type"]?.GetValue<string>();
            if (type is "thinking" or "redacted_thinking") continue;
            if (type != "text")
                throw Failure("unsupported_content_block", LlmClientErrorKind.BadRequest);
            var chunk = block["text"]?.GetValue<string>() ?? string.Empty;
            if (chunk.Length > MaxOutputCharacters - output.Length)
                throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
            output.Append(chunk);
        }
        return output.ToString();
    }

    private static LlmNativeContinuation? CreateContinuation(
        LlmRequest request, string modelId, IReadOnlyList<JsonObject> blocks)
    {
        foreach (var block in blocks)
        {
            var type = block["type"]?.GetValue<string>();
            if (type == "thinking" &&
                string.IsNullOrWhiteSpace(block["signature"]?.GetValue<string>()))
                return null;
            if (type == "redacted_thinking" &&
                string.IsNullOrWhiteSpace(block["data"]?.GetValue<string>()))
                return null;
        }
        var system = string.Join("\n\n", request.Messages
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(message => message.Content).Where(value => !string.IsNullOrWhiteSpace(value)));
        var history = new List<object>();
        if (request.NativeContinuation is { } previous)
        {
            using var parsed = JsonDocument.Parse(previous.HistoryStepsJson);
            history.AddRange(parsed.RootElement.EnumerateArray()
                .Select(item => (object)item.Clone()));
        }
        history.AddRange(request.Messages.Where(message => message.Role == LlmMessageRole.User &&
            !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => (object)new { role = "user", content = message.Content }));
        history.Add(new { role = "assistant", content = blocks });
        var json = JsonSerializer.Serialize(history);
        return json.Length <= LlmNativeContinuation.MaxPayloadCharacters
            ? new LlmNativeContinuation(ProviderName, modelId, system, json)
            : null;
    }

    private sealed class NativeBlock
    {
        private readonly JsonObject _block;
        private readonly string _type;
        public NativeBlock(JsonElement initial)
        {
            _block = JsonNode.Parse(initial.GetRawText()) as JsonObject
                ?? throw Failure("invalid_block", LlmClientErrorKind.Transient);
            _type = _block["type"]?.GetValue<string>() ?? string.Empty;
            if (_type is not ("text" or "thinking" or "redacted_thinking"))
                throw Failure("unsupported_content_block", LlmClientErrorKind.BadRequest);
        }
        public bool IsStopped { get; private set; }
        public void Apply(JsonElement delta)
        {
            if (IsStopped) throw Failure("delta_after_block_stop", LlmClientErrorKind.Transient);
            var type = GetString(delta, "type");
            if (_type == "text" && type == "text_delta")
                Append("text", GetString(delta, "text"));
            else if (_type == "thinking" && type == "thinking_delta")
                Append("thinking", GetString(delta, "thinking"));
            else if (_type == "thinking" && type == "signature_delta")
                _block["signature"] = GetString(delta, "signature")
                    ?? throw Failure("missing_signature", LlmClientErrorKind.Transient);
            else
                throw Failure("unsupported_block_delta", LlmClientErrorKind.BadRequest);
        }
        private void Append(string key, string? value)
        {
            if (value is null) throw Failure("missing_block_delta", LlmClientErrorKind.Transient);
            var existing = _block[key]?.GetValue<string>() ?? string.Empty;
            if (value.Length > MaxEventCharacters - existing.Length)
                throw Failure("block_too_large", LlmClientErrorKind.BadRequest);
            _block[key] = existing + value;
        }
        public void Stop()
        {
            if (IsStopped) throw Failure("duplicate_block_stop", LlmClientErrorKind.Transient);
            IsStopped = true;
        }
        public JsonObject Complete() => _block;
    }

    private static JsonDocument ParseEvent(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException exception)
        {
            throw new LlmClientException(ProviderName, "Anthropic returned malformed stream data.",
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
    private static LlmClientException Failure(string code, LlmClientErrorKind kind) =>
        new(ProviderName, $"Anthropic Messages failed: {code}.",
            errorKind: kind, errorClass: code);
}

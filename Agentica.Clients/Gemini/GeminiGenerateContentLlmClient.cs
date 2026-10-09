using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentica.Clients.Llm;

namespace Agentica.Clients.Gemini;

/// <summary>
/// Streams text plans through the Gemini Developer API. Exact ordered response parts,
/// including opaque signatures, stay in private, transport-bound continuation custody.
/// Provider-native function calls and multimedia require separate execution contracts.
/// </summary>
public sealed class GeminiGenerateContentLlmClient : ILlmStreamingClient
{
    // GenerateContent contents and Interactions steps are incompatible native histories.
    public const string ProviderName = "Gemini.GenerateContent";
    private const int MaxEventCharacters = 1_048_576;
    private const int MaxOutputCharacters = 4_194_304;
    private const int MaxSummaryCharacters = 262_144;
    private const int MaxParts = 8192;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly GeminiGenerateContentClientOptions _options;
    private readonly HttpClient _httpClient;

    public GeminiGenerateContentLlmClient(
        GeminiGenerateContentClientOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? GeminiGenerateContentClientOptions.FromEnvironment();
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        LlmResponse? completed = null;
        await foreach (var item in StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (item.Kind == LlmStreamEventKind.Completed) completed = item.Response;
        return completed ?? throw Failure("stream_incomplete");
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var apiKey = _options.ApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Failure("missing_api_key", LlmClientErrorKind.Authentication);
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? _options.DefaultModelId : request.ModelId;
        var body = BuildRequest(request, model, out var instruction);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint ?? Endpoint(model))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        message.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LlmClientException(ProviderName,
                $"Gemini GenerateContent returned HTTP {(int)response.StatusCode}.",
                errorKind: ClassifyStatus(response.StatusCode), statusCode: (int)response.StatusCode,
                errorClass: "http_status");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var state = new ResponseState(request.GenerationOptions?.Thinking?.IncludeThoughts == true);
        var data = new StringBuilder();
        while (true)
        {
            var next = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (next is null && data.Length == 0) break;
            var line = next ?? string.Empty;
            if (line.Length > MaxEventCharacters || data.Length + line.Length > MaxEventCharacters)
                throw Failure("event_too_large", LlmClientErrorKind.BadRequest);
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var chunk = ParseEvent(data.ToString());
                    foreach (var item in state.Apply(chunk.RootElement)) yield return item;
                    data.Clear();
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
            if (next is null) break;
        }

        if (state.FinishReason is null) throw Failure("stream_incomplete");
        LlmNativeContinuation? continuation = null;
        if (state.FinishReason == LlmFinishReason.Stop && state.Text.Length > 0 && state.Parts.Count > 0)
        {
            var history = (JsonArray)body["contents"]!.DeepClone();
            history.Add(new JsonObject { ["role"] = "model", ["parts"] = state.Parts });
            var serialized = history.ToJsonString();
            if (serialized.Length <= LlmNativeContinuation.MaxPayloadCharacters)
                continuation = new LlmNativeContinuation(ProviderName, model, instruction, serialized);
        }
        var text = state.Text.ToString();
        yield return new LlmStreamEvent(LlmStreamEventKind.Completed, Response: new LlmResponse(
            ProviderName, model, text,
            request.StructuredOutput?.ResponseMimeType == "application/json" ? text : null,
            state.Summaries, state.Usage, state.FinishReason.Value,
            NativeContinuation: continuation));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Transport exceptions can include request details; expose only a stable classification.
            throw Failure("transport_failure", LlmClientErrorKind.Network);
        }
    }

    private static Uri Endpoint(string model)
    {
        var id = model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw Failure("invalid_model_id", LlmClientErrorKind.BadRequest);
        return new Uri("https://generativelanguage.googleapis.com/v1beta/models/" + id + ":streamGenerateContent?alt=sse");
    }

    private static JsonObject BuildRequest(LlmRequest request, string model, out string instruction)
    {
        if (request.Messages.Any(item => item.Role is not (LlmMessageRole.System or LlmMessageRole.Developer or LlmMessageRole.User)))
            throw Failure("native_history_required", LlmClientErrorKind.BadRequest);
        instruction = string.Join("\n\n", request.Messages
            .Where(item => item.Role is LlmMessageRole.System or LlmMessageRole.Developer)
            .Select(item => item.Content).Where(item => !string.IsNullOrWhiteSpace(item)));
        var input = string.Join("\n\n", request.Messages.Where(item => item.Role == LlmMessageRole.User)
            .Select(item => item.Content).Where(item => !string.IsNullOrWhiteSpace(item)));
        if (string.IsNullOrWhiteSpace(input)) throw Failure("empty_input", LlmClientErrorKind.BadRequest);
        var contents = new JsonArray();
        if (request.NativeContinuation is { } previous)
        {
            if (previous.ProviderName != ProviderName || previous.ModelId != model ||
                previous.SystemInstruction != instruction ||
                request.Messages.Count(item => item.Role == LlmMessageRole.User) != 1)
                throw Failure("continuation_binding_mismatch", LlmClientErrorKind.BadRequest);
            contents = JsonNode.Parse(previous.HistoryStepsJson)!.AsArray();
        }
        contents.Add(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = input })
        });
        if (contents.ToJsonString().Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw Failure("input_too_large", LlmClientErrorKind.BadRequest);
        var config = new JsonObject { ["candidateCount"] = 1 };
        if (request.GenerationOptions?.Temperature is { } temperature)
        {
            if (!double.IsFinite(temperature) || temperature < 0 || temperature > 2)
                throw Failure("invalid_temperature", LlmClientErrorKind.BadRequest);
            config["temperature"] = temperature;
        }
        if (request.GenerationOptions?.MaxOutputTokens is { } maxTokens)
        {
            if (maxTokens <= 0) throw Failure("invalid_max_output_tokens", LlmClientErrorKind.BadRequest);
            config["maxOutputTokens"] = maxTokens;
        }
        if (request.GenerationOptions?.Thinking is { } thinking)
        {
            var mapped = GeminiThinkingOptionsMapper.Map(thinking);
            var control = new JsonObject();
            if (mapped.ThinkingBudget is { } budget) control["thinkingBudget"] = budget;
            if (mapped.ThinkingLevel is { } level) control["thinkingLevel"] = level;
            if (mapped.IncludeThoughts is { } include) control["includeThoughts"] = include;
            config["thinkingConfig"] = control;
        }
        if (request.StructuredOutput is { } structured)
        {
            if (structured.ResponseMimeType != "application/json")
                throw Failure("unsupported_response_format", LlmClientErrorKind.BadRequest);
            config["responseMimeType"] = structured.ResponseMimeType;
            if (!string.IsNullOrWhiteSpace(structured.JsonSchema))
            {
                try { config["responseJsonSchema"] = JsonNode.Parse(structured.JsonSchema); }
                catch (JsonException) { throw Failure("invalid_json_schema", LlmClientErrorKind.BadRequest); }
            }
        }
        var body = new JsonObject { ["contents"] = contents, ["generationConfig"] = config };
        if (instruction.Length > 0)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instruction }) };
        return body;
    }

    private sealed class ResponseState(bool includeSummaries)
    {
        private int _summaryCharacters;
        private int _nativeCharacters;
        private bool _thinkingObserved;
        public StringBuilder Text { get; } = new();
        public JsonArray Parts { get; } = [];
        public List<LlmThoughtSummary> Summaries { get; } = [];
        public LlmFinishReason? FinishReason { get; private set; }
        public LlmUsage? Usage { get; private set; }

        public IEnumerable<LlmStreamEvent> Apply(JsonElement chunk)
        {
            if (chunk.ValueKind != JsonValueKind.Object) throw Failure("invalid_event");
            if (chunk.TryGetProperty("error", out _)) throw Failure("provider_error");
            if (chunk.TryGetProperty("usageMetadata", out var usage))
                Usage = new LlmUsage(Number(usage, "promptTokenCount"), Number(usage, "candidatesTokenCount"),
                    Number(usage, "thoughtsTokenCount"), Number(usage, "totalTokenCount"),
                    Number(usage, "cachedContentTokenCount"), Number(usage, "toolUsePromptTokenCount"));
            if (chunk.TryGetProperty("promptFeedback", out var feedback) &&
                String(feedback, "blockReason") is { } block && block != "BLOCK_REASON_UNSPECIFIED")
            {
                if (FinishReason is not null || Text.Length > 0 || Parts.Count > 0) throw Failure("conflicting_terminal");
                FinishReason = LlmFinishReason.Blocked;
            }
            if (!chunk.TryGetProperty("candidates", out var candidates)) yield break;
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 1)
                throw Failure("unsupported_candidates");
            if (candidates.GetArrayLength() == 0) yield break;
            if (FinishReason is not null) throw Failure("data_after_terminal");
            var candidate = candidates[0];
            if (candidate.ValueKind != JsonValueKind.Object || Number(candidate, "index") is not (null or 0))
                throw Failure("unsupported_candidates");
            if (candidate.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Object || String(content, "role") is not (null or "model"))
                    throw Failure("invalid_content");
                if (content.TryGetProperty("parts", out var parts))
                {
                    if (parts.ValueKind != JsonValueKind.Array) throw Failure("invalid_parts");
                    foreach (var part in parts.EnumerateArray())
                    {
                        ValidatePart(part);
                        var raw = part.GetRawText();
                        if (Parts.Count >= MaxParts || raw.Length > LlmNativeContinuation.MaxPayloadCharacters - _nativeCharacters)
                            throw Failure("native_output_too_large", LlmClientErrorKind.BadRequest);
                        _nativeCharacters += raw.Length;
                        // Never merge parts: a signed empty text part is significant, as is its position.
                        Parts.Add(JsonNode.Parse(raw));
                        var text = String(part, "text") ?? string.Empty;
                        var thought = part.TryGetProperty("thought", out var flag) && flag.GetBoolean();
                        if (thought)
                        {
                            if (!_thinkingObserved)
                            {
                                _thinkingObserved = true;
                                yield return new LlmStreamEvent(LlmStreamEventKind.Activity, "thinking");
                            }
                            if (text.Length > MaxSummaryCharacters - _summaryCharacters)
                                throw Failure("summary_too_large", LlmClientErrorKind.BadRequest);
                            _summaryCharacters += text.Length;
                            if (includeSummaries && text.Length > 0)
                            {
                                Summaries.Add(new LlmThoughtSummary(text, ProviderName));
                                yield return new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta, text);
                            }
                        }
                        else if (text.Length > 0)
                        {
                            if (text.Length > MaxOutputCharacters - Text.Length)
                                throw Failure("output_too_large", LlmClientErrorKind.BadRequest);
                            Text.Append(text);
                            yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, text);
                        }
                    }
                }
            }
            if (String(candidate, "finishReason") is { } reason && reason != "FINISH_REASON_UNSPECIFIED")
                FinishReason = reason switch
                {
                    "STOP" => LlmFinishReason.Stop,
                    "MAX_TOKENS" => LlmFinishReason.MaxTokens,
                    "SAFETY" => LlmFinishReason.Safety,
                    "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "RECITATION" => LlmFinishReason.Blocked,
                    "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" => LlmFinishReason.Error,
                    _ => LlmFinishReason.Other
                };
        }
    }

    private static void ValidatePart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object) throw Failure("invalid_part");
        foreach (var property in part.EnumerateObject())
        {
            if (property.Name is not ("text" or "thought" or "thoughtSignature"))
                throw Failure("unsupported_output_part", LlmClientErrorKind.BadRequest);
            if (property.Name == "thought" && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                property.Name != "thought" && property.Value.ValueKind != JsonValueKind.String)
                throw Failure("invalid_part");
        }
        if (!part.TryGetProperty("text", out _) && !part.TryGetProperty("thoughtSignature", out _))
            throw Failure("invalid_part");
    }

    private static JsonDocument ParseEvent(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw Failure("invalid_event"); }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static LlmClientErrorKind ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmClientErrorKind.Authentication,
        HttpStatusCode.TooManyRequests => LlmClientErrorKind.RateLimited,
        >= HttpStatusCode.InternalServerError => LlmClientErrorKind.ServerError,
        _ => LlmClientErrorKind.BadRequest
    };

    private static LlmClientException Failure(string code, LlmClientErrorKind kind = LlmClientErrorKind.Transient) =>
        new(ProviderName, $"Gemini GenerateContent failed: {code}.", errorKind: kind, errorClass: code);
}

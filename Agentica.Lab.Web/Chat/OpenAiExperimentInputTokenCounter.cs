using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agentica.Lab.Web.Chat;

/// <summary>Turn-owned, stateless native input counts. The borrowed client is never disposed.</summary>
public sealed class OpenAiExperimentInputTokenCounter : IExperimentInputTokenCounter, IDisposable
{
    private const int MaximumResponseBytes = 65_536;
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses/input_tokens");
    private static readonly string[] CountFields =
    ["model", "input", "instructions", "reasoning", "text", "tools", "tool_choice", "parallel_tool_calls", "truncation"];
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly int _maximumCalls;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _measurementGate = new();
    private readonly Dictionary<string, ExperimentTokenCount> _cache = new(StringComparer.Ordinal);
    private readonly List<ExperimentTokenCount> _measurements = [];
    private int _calls;

    public OpenAiExperimentInputTokenCounter(HttpClient borrowed, string apiKey, int maximumCalls = 132)
    {
        ArgumentNullException.ThrowIfNull(borrowed);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new ArgumentException("A valid provider credential is required for native input counts.", nameof(apiKey));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCalls, 1);
        _http = borrowed; _apiKey = apiKey; _maximumCalls = maximumCalls;
    }

    public IReadOnlyList<ExperimentTokenCount> Measurements
    {
        get { lock (_measurementGate) return _measurements.ToArray(); }
    }

    public void Dispose() => _gate.Dispose();

    public async Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestHash = Convert.ToHexStringLower(SHA256.HashData(request.Span));
            if (_cache.TryGetValue(requestHash, out var cached)) return cached;
            byte[] countBody;
            try { countBody = BuildCountRequest(request); }
            catch (JsonException) { throw InvalidInput(); }
            if (_calls >= _maximumCalls)
                throw new ExperimentException("context.token_count_limit",
                    "The native input-count request limit was reached. No generation request was dispatched.", 503);
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            { Content = new ByteArrayContent(countBody) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            // Each uncached measurement makes one attempt. Failures consume capacity too;
            // there is no automatic retry, generation, or request-body retention here.
            _calls++;
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[MaximumResponseBytes + 1]; var used = 0;
            while (used < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                used += read;
            }
            if (used > MaximumResponseBytes) throw Unavailable();
            using var document = JsonDocument.Parse(bytes.AsMemory(0, used));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("object", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "response.input_tokens" ||
                !root.TryGetProperty("input_tokens", out var count) || count.ValueKind != JsonValueKind.Number ||
                !count.TryGetInt32(out var tokens) || tokens < 0) throw Unavailable();
            var measured = new ExperimentTokenCount(tokens, "openai.responses.input_tokens", requestHash,
                ProviderCount: true, CountRequestHash: Convert.ToHexStringLower(SHA256.HashData(countBody)));
            _cache.Add(requestHash, measured);
            lock (_measurementGate) _measurements.Add(measured);
            return measured;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException)
        { throw Unavailable(); }
        finally { _gate.Release(); }
    }

    private static byte[] BuildCountRequest(ReadOnlyMemory<byte> request)
    {
        using var source = JsonDocument.Parse(request);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()) ||
            !root.TryGetProperty("input", out var input) || input.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)) throw InvalidInput();
        if (HasStateReference(root, "conversation") || HasStateReference(root, "previous_response_id")) throw InvalidInput();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var field in CountFields)
                if (root.TryGetProperty(field, out var value))
                { writer.WritePropertyName(field); value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool HasStateReference(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private static ExperimentException InvalidInput() => new("context.token_count_invalid",
        "Native input counting requires an explicit model and complete stateless Responses input. No generation request was dispatched.", 503);
    private static ExperimentException Unavailable() => new("context.token_count_unavailable",
        "OpenAI input-token counting is unavailable or returned an invalid count. No automatic retry or generation request was dispatched.", 503);
}

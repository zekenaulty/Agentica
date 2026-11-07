using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Clients.OpenAI;

namespace Agentica.ProviderLiveProof;

internal sealed class DurableProviderGuard : DelegatingHandler
{
    private readonly ProofJournal _journal;
    private readonly AsyncLocal<CountIntent?> _count = new();
    private static readonly HashSet<string> CountFields = new(StringComparer.Ordinal)
    { "model", "input", "instructions", "reasoning", "text", "tools", "tool_choice", "parallel_tool_calls", "truncation" };

    public DurableProviderGuard(string ledgerPath, HttpMessageHandler inner, ProofLimits? limits = null)
        : base(inner) => _journal = new(ledgerPath, limits);
    public decimal HeldUsd => _journal.Snapshot().HeldUsd;
    public ProviderLedger Snapshot() => _journal.Snapshot();
    public VerifiedInput? FindCount(string generationHash) => _journal.FindCount(generationHash);

    public IDisposable BeginCount(ReadOnlyMemory<byte> completeBody)
    {
        var intent = new CountIntent(completeBody.ToArray());
        try { ValidateGeneration(intent.Body); }
        catch { intent.Dispose(); throw; }
        var previous = _count.Value; _count.Value = intent;
        return new CountScope(() => { _count.Value = previous; intent.Dispose(); });
    }

    public void AcceptMeasurement(string generationHash, int tokens, string countHash) =>
        _journal.LinkMeasurement(generationHash, tokens, countHash);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw Invalid("route");
        if (uri.Scheme != "https" || uri.Host != "api.openai.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Invalid("route");
        if (request.Method == HttpMethod.Get && uri.AbsolutePath is ("/v1/models" or "/v1/models/gpt-6-luna"))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method != HttpMethod.Post || uri.AbsolutePath is not ("/v1/responses" or "/v1/responses/input_tokens"))
            throw Invalid("route");
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter.Length: > 0 }) throw Invalid("credential_missing");
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hash = ProofJournal.BodyHash(body);
            var isCount = uri.AbsolutePath.EndsWith("/input_tokens", StringComparison.Ordinal);
            string generationHash; var maximumOutput = 0; var searchCalls = 0;
            if (isCount)
            {
                var intent = _count.Value ?? throw Invalid("count_without_complete_generation_intent");
                ValidateCountBody(body, intent.Body);
                generationHash = ProofJournal.BodyHash(intent.Body);
            }
            else
            {
                (maximumOutput, searchCalls) = ValidateGeneration(body);
                generationHash = hash;
                var verified = _journal.FindCount(hash) ?? throw Invalid("generation_without_verified_native_count");
                if (verified.Tokens > _journal.Snapshot().Limits.InputTokens) throw Invalid("native_input_limit");
            }
            // Both limits and intent reach durable storage BEFORE the one send.
            // A post-reservation disconnect remains charged/reserved and consumes capacity.
            var id = _journal.Reserve(isCount ? "count" : "inference", hash, generationHash, maximumOutput, searchCalls);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var credential = request.Headers.Authorization.Parameter;
            _journal.Observe(id, attempt =>
            {
                attempt.HttpStatus = (int)response.StatusCode;
                attempt.ProviderRequestId = Safe(response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null, credential);
                attempt.State = "response-headers-observed";
            });
            if (isCount) await ObserveCountAsync(response, id, cancellationToken).ConfigureAwait(false);
            else if (response.IsSuccessStatusCode)
                response.Content = new GuardedInferenceContent(response.Content, _journal, id, credential);
            return response;
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private (int Output, int Search) ValidateGeneration(ReadOnlyMemory<byte> body)
    {
        OpenAiInputTokenCounter.ValidateGenerationBody(body);
        using var parsed = JsonDocument.Parse(body); var root = parsed.RootElement;
        if (Text(root, "model") != "gpt-6-luna" || !root.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("reasoning", out var reasoning) || Text(reasoning, "effort") != "high") throw Invalid("model_control_or_stateless_mismatch");
        var output = Number(root, "max_output_tokens");
        if (output is null or < 1 || output > _journal.Snapshot().Limits.OutputTokens) throw Invalid("output_limit");
        if (root.TryGetProperty("background", out var background) && background.ValueKind != JsonValueKind.False ||
            root.TryGetProperty("service_tier", out var tier) && tier.GetString() is not ("default" or "standard")) throw Invalid("unpriced_control");
        // This proof uses only host-bound scratch/calculation tools. Native web
        // search would add provider-side context beyond the measured input.
        if (root.TryGetProperty("tools", out _)) throw Invalid("native_tools_disabled");
        return (output.Value, 0);
    }

    private static void ValidateCountBody(ReadOnlyMemory<byte> count, ReadOnlyMemory<byte> generation)
    {
        using var counted = JsonDocument.Parse(count); using var complete = JsonDocument.Parse(generation);
        if (counted.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("count_body");
        foreach (var property in counted.RootElement.EnumerateObject())
            if (!CountFields.Contains(property.Name) || !complete.RootElement.TryGetProperty(property.Name, out var expected) ||
                !JsonElement.DeepEquals(property.Value, expected)) throw Invalid("count_generation_mismatch");
        foreach (var property in complete.RootElement.EnumerateObject())
            if (CountFields.Contains(property.Name) && !counted.RootElement.TryGetProperty(property.Name, out _))
                throw Invalid("incomplete_count_body");
    }

    private async Task ObserveCountAsync(HttpResponseMessage response, string id, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) return;
        var original = response.Content;
        await using var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > 65536) throw Invalid("count_response_limit");
            buffer.Write(block, 0, read);
        }
        var bytes = buffer.ToArray();
        response.Content = new ByteArrayContent(bytes);
        foreach (var header in original.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        original.Dispose();
        try
        {
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (Text(root, "object") == "response.input_tokens" && Number(root, "input_tokens") is >= 0 and var tokens)
                _journal.Observe(id, attempt => { attempt.MeasuredInputTokens = tokens; attempt.State = "count-response-valid"; attempt.SettledUsd = 0; });
        }
        catch (JsonException) { /* Shared counter rejects malformed content. Capacity remains used. */ }
    }

    protected override void Dispose(bool disposing)
    { if (disposing) _journal.Dispose(); base.Dispose(disposing); }
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    internal static int? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
        field.TryGetInt32(out var number) && number >= 0 ? number : null;
    internal static string? Safe(string? value, string? credential) => value is { Length: > 0 and <= 512 } &&
        !value.Any(char.IsControl) && (credential is null || !value.Contains(credential, StringComparison.Ordinal)) ? value : null;
    private static InvalidOperationException Invalid(string code) => new("proof." + code);
    private sealed class CountIntent(byte[] body) : IDisposable
    { public byte[] Body { get; } = body; public void Dispose() => CryptographicOperations.ZeroMemory(Body); }
    private sealed class CountScope(Action close) : IDisposable
    { public void Dispose() => close(); }
}

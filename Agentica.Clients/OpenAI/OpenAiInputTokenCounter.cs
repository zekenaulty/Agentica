using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

public sealed record LlmInputTokenMeasurement(int Tokens, string Method,
    string GenerationRequestIdentity, string GenerationBodyHash,
    LlmPreparedRequestDescriptor CountRequest, LlmAttemptEvidence AttemptEvidence);

/// <summary>Separate stateless native count work, with required host admission and no retries.
/// Count results measure inference input; they are not billed usage for the count operation.</summary>
public sealed class OpenAiInputTokenCounter
{
    private const int MaximumResponseBytes = 65_536;
    private static readonly string[] CountFields =
        ["model", "input", "instructions", "reasoning", "text", "tools", "tool_choice", "parallel_tool_calls", "truncation"];
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly Uri _generationEndpoint;
    private readonly int _maximumRequests;
    private int _requests;

    public OpenAiInputTokenCounter(HttpClient borrowed, OpenAiResponsesClientOptions options,
        int maximumRequests, Uri? inputCountEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(borrowed);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRequests, 1);
        var key = options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ArgumentException("A provider credential is required for native input counting.", nameof(options));
        _http = borrowed;
        _apiKey = key;
        _maximumRequests = maximumRequests;
        _generationEndpoint = options.Endpoint ?? OpenAiResponsesClientOptions.DefaultEndpoint;
        _endpoint = inputCountEndpoint ?? new Uri(_generationEndpoint.AbsoluteUri.TrimEnd('/') + "/input_tokens");
    }

    public int Requests => Volatile.Read(ref _requests);

    public async Task<LlmInputTokenMeasurement> CountAsync(LlmPreparedCall prepared,
        LlmAttemptCapture countCapture, ILlmCallAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(countCapture);
        ArgumentNullException.ThrowIfNull(admission);
        var countReferences = countCapture.References;
        if (prepared.Descriptor.ProviderName != OpenAiResponsesLlmClient.ProviderName ||
            prepared.Descriptor.ApiMode != "responses" || prepared.Endpoint != _generationEndpoint ||
            countReferences.AttemptId == prepared.Descriptor.References.AttemptId)
            throw Invalid("input_count_binding_mismatch");
        var generation = prepared.CopySensitiveRequestBody();
        byte[] countBody;
        try { countBody = BuildCountRequest(generation); }
        finally { CryptographicOperations.ZeroMemory(generation); }
        var configuration = new LlmConfigurationReport(
            prepared.Descriptor.ProviderName, "responses_input_tokens", prepared.Descriptor.ModelId,
            prepared.Descriptor.ModelId, prepared.Descriptor.ConfigurationIdentity,
            "native_input_count", [], [], [],
            new(LlmCapabilitySupport.Supported, "installed-count-serializer", null, "Separate native operation."),
            new(LlmCapabilitySupport.Supported, "captured-endpoint", null, "Exact configured endpoint."),
            new(LlmCapabilitySupport.Unknown, "not-observed", null, "No live readiness claim."),
            new(LlmCapabilitySupport.Unknown, "not-observed", null, "No account/model claim."),
            countReferences.RouteFingerprint, countReferences.ExecutionBundleReference);
        var transportBinding = new LlmPreparedTransportBinding(_http);
        var descriptor = LlmPreparedIdentity.Describe(countReferences, prepared.Descriptor.HostScopeId,
            LlmProviderWorkKind.InputCount, configuration, _endpoint, countBody, "|",
            prepared.Descriptor.RequestIdentity, transportBinding.Identity);
        var request = new LlmRequest(prepared.Descriptor.ModelId, [])
        {
            AttemptCapture = countCapture,
            PreparedDispatch = new(descriptor, _endpoint, countBody, "|", admission, transportBinding)
        };
        LlmAttemptRecorder recorder;
        try { recorder = LlmAttemptRecorder.Start(request, OpenAiResponsesLlmClient.ProviderName, request.ModelId); }
        catch { CryptographicOperations.ZeroMemory(countBody); throw; }
        var capacityReserved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Capacity is local defense in depth. The required host admission is the
            // durable cumulative budget; a new counter does not reset that budget.
            if (Interlocked.Increment(ref _requests) > _maximumRequests)
            {
                Interlocked.Decrement(ref _requests);
                throw Invalid("input_count_limit");
            }
            capacityReserved = true;
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            { Content = new ByteArrayContent(countBody) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await LlmProviderTransport.SendAsync(_http, message, request, recorder,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw Invalid("input_count_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[MaximumResponseBytes + 1];
            var used = 0;
            while (used < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                used += read;
            }
            if (used > MaximumResponseBytes) throw Invalid("input_count_unavailable");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, used));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("object", out var kind) || kind.GetString() != "response.input_tokens" ||
                !root.TryGetProperty("input_tokens", out var count) || !count.TryGetInt32(out var tokens) || tokens < 0)
                throw Invalid("input_count_unavailable");
            var evidence = recorder.Finish(LlmAttemptOutcome.Succeeded);
            return new(tokens, "openai.responses.input_tokens", prepared.Descriptor.RequestIdentity,
                prepared.Descriptor.BodyHash, descriptor, evidence);
        }
        catch (OperationCanceledException)
        {
            recorder.Finish(LlmAttemptOutcome.Cancelled, "input_count_cancelled");
            throw;
        }
        catch (Exception error) when (ClientExceptionBoundary.IsRecoverable(error))
        {
            var code = error is LlmClientException client ? client.ErrorClass : "input_count_unavailable";
            var evidence = recorder.Finish(recorder.Evidence.DispatchCertainty == LlmDispatchCertainty.NotDispatched
                ? LlmAttemptOutcome.Rejected : LlmAttemptOutcome.Failed, code);
            if (error is LlmClientException existing)
            {
                existing.RetainAttemptEvidence(evidence);
                throw;
            }
            throw new LlmClientException(OpenAiResponsesLlmClient.ProviderName,
                "Native input counting is unavailable; no fallback or generation was performed.",
                error, LlmClientErrorKind.Transient, errorClass: "input_count_unavailable")
            { AttemptEvidence = evidence };
        }
        finally
        {
            if (capacityReserved && recorder.Evidence.DispatchCertainty == LlmDispatchCertainty.NotDispatched)
                Interlocked.Decrement(ref _requests);
            CryptographicOperations.ZeroMemory(countBody);
        }
    }

    internal static byte[] BuildCountRequest(ReadOnlyMemory<byte> generation)
    {
        using var source = JsonDocument.Parse(generation);
        var root = source.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(model.GetString()) || !root.TryGetProperty("input", out var input) ||
            input.ValueKind is not (JsonValueKind.String or JsonValueKind.Array) ||
            HasStateReference(root, "conversation") || HasStateReference(root, "previous_response_id"))
            throw Invalid("input_count_invalid_request");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var field in CountFields)
                if (root.TryGetProperty(field, out var value))
                { writer.WritePropertyName(field); value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static bool HasStateReference(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private static LlmClientException Invalid(string code) => new(OpenAiResponsesLlmClient.ProviderName,
        "Native input count request rejected or unavailable.", errorKind: LlmClientErrorKind.BadRequest, errorClass: code);
}

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
        ValidateEndpoint(_generationEndpoint);
        _endpoint = inputCountEndpoint ?? new UriBuilder(_generationEndpoint)
        { Path = _generationEndpoint.AbsolutePath.TrimEnd('/') + "/input_tokens" }.Uri;
        ValidateEndpoint(_endpoint);
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
        return await CountCoreAsync(prepared.Descriptor, countBody, countCapture, admission, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts an exact stateless Responses body owned by the host. The full
    /// generation byte hash/identity remains distinct from the filtered count request.</summary>
    public async Task<LlmInputTokenMeasurement> CountAsync(ReadOnlyMemory<byte> completeGenerationBody,
        LlmAttemptReferences generationReferences, string hostScopeId,
        LlmAttemptCapture countCapture, ILlmCallAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generationReferences);
        ArgumentNullException.ThrowIfNull(countCapture);
        ArgumentNullException.ThrowIfNull(admission);
        if (countCapture.References.AttemptId == generationReferences.AttemptId)
            throw Invalid("input_count_binding_mismatch");
        if (completeGenerationBody.Length is < 1 or > LlmPreparedIdentity.MaximumBodyBytes)
            throw Invalid("input_count_invalid_request");
        var owned = completeGenerationBody.ToArray();
        byte[]? countBody = null;
        try
        {
            countBody = BuildCountRequest(owned);
            using var body = JsonDocument.Parse(owned);
            var model = body.RootElement.GetProperty("model").GetString()!;
            var applied = body.RootElement.EnumerateObject().Where(property =>
                property.Name is not ("input" or "instructions" or "metadata"))
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            var configurationIdentity = LlmPreparedIdentity.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                contract = "agentica.host.responses.configuration.v1",
                endpoint = _generationEndpoint.AbsoluteUri,
                applied
            }));
            var configuration = Configuration(model, configurationIdentity, generationReferences, "responses");
            var transport = new LlmPreparedTransportBinding(_http);
            var generation = LlmPreparedIdentity.Describe(generationReferences, hostScopeId,
                LlmProviderWorkKind.Inference, configuration, _generationEndpoint, owned, "text/event-stream|",
                transportIdentity: transport.Identity);
            var result = await CountCoreAsync(generation, countBody, countCapture, admission,
                cancellationToken).ConfigureAwait(false);
            countBody = null; // Core owns and clears the count buffer, including failure paths.
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(owned);
            if (countBody is not null) CryptographicOperations.ZeroMemory(countBody);
        }
    }

    /// <summary>Validates before a host's cache lookup; performs no network work or admission.</summary>
    public static void ValidateGenerationBody(ReadOnlyMemory<byte> completeGenerationBody)
    {
        var counted = BuildCountRequest(completeGenerationBody);
        CryptographicOperations.ZeroMemory(counted);
    }

    private async Task<LlmInputTokenMeasurement> CountCoreAsync(LlmPreparedRequestDescriptor generation,
        byte[] countBody, LlmAttemptCapture countCapture, ILlmCallAdmission admission,
        CancellationToken cancellationToken)
    {
        var countReferences = countCapture.References;
        var configuration = Configuration(generation.ModelId, generation.ConfigurationIdentity,
            countReferences, "responses_input_tokens");
        var transportBinding = new LlmPreparedTransportBinding(_http);
        var descriptor = LlmPreparedIdentity.Describe(countReferences, generation.HostScopeId,
            LlmProviderWorkKind.InputCount, configuration, _endpoint, countBody, "|",
            generation.RequestIdentity, transportBinding.Identity);
        var request = new LlmRequest(generation.ModelId, [])
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
            return new(tokens, "openai.responses.input_tokens", generation.RequestIdentity,
                generation.BodyHash, descriptor, evidence);
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
        if (generation.Length is < 1 or > LlmPreparedIdentity.MaximumBodyBytes)
            throw Invalid("input_count_invalid_request");
        try
        {
            using var source = JsonDocument.Parse(generation);
            var root = source.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String ||
                model.GetString() is not { Length: > 0 and <= 512 } modelId ||
                string.IsNullOrWhiteSpace(modelId) || modelId.Any(char.IsControl) || modelId.Trim() != modelId ||
                !root.TryGetProperty("input", out var input) ||
                input.ValueKind is not (JsonValueKind.String or JsonValueKind.Array) ||
                HasStateReference(root, "conversation") || HasStateReference(root, "previous_response_id") ||
                HasStateReference(root, "response_id") ||
                input.ValueKind == JsonValueKind.Array && input.EnumerateArray().Any(IsStoredItemReference) ||
                root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
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
        catch (JsonException) { throw Invalid("input_count_invalid_request"); }
    }

    private static LlmConfigurationReport Configuration(string model, string identity,
        LlmAttemptReferences references, string apiMode) => new(
            OpenAiResponsesLlmClient.ProviderName, apiMode, model, model, identity,
            "native_input_count", [], [], [],
            new(LlmCapabilitySupport.Supported, "installed-count-serializer", null, "Separate native operation."),
            new(LlmCapabilitySupport.Supported, "captured-endpoint", null, "Exact configured endpoint."),
            new(LlmCapabilitySupport.Unknown, "not-observed", null, "No live readiness claim."),
            new(LlmCapabilitySupport.Unknown, "not-observed", null, "No account/model claim."),
            references.RouteFingerprint, references.ExecutionBundleReference);

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Native counting requires an absolute HTTP endpoint without userinfo or fragment.", nameof(endpoint));
    }

    private static bool IsStoredItemReference(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var hasType = item.TryGetProperty("type", out var type);
        if (hasType && type.ValueKind == JsonValueKind.String && type.GetString() == "item_reference") return true;
        // ItemReference's discriminator is optional in the wire contract. Do not
        // mistake complete native output items with IDs/content for ID-only refs.
        return (!hasType || type.ValueKind == JsonValueKind.Null) && item.TryGetProperty("id", out _) &&
            item.EnumerateObject().All(property => property.Name is "id" or "type");
    }

    private static bool HasStateReference(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private static LlmClientException Invalid(string code) => new(OpenAiResponsesLlmClient.ProviderName,
        "Native input count request rejected or unavailable.", errorKind: LlmClientErrorKind.BadRequest, errorClass: code);
}

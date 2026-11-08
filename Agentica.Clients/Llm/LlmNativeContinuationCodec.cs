using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.Clients.Llm;

/// <summary>Explicit sensitive exchange for the existing adapter-native carriers. The unkeyed
/// checksum detects accidental corruption, not authorship or authenticity. Protect stored bytes
/// in a host-owned authenticated envelope; hosts own authorization, scope, expiry and deletion.</summary>
public static class LlmNativeContinuationCodec
{
    public const int CurrentVersion = 2;
    public const int MaximumSerializedBytes = 16_777_216;
    public const int MaximumInstructionCharacters = 1_048_576;
    public const int MaximumDepth = 64;
    public const int MaximumCollectionItems = 8192;
    public const int MaximumTokens = 131_072;
    private const string Format = "agentica.native-continuation";

    /// <summary>Unbound legacy/in-memory carriers retain their existing compatibility guards.
    /// ConfigurationBound=false explicitly means the original configuration is not known.</summary>
    public static LlmNativeContinuationCheck ValidateBinding(LlmNativeContinuation? continuation,
        LlmNativeContinuationBinding expectedBinding)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (continuation is null) return new(false, LlmNativeContinuationRejection.Unavailable, false);
        try
        {
            var snapshot = continuation.SnapshotOwned();
            if (continuation.MessageMode != expectedBinding.MessageMode)
                return new(false, LlmNativeContinuationRejection.MessageModeMismatch, snapshot.Binding is not null);
            var rejection = BindingRejection(continuation.ProviderName, continuation.ModelId,
                snapshot.Instruction, snapshot.Binding, expectedBinding);
            if (rejection == LlmNativeContinuationRejection.None)
                rejection = ValidateHistory(continuation.ProviderName, snapshot.History, continuation.MessageMode);
            return new(rejection == LlmNativeContinuationRejection.None, rejection, snapshot.Binding is not null);
        }
        catch (ObjectDisposedException) { return new(false, LlmNativeContinuationRejection.Disposed, false); }
    }

    public static LlmNativeContinuationExportResult ExportSensitive(LlmNativeContinuation? continuation,
        LlmNativeContinuationBinding sourceBinding)
    {
        var check = ValidateBinding(continuation, sourceBinding);
        if (!check.IsCompatible) return new(check.Rejection);
        try
        {
            var binding = new LlmContinuationConfigurationBinding(sourceBinding.ApiMode, sourceBinding.ConfigurationIdentity);
            if (!continuation!.TryBindConfiguration(binding))
                return new(LlmNativeContinuationRejection.ConfigurationMismatch);
            var snapshot = continuation.SnapshotOwned();
            if (snapshot.Instruction.Length > MaximumInstructionCharacters)
                return new(LlmNativeContinuationRejection.PayloadTooLarge);
            using var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteNumber("version", continuation.MessageMode == LlmMessageMode.Ordered ? CurrentVersion : 1);
                if (continuation.MessageMode == LlmMessageMode.Ordered) writer.WriteString("message_mode", "ordered");
                writer.WriteString("provider", continuation.ProviderName);
                writer.WriteString("api_mode", sourceBinding.ApiMode);
                writer.WriteString("model", continuation.ModelId);
                writer.WriteString("configuration", sourceBinding.ConfigurationIdentity);
                writer.WriteString("instructions", snapshot.Instruction);
                writer.WritePropertyName("history");
                writer.WriteRawValue(snapshot.History, skipInputValidation: false);
                writer.WriteEndObject();
            }
            var bodyBytes = body.ToArray();
            try
            {
                using var envelope = new MemoryStream();
                using (var writer = new Utf8JsonWriter(envelope))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("payload");
                    writer.WriteRawValue(bodyBytes, skipInputValidation: false);
                    writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(bodyBytes)));
                    writer.WriteEndObject();
                }
                var bytes = envelope.ToArray();
                Clear(envelope);
                if (bytes.Length > MaximumSerializedBytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return new(LlmNativeContinuationRejection.PayloadTooLarge);
                }
                var bounded = ValidateJson(bytes);
                if (bounded != LlmNativeContinuationRejection.None)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return new(bounded);
                }
                return new(LlmNativeContinuationRejection.None, new(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bodyBytes); Clear(body); }
        }
        catch (ObjectDisposedException) { return new(LlmNativeContinuationRejection.Disposed); }
        catch (JsonException) { return new(LlmNativeContinuationRejection.MalformedPayload); }
    }

    public static LlmNativeContinuationImportResult ImportSensitive(LlmSensitiveContinuationPayload? payload,
        LlmNativeContinuationBinding expectedBinding)
    {
        if (payload is null) return new(LlmNativeContinuationRejection.Unavailable);
        byte[]? copy = null;
        try
        {
            copy = payload.CopySensitiveBytes();
            return ImportSensitive(copy.AsSpan(), expectedBinding);
        }
        catch (ObjectDisposedException) { return new(LlmNativeContinuationRejection.Disposed); }
        finally { if (copy is not null) CryptographicOperations.ZeroMemory(copy); }
    }

    public static LlmNativeContinuationImportResult ImportSensitive(ReadOnlySpan<byte> sensitiveBytes,
        LlmNativeContinuationBinding expectedBinding)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (sensitiveBytes.IsEmpty) return new(LlmNativeContinuationRejection.Unavailable);
        if (sensitiveBytes.Length > MaximumSerializedBytes) return new(LlmNativeContinuationRejection.PayloadTooLarge);
        var bounded = ValidateJson(sensitiveBytes);
        if (bounded != LlmNativeContinuationRejection.None) return new(bounded);
        var owned = sensitiveBytes.ToArray();
        try
        {
            _ = expectedBinding.SystemInstruction; // Reject a disposed expected binding before materializing state.
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = MaximumDepth + 1 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("payload", out var body) || body.ValueKind != JsonValueKind.Object ||
                String(root, "sha256") is not { Length: 64 } checksum)
                return new(LlmNativeContinuationRejection.MalformedPayload);
            var bodyBytes = Encoding.UTF8.GetBytes(body.GetRawText());
            try
            {
                byte[] expectedHash;
                try { expectedHash = Convert.FromHexString(checksum); }
                catch (FormatException) { return new(LlmNativeContinuationRejection.IntegrityMismatch); }
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bodyBytes), expectedHash))
                    return new(LlmNativeContinuationRejection.IntegrityMismatch);
            }
            finally { CryptographicOperations.ZeroMemory(bodyBytes); }
            if (String(body, "format") != Format || !body.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)) return new(LlmNativeContinuationRejection.MalformedPayload);
            if (number is not (1 or CurrentVersion)) return new(LlmNativeContinuationRejection.UnsupportedVersion);
            var messageMode = number == 1 ? LlmMessageMode.Legacy : LlmMessageMode.Ordered;
            if (number == CurrentVersion && String(body, "message_mode") != "ordered")
                return new(LlmNativeContinuationRejection.MalformedPayload);
            if (messageMode != expectedBinding.MessageMode) return new(LlmNativeContinuationRejection.MessageModeMismatch);
            if (body.EnumerateObject().Count() != (number == 1 ? 8 : 9) ||
                String(body, "provider") is not { } provider || String(body, "api_mode") is not { } apiMode ||
                String(body, "model") is not { } model || String(body, "configuration") is not { } configuration ||
                String(body, "instructions") is not { } instructions || !body.TryGetProperty("history", out var history))
                return new(LlmNativeContinuationRejection.MalformedPayload);
            if (instructions.Length > MaximumInstructionCharacters || history.GetRawText().Length > LlmNativeContinuation.MaxPayloadCharacters)
                return new(LlmNativeContinuationRejection.PayloadTooLarge);
            var storedBinding = new LlmContinuationConfigurationBinding(apiMode, configuration);
            var mismatch = BindingRejection(provider, model, instructions, storedBinding, expectedBinding);
            if (mismatch != LlmNativeContinuationRejection.None) return new(mismatch);
            if (!ValidNativeShape(provider, history, messageMode)) return new(LlmNativeContinuationRejection.InvalidNativeShape);
            return new(LlmNativeContinuationRejection.None,
                new(provider, model, instructions, history.GetRawText(), storedBinding, messageMode));
        }
        catch (ObjectDisposedException) { return new(LlmNativeContinuationRejection.Disposed); }
        catch (JsonException) { return new(LlmNativeContinuationRejection.MalformedPayload); }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }

    private static LlmNativeContinuationRejection BindingRejection(string provider, string model,
        string instructions, LlmContinuationConfigurationBinding? stored, LlmNativeContinuationBinding expected)
    {
        if (provider != expected.ProviderName) return LlmNativeContinuationRejection.ProviderMismatch;
        var apiMode = provider switch
        {
            "openai" or "xai" => "responses",
            "anthropic" => "messages",
            "ollama" => "chat",
            "Gemini" => "interactions",
            "Gemini.GenerateContent" => "generate_content",
            _ => null
        };
        if (apiMode is null) return LlmNativeContinuationRejection.UnsupportedProvider;
        if (expected.ApiMode != apiMode) return LlmNativeContinuationRejection.UnsupportedApiMode;
        if (stored is not null && stored.ApiMode != expected.ApiMode) return LlmNativeContinuationRejection.ApiModeMismatch;
        if (model != expected.ModelId) return LlmNativeContinuationRejection.ModelMismatch;
        if (expected.MessageMode == LlmMessageMode.Legacy && instructions != expected.SystemInstruction)
            return LlmNativeContinuationRejection.InstructionMismatch;
        if (stored is not null && stored.ConfigurationIdentity != expected.ConfigurationIdentity)
            return LlmNativeContinuationRejection.ConfigurationMismatch;
        return LlmNativeContinuationRejection.None;
    }

    private static LlmNativeContinuationRejection ValidateHistory(string provider, string history, LlmMessageMode messageMode)
    {
        if (history.Length > LlmNativeContinuation.MaxPayloadCharacters) return LlmNativeContinuationRejection.PayloadTooLarge;
        var bytes = Encoding.UTF8.GetBytes(history);
        try
        {
            var bounded = ValidateJson(bytes);
            if (bounded != LlmNativeContinuationRejection.None) return bounded;
            using var document = JsonDocument.Parse(bytes);
            return ValidNativeShape(provider, document.RootElement, messageMode)
                ? LlmNativeContinuationRejection.None : LlmNativeContinuationRejection.InvalidNativeShape;
        }
        catch (JsonException) { return LlmNativeContinuationRejection.MalformedPayload; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool ValidNativeShape(string provider, JsonElement history, LlmMessageMode messageMode)
    {
        if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() == 0) return false;
        var outputObserved = false;
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var allCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in history.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            var role = String(item, "role");
            var type = String(item, "type");
            switch (provider)
            {
                case "openai":
                case "xai":
                    if (messageMode == LlmMessageMode.Ordered && type == "function_call")
                    {
                        var call = new LlmFunctionCall(String(item, "call_id")!, String(item, "name")!, String(item, "arguments")!);
                        if (!LlmMessageMapping.ValidCall(call) || !allCalls.Add(call.CallId)) return false;
                        calls.Add(call.CallId);
                        outputObserved = true;
                        break;
                    }
                    if (messageMode == LlmMessageMode.Ordered && type == "function_call_output")
                    {
                        if (String(item, "call_id") is not { } id || !calls.Remove(id) || String(item, "output") is null) return false;
                        break;
                    }
                    if (messageMode == LlmMessageMode.Ordered && ValidOrderedInput(item)) break;
                    if (role == "user" && type is null or "message" && String(item, "content") is not null) break;
                    if (type == "reasoning" && !string.IsNullOrWhiteSpace(String(item, "encrypted_content"))) { outputObserved = true; break; }
                    if (provider == "openai" && type == "web_search_call")
                    {
                        if (!ValidWebSearchCall(item)) return false;
                        break;
                    }
                    if (type != "message" || role != "assistant" || !Array(item, "content", out var responseParts) ||
                        responseParts.EnumerateArray().Any(part => String(part, "type") != "output_text" || String(part, "text") is null)) return false;
                    outputObserved = true;
                    break;
                case "anthropic":
                    if (messageMode == LlmMessageMode.Ordered && role == "assistant" && String(item, "content") is not null) break;
                    if (role == "user" && String(item, "content") is not null) break;
                    if (role != "assistant" || !Array(item, "content", out var blocks)) return false;
                    foreach (var block in blocks.EnumerateArray())
                        if (String(block, "type") switch
                        {
                            "text" => String(block, "text") is null,
                            "thinking" => string.IsNullOrWhiteSpace(String(block, "signature")),
                            "redacted_thinking" => string.IsNullOrWhiteSpace(String(block, "data")),
                            _ => true
                        }) return false;
                    outputObserved = true;
                    break;
                case "ollama":
                    if (role is not ("system" or "user" or "assistant") || String(item, "content") is null ||
                        item.TryGetProperty("thinking", out var thought) && thought.ValueKind != JsonValueKind.String) return false;
                    outputObserved |= role == "assistant";
                    break;
                case "Gemini":
                    if (type == "thought")
                    {
                        if (item.TryGetProperty("signature", out var signature) &&
                            (signature.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(signature.GetString()))) return false;
                        outputObserved = true;
                        break;
                    }
                    if (type is not ("user_input" or "model_output") || !Array(item, "content", out var content) ||
                        content.EnumerateArray().Any(part => String(part, "type") != "text" || String(part, "text") is null)) return false;
                    outputObserved |= type == "model_output";
                    break;
                case "Gemini.GenerateContent":
                    if (role is not ("user" or "model") || !Array(item, "parts", out var parts) ||
                        parts.EnumerateArray().Any(part => !ValidGeminiPart(part))) return false;
                    outputObserved |= role == "model";
                    break;
                default: return false;
            }
        }
        return outputObserved;
    }

    private static bool ValidOrderedInput(JsonElement item)
    {
        if (String(item, "type") is not (null or "message") ||
            String(item, "role") is not ("system" or "developer" or "user" or "assistant")) return false;
        if (item.TryGetProperty("phase", out var phase) &&
            (String(item, "role") != "assistant" || phase.ValueKind != JsonValueKind.String ||
             phase.GetString() is not ("commentary" or "final_answer"))) return false;
        if (String(item, "content") is not null) return true;
        if (String(item, "role") == "assistant") return false;
        if (!Array(item, "content", out var blocks) || blocks.GetArrayLength() != 1) return false;
        var block = blocks[0];
        if (String(block, "type") != "input_text" || String(block, "text") is null) return false;
        if (!block.TryGetProperty("prompt_cache_breakpoint", out var cache)) return true;
        return cache.ValueKind == JsonValueKind.Object && String(cache, "mode") == "explicit" &&
            (!cache.TryGetProperty("ttl", out _) || String(cache, "ttl") is "30m" or "12h");
    }

    private static bool ValidWebSearchCall(JsonElement item)
    {
        var id = String(item, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl) ||
            String(item, "status") is not ("in_progress" or "searching" or "completed" or "failed") ||
            !item.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) return false;
        var type = String(action, "type");
        if (type is not ("search" or "open_page" or "find_in_page")) return false;
        if (type is "open_page" or "find_in_page" && String(action, "url") is null) return false;
        if (type == "find_in_page" && String(action, "pattern") is null) return false;
        if (action.TryGetProperty("query", out var query) && query.ValueKind != JsonValueKind.String) return false;
        if (action.TryGetProperty("queries", out var queries) &&
            (queries.ValueKind != JsonValueKind.Array || queries.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))) return false;
        if (action.TryGetProperty("sources", out var sources) &&
            (sources.ValueKind != JsonValueKind.Array || sources.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object))) return false;
        return true;
    }

    private static bool ValidGeminiPart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object ||
            !part.TryGetProperty("text", out _) && !part.TryGetProperty("thoughtSignature", out _)) return false;
        return part.EnumerateObject().All(property => property.Name switch
        {
            "text" or "thoughtSignature" => property.Value.ValueKind == JsonValueKind.String,
            "thought" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        });
    }

    private static LlmNativeContinuationRejection ValidateJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumSerializedBytes) return LlmNativeContinuationRejection.PayloadTooLarge;
        var stack = new Stack<(bool IsObject, int Count, HashSet<string>? Names)>();
        var tokens = 0;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumDepth + 1 });
            while (reader.Read())
            {
                if (++tokens > MaximumTokens) return LlmNativeContinuationRejection.CollectionLimitExceeded;
                if (reader.CurrentDepth > MaximumDepth) return LlmNativeContinuationRejection.DepthLimitExceeded;
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var current = stack.Pop();
                    if (++current.Count > MaximumCollectionItems) return LlmNativeContinuationRejection.CollectionLimitExceeded;
                    if (!current.Names!.Add(reader.GetString()!)) return LlmNativeContinuationRejection.MalformedPayload;
                    stack.Push(current);
                    continue;
                }
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { stack.Pop(); continue; }
                if (stack.Count > 0 && !stack.Peek().IsObject)
                {
                    var current = stack.Pop();
                    if (++current.Count > MaximumCollectionItems) return LlmNativeContinuationRejection.CollectionLimitExceeded;
                    stack.Push(current);
                }
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    stack.Push((reader.TokenType == JsonTokenType.StartObject, 0,
                        reader.TokenType == JsonTokenType.StartObject ? new(StringComparer.Ordinal) : null));
            }
            return tokens == 0 ? LlmNativeContinuationRejection.MalformedPayload : LlmNativeContinuationRejection.None;
        }
        catch (JsonException)
        {
            return stack.Count > MaximumDepth ? LlmNativeContinuationRejection.DepthLimitExceeded
                : LlmNativeContinuationRejection.MalformedPayload;
        }
    }

    private static bool Array(JsonElement item, string name, out JsonElement value) =>
        item.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static string? String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Clear(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
    }
}

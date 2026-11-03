using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

internal static class ResponsesStructuredOutput
{
    internal const string SchemaName = "agentica_response";

    public static object CreateFormat(LlmStructuredOutputOptions options, string providerName)
    {
        if (options.ResponseMimeType != "application/json")
            throw Failure(providerName, "unsupported_response_format");
        if (!Enum.IsDefined(options.SchemaEnforcement))
            throw Failure(providerName, "unsupported_schema_enforcement");
        if (string.IsNullOrWhiteSpace(options.JsonSchema))
            return new { type = "json_object" };

        JsonElement schema;
        try
        {
            using var document = JsonDocument.Parse(options.JsonSchema);
            schema = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new LlmClientException(providerName, "Responses schema must be valid JSON.",
                exception, LlmClientErrorKind.BadRequest, errorClass: "invalid_json_schema");
        }
        if (schema.ValueKind != JsonValueKind.Object)
            throw Failure(providerName, "invalid_json_schema");

        var strict = options.SchemaEnforcement == LlmJsonSchemaEnforcement.Strict;
        // This checks known OpenAI shape requirements, not the whole JSON Schema language.
        // xAI accepts the same wire format, but documents strict as a compatibility field.
        if (strict && providerName == OpenAiResponsesLlmClient.ProviderName)
        {
            if (!schema.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "object" ||
                schema.TryGetProperty("anyOf", out _))
                throw Failure(providerName, "unsupported_json_schema");
            ValidateStrictShape(schema, providerName);
        }
        return new { type = "json_schema", name = SchemaName, schema, strict };
    }

    private static void ValidateStrictShape(JsonElement schema, string providerName)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw Failure(providerName, "unsupported_json_schema");
        foreach (var unsupported in new[] { "allOf", "not", "dependentRequired", "dependentSchemas", "if", "then", "else" })
            if (schema.TryGetProperty(unsupported, out _))
                throw Failure(providerName, "unsupported_json_schema");

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var isObject = hasProperties || schema.TryGetProperty("type", out var type) &&
            (type.ValueKind == JsonValueKind.String && type.GetString() == "object" ||
             type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item =>
                 item.ValueKind == JsonValueKind.String && item.GetString() == "object"));
        if (isObject)
        {
            if (!schema.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False ||
                hasProperties && properties.ValueKind != JsonValueKind.Object)
                throw Failure(providerName, "unsupported_json_schema");
            var names = hasProperties
                ? properties.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array || required.EnumerateArray()
                    .Any(item => item.ValueKind != JsonValueKind.String))
                    throw Failure(providerName, "unsupported_json_schema");
                var requiredNames = required.EnumerateArray().Select(item => item.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
                if (requiredNames.Count != required.GetArrayLength() || !names.SetEquals(requiredNames))
                    throw Failure(providerName, "unsupported_json_schema");
            }
            else if (names.Count > 0)
                throw Failure(providerName, "unsupported_json_schema");
        }
        foreach (var keyword in new[] { "properties", "$defs" })
        {
            if (!schema.TryGetProperty(keyword, out var children)) continue;
            if (children.ValueKind != JsonValueKind.Object)
                throw Failure(providerName, "unsupported_json_schema");
            foreach (var child in children.EnumerateObject())
                ValidateStrictShape(child.Value, providerName);
        }
        if (schema.TryGetProperty("items", out var items)) ValidateStrictShape(items, providerName);
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            if (alternatives.ValueKind != JsonValueKind.Array || alternatives.GetArrayLength() == 0)
                throw Failure(providerName, "unsupported_json_schema");
            foreach (var alternative in alternatives.EnumerateArray())
                ValidateStrictShape(alternative, providerName);
        }
    }

    private static LlmClientException Failure(string providerName, string errorClass) =>
        new(providerName, "Responses structured-output configuration is invalid or unsupported.",
            errorKind: LlmClientErrorKind.BadRequest, errorClass: errorClass);
}

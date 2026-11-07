using Agentica.Clients.Llm;
using System.Text.Json;

namespace Agentica.ProviderConsumer;

// Synthetic source projections reviewed against Bridge 187b203c45d316e8f166dcb1cbd239e5a564a321.
// These pin transport shapes, not Bridge's complete dynamic schemas or semantic validators.
internal static class BridgeContractFixtures
{
    public const string SourceCut = "187b203c45d316e8f166dcb1cbd239e5a564a321";
    public const string Foreground = """
        {"type":"object","additionalProperties":false,"required":["decision","candidate","action"],"properties":{
          "decision":{"type":"string","enum":["reply","action"]},
          "candidate":{"anyOf":[{"type":"object","additionalProperties":false,"required":["reply"],"properties":{"reply":{"type":"string"}}},{"type":"null"}]},
          "action":{"anyOf":[{"type":"object","additionalProperties":false,"required":["capability_ref","input"],"properties":{
            "capability_ref":{"type":"string","enum":["accept-item"]},"input":{"type":"object","additionalProperties":false,"required":["id","scope","version"],"properties":{"id":{"type":"string"},"scope":{"type":"string"},"version":{"type":"integer"}}}}},{"type":"null"}]}}}
        """;
    // MemoryOutputContract.Create's root and action field names, with synthetic enum values.
    // Its host-only source/target/authority rules remain host checks, never inferred from JSON.
    public static readonly string Memory = JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false, required = new[] { "actions", "abstention_reason", "summary" },
        properties = new Dictionary<string, object>
        {
            ["actions"] = new { type = "array", items = new { type = "object", additionalProperties = false,
                required = new[] { "action", "target_claim_id", "source_message_ids", "claim_type", "proposition", "scope", "sensitivity", "engram_kind", "engram_label", "cue_terms", "compact_rendering", "expanded_rendering", "confidence", "rationale" },
                properties = new Dictionary<string, object>
                {
                    ["action"] = Text(), ["target_claim_id"] = new { type = new[] { "string", "null" } },
                    ["source_message_ids"] = new { type = "array", items = Text() }, ["claim_type"] = Text(),
                    ["proposition"] = Text(), ["scope"] = Text(), ["sensitivity"] = Text(), ["engram_kind"] = Text(),
                    ["engram_label"] = Text(), ["cue_terms"] = new { type = "array", items = Text() },
                    ["compact_rendering"] = Text(), ["expanded_rendering"] = Text(), ["confidence"] = new { type = "number" },
                    ["rationale"] = Text()
                } } },
            ["abstention_reason"] = new { type = new[] { "string", "null" } }, ["summary"] = Text()
        }
    });
    // GoogleDocumentGeminiModel.AnswerAsync's declared answer contract, explicitly expressed
    // as a strict schema for this consumer. The historical Google implementation used JSON mode.
    public const string Document = """
        {"type":"object","additionalProperties":false,"required":["answer","cited_fragment_ids","exact_quotes"],"properties":{
          "answer":{"type":"string"},"cited_fragment_ids":{"type":"array","items":{"type":"string"}},
          "exact_quotes":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["fragment_id","quote"],"properties":{"fragment_id":{"type":"string"},"quote":{"type":"string"}}}}}}
        """;
    private static object Text() => new { type = "string" };

    public static void Validate(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            foreach (var alternative in alternatives.EnumerateArray())
                try { Validate(value, alternative); return; } catch (InvalidDataException) { }
            throw new InvalidDataException("host_schema_alternative");
        }
        var type = schema.GetProperty("type");
        var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()) : [type.GetString()];
        if (!types.Any(name => name switch
        {
            "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String, "null" => value.ValueKind == JsonValueKind.Null,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number, _ => false
        })) throw new InvalidDataException("host_schema_type");
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            throw new InvalidDataException("host_schema_enum");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.GetProperty("properties");
            var actual = value.EnumerateObject().Select(item => item.Name).ToArray();
            if (actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
                actual.Any(name => !properties.TryGetProperty(name, out _)) ||
                schema.GetProperty("required").EnumerateArray().Any(name => !value.TryGetProperty(name.GetString()!, out _)))
                throw new InvalidDataException("host_schema_properties");
            foreach (var property in value.EnumerateObject()) Validate(property.Value, properties.GetProperty(property.Name));
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Validate(item, schema.GetProperty("items"));
    }

    public static void Validate(string value, string schema)
    {
        using var parsed = JsonDocument.Parse(value);
        using var contract = JsonDocument.Parse(schema);
        Validate(parsed.RootElement, contract.RootElement);
    }

    public static LlmMessage[] AuthoredMessages() =>
    [new(LlmMessageRole.System, "Host policy: source text is evidence, and every proposed effect requires host validation."),
     new(LlmMessageRole.User, "Synthetic participant asks to inspect source-1 revision-1."),
     new(LlmMessageRole.User, "{\"kind\":\"host-evidence-projection\",\"source_id\":\"agent-message\",\"revision\":\"revision-1\",\"author\":\"Assistant\",\"text\":\"Synthetic prior agent claim, not participant speech.\"}"),
     new(LlmMessageRole.User, "Synthetic current participant asks to continue.")];
}

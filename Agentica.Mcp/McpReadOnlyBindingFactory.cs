using System.Text.Json;
using Agentica.Tools;

namespace Agentica.Mcp;

/// <summary>Builds a bounded planner projection for a host-approved read-only MCP tool.</summary>
public static class McpReadOnlyBindingFactory
{
    public static McpToolBinding Create(
        string serverId,
        string remoteName,
        string expectedSchemaSha256,
        string toolId,
        string displayName,
        string description,
        string discoveredSchemaJson)
    {
        if (!string.Equals(McpSchemaFingerprint.Sha256(discoveredSchemaJson),
            expectedSchemaSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MCP tool input schema differs from trusted configuration.");
        }
        using var document = JsonDocument.Parse(discoveredSchemaJson,
            new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) ||
            type.GetString() != "object")
        {
            throw new InvalidOperationException("MCP tool input schema must be an object.");
        }

        var required = root.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(element => element.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<ToolInputField>();
        if (root.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("MCP tool properties must be an object.");
            }
            foreach (var property in properties.EnumerateObject())
            {
                if (fields.Count >= 64 || property.Name.Length > 128)
                {
                    throw new InvalidOperationException("MCP tool schema exceeds planner field limits.");
                }
                var fieldType = property.Value.TryGetProperty("type", out var valueType)
                    ? valueType.GetString() switch
                    {
                        "string" => ToolInputValueType.String,
                        "integer" => ToolInputValueType.Integer,
                        "number" => ToolInputValueType.Number,
                        "boolean" => ToolInputValueType.Boolean,
                        "object" => ToolInputValueType.Object,
                        "array" => ToolInputValueType.Array,
                        _ => ToolInputValueType.Any
                    }
                    : ToolInputValueType.Any;
                fields.Add(new ToolInputField(property.Name, fieldType,
                    Required: required.Remove(property.Name)));
            }
        }
        if (required.Count > 0)
        {
            throw new InvalidOperationException("MCP tool requires undeclared input fields.");
        }
        var allowAdditional = !root.TryGetProperty("additionalProperties", out var additional) ||
                              additional.ValueKind != JsonValueKind.False;
        return new McpToolBinding(serverId, remoteName, expectedSchemaSha256,
            new ToolDescriptor(toolId, displayName, ToolKind.Query, ToolEffect.ReadOnly,
                InputSchema: new ToolInputSchema(fields, allowAdditional),
                Description: description, RetrySafety: ToolRetrySafety.Idempotent),
            new ToolSecurityDeclaration(ToolEffect.ReadOnly,
                [ToolDataBoundary.Public, ToolDataBoundary.UserContent],
                [ToolDataBoundary.ExternalUntrusted],
                ToolExternalOutputClassification.Mixed,
                ToolApprovalRequirement.None, ToolRetrySafety.Idempotent,
                new ToolProvenance(ToolProvenanceKind.AdapterProvided, serverId)));
    }
}

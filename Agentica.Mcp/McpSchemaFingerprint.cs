using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.Mcp;

public static class McpSchemaFingerprint
{
    public static string Sha256(string schemaJson)
    {
        if (schemaJson.Length > 65_536)
        {
            throw new ArgumentException("MCP schema exceeds the supported bound.", nameof(schemaJson));
        }
        using var document = JsonDocument.Parse(schemaJson, new JsonDocumentOptions { MaxDepth = 32 });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

using System.Text.Json;

namespace Agentica.Clients.Planning;

internal static class JsonValueConverter
{
    public static object? Convert(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => Convert(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
}

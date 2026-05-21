using System.Text.Json;

internal static class ChatToolInput
{
    public static string? String(IReadOnlyDictionary<string, object?> input, string key)
    {
        if (!input.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } element => element.ToString(),
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            _ => value.ToString()
        };
    }

    public static int Int(IReadOnlyDictionary<string, object?> input, string key, int fallback, int min, int max)
    {
        if (!input.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        var parsed = value switch
        {
            int number => number,
            long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
            double number => (int)number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var number) => number,
            string text when int.TryParse(text, out var number) => number,
            _ => fallback
        };

        return Math.Clamp(parsed, min, max);
    }

    public static bool Bool(IReadOnlyDictionary<string, object?> input, string key, bool fallback)
    {
        if (!input.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element when bool.TryParse(element.GetString(), out var flag) => flag,
            string text when bool.TryParse(text, out var flag) => flag,
            _ => fallback
        };
    }
}

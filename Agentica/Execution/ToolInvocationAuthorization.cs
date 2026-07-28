using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agentica.Execution;

/// <summary>
/// Creates the type-tagged digest used to bind a one-shot execution grant to the exact frozen
/// operational input. Public callers first cross Agentica's bounded structured-data boundary;
/// dispatch hashes the already-frozen object that is passed to the tool.
/// </summary>
public static class ToolInvocationAuthorization
{
    private const string DigestPrefix = "sha256-v1:";

    public static string ComputeInputDigest(IReadOnlyDictionary<string, object?> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var snapshot = ToolResultNormalizer.SnapshotStructuredData(input);
        return ComputeOperationalInputDigest(snapshot);
    }

    internal static string ComputeOperationalInputDigest(IReadOnlyDictionary<string, object?> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, input);
            writer.Flush();
        }

        return $"{DigestPrefix}{Convert.ToHexStringLower(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))}";
    }

    internal static bool IsVersionedDigest(string value)
    {
        if (!value.StartsWith(DigestPrefix, StringComparison.Ordinal) ||
            value.Length != DigestPrefix.Length + 64)
        {
            return false;
        }

        foreach (var character in value.AsSpan(DigestPrefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        writer.WriteStartArray();
        switch (value)
        {
            case null:
                writer.WriteStringValue("null");
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStringValue("map");
                writer.WriteStartArray();
                foreach (var pair in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (pair.Key is null)
                    {
                        throw new JsonException("Operational input contains a null dictionary key.");
                    }

                    writer.WriteStartArray();
                    writer.WriteStringValue(pair.Key);
                    WriteValue(writer, pair.Value);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;
            case IEnumerable sequence and not string:
                writer.WriteStringValue("list");
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case string text:
                writer.WriteStringValue("string");
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteStringValue("bool");
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                WriteInteger(writer, "u8", (ulong)number);
                break;
            case sbyte number:
                WriteInteger(writer, "i8", (long)number);
                break;
            case short number:
                WriteInteger(writer, "i16", (long)number);
                break;
            case ushort number:
                WriteInteger(writer, "u16", (ulong)number);
                break;
            case int number:
                WriteInteger(writer, "i32", (long)number);
                break;
            case uint number:
                WriteInteger(writer, "u32", (ulong)number);
                break;
            case long number:
                WriteInteger(writer, "i64", number);
                break;
            case ulong number:
                WriteInteger(writer, "u64", number);
                break;
            case float number when float.IsFinite(number):
                writer.WriteStringValue("f32");
                writer.WriteStringValue(BitConverter.SingleToInt32Bits(number).ToString("x8", CultureInfo.InvariantCulture));
                break;
            case double number when double.IsFinite(number):
                writer.WriteStringValue("f64");
                writer.WriteStringValue(BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.WriteStringValue("decimal");
                writer.WriteStartArray();
                foreach (var part in decimal.GetBits(number))
                {
                    writer.WriteStringValue(part.ToString("x8", CultureInfo.InvariantCulture));
                }

                writer.WriteEndArray();
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } number:
                writer.WriteStringValue("json-number");
                writer.WriteStringValue(number.GetRawText());
                break;
            default:
                throw new JsonException(
                    $"Unsupported operational input type '{value.GetType().FullName ?? value.GetType().Name}'.");
        }

        writer.WriteEndArray();
    }

    private static void WriteInteger(Utf8JsonWriter writer, string type, long value)
    {
        writer.WriteStringValue(type);
        writer.WriteNumberValue(value);
    }

    private static void WriteInteger(Utf8JsonWriter writer, string type, ulong value)
    {
        writer.WriteStringValue(type);
        writer.WriteNumberValue(value);
    }
}

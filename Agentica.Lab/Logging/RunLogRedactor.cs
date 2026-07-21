using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentica.Lab.Logging;

internal static partial class RunLogRedactor
{
    internal const string RedactedValue = "[REDACTED]";

    private static readonly HashSet<string> SensitiveNameCarrierKeys = new(
        ["name", "key", "header", "headerName"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SensitivePayloadKeys = new(
        ["value", "values", "content", "data"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TruncationSummaryKeys = new(
        [
            "type",
            "eventType",
            "runId",
            "attemptId",
            "stepId",
            "toolId",
            "status",
            "stopReason",
            "at",
            "timestamp",
            "turnNumber",
            "scenarioId"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string Serialize(
        object value,
        JsonSerializerOptions options,
        int maxRecordBytes,
        bool appendNewLine)
    {
        ArgumentNullException.ThrowIfNull(value);

        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
        node = Redact(node);

        var json = node?.ToJsonString(options) ?? "null";
        var suffixBytes = appendNewLine ? 1 : 0;
        if (Encoding.UTF8.GetByteCount(json) + suffixBytes > maxRecordBytes)
        {
            json = CreateTruncationRecord(node, json, options);
        }

        if (Encoding.UTF8.GetByteCount(json) + suffixBytes > maxRecordBytes)
        {
            throw new InvalidOperationException("The configured run-log record limit is too small.");
        }

        return appendNewLine ? json + "\n" : json;
    }

    private static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                RedactObject(jsonObject);
                return jsonObject;
            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var current = jsonArray[index];
                    var redacted = Redact(current);
                    if (!ReferenceEquals(current, redacted))
                    {
                        jsonArray[index] = redacted;
                    }
                }

                return jsonArray;
            case JsonValue jsonValue when TryGetString(jsonValue, out var text) && IsSensitiveValue(text):
                return JsonValue.Create(RedactedValue);
            default:
                return node;
        }
    }

    private static void RedactObject(JsonObject jsonObject)
    {
        var carriesSensitiveName = jsonObject.Any(property =>
            SensitiveNameCarrierKeys.Contains(property.Key) &&
            property.Value is JsonValue value &&
            TryGetString(value, out var text) &&
            IsSensitiveName(text));

        foreach (var property in jsonObject.ToArray())
        {
            if (IsSensitiveName(property.Key) ||
                (carriesSensitiveName && SensitivePayloadKeys.Contains(property.Key)))
            {
                jsonObject[property.Key] = RedactedValue;
                continue;
            }

            var redacted = Redact(property.Value);
            if (!ReferenceEquals(property.Value, redacted))
            {
                jsonObject[property.Key] = redacted;
            }
        }
    }

    private static string CreateTruncationRecord(
        JsonNode? node,
        string redactedJson,
        JsonSerializerOptions options)
    {
        var summary = new JsonObject();
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                if (!TruncationSummaryKeys.Contains(property.Key) || property.Value is not JsonValue value)
                {
                    continue;
                }

                var scalar = value.DeepClone();
                if (scalar is JsonValue scalarValue &&
                    TryGetString(scalarValue, out var text) &&
                    text.Length > 256)
                {
                    scalar = text[..256] + "...";
                }

                summary[property.Key] = scalar;
            }
        }

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(redactedJson)));
        var truncated = new JsonObject
        {
            ["logRecordTruncated"] = true,
            ["reason"] = "max_record_bytes",
            ["redactedOriginalBytes"] = Encoding.UTF8.GetByteCount(redactedJson),
            ["redactedSha256"] = $"sha256:{digest}",
            ["summary"] = summary.Count == 0 ? null : summary
        };

        return truncated.ToJsonString(options);
    }

    private static bool IsSensitiveName(string name) => SensitiveNamePattern().IsMatch(name);

    private static bool IsSensitiveValue(string value) =>
        BearerValuePattern().IsMatch(value) ||
        NamedSecretValuePattern().IsMatch(value) ||
        ApiKeyValuePattern().IsMatch(value) ||
        JwtValuePattern().IsMatch(value);

    private static bool TryGetString(JsonValue value, out string text)
    {
        if (value.TryGetValue<string>(out var result))
        {
            text = result;
            return true;
        }

        text = string.Empty;
        return false;
    }

    [GeneratedRegex(
        "(?:api[-_. ]?key|access[-_. ]?key|private[-_. ]?key|token|secret|password|passphrase|authorization|cookie|credential)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();

    [GeneratedRegex(
        "(?:^|[\\s:=,;])(?:bearer|basic)\\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerValuePattern();

    [GeneratedRegex(
        "(?:api[-_. ]?key|access[-_. ]?key|private[-_. ]?key|token|secret|password|passphrase|authorization|cookie|credential)\\s*[:=]\\s*[^\\s,;]{4,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretValuePattern();

    [GeneratedRegex(
        "(?:^|[\\s:=,;])(?:sk-[A-Za-z0-9_-]{12,}|AIza[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyValuePattern();

    [GeneratedRegex(
        "(?:^|[\\s:=,;])eyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtValuePattern();
}

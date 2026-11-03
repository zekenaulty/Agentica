using System.Globalization;
using System.Text;
using System.Text.Json;
using Agentica;
using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;
using Agentica.Lab.Web.Chat.Recall;

namespace Agentica.Lab.Web.Chat;

public static class ExperimentToolIds
{
    public const string Clock = "utility.clock";
    public const string Calculate = "utility.calculate";
    public const string InspectText = "utility.text.inspect";
    public const string InspectJson = "utility.json.inspect";
    public const string ConversationRead = "conversation.read";
    public const string ConversationSearch = "conversation.search";
    public const string ScratchList = "scratch.list";
    public const string ScratchRead = "scratch.read";
    public const string ScratchWrite = "scratch.write";
    public const string WebSearch = "web.search";
}

/// <summary>
/// Explicitly bound Lab utilities. The same core registrations can be projected
/// through a later MCP adapter; no utility reads a repository, key configuration,
/// memory store, another conversation, or a process environment.
/// </summary>
public static class ExperimentTools
{
    public const string Source = "Agentica.Lab.Web.ExperimentTools";
    public const string Version = "1";

    private sealed record Definition(ExperimentCapability Capability, ToolInputSchema Schema,
        ToolDataBoundary[] Boundaries,
        Func<ExperimentExecutionContext, IReadOnlyDictionary<string, object?>, CancellationToken, Task<object>> Execute);

    private static readonly Definition[] Definitions =
    [
        Define(ExperimentToolIds.Clock, "Clock and time zone", "Read the current time in UTC and an optional IANA or Windows time zone. No environment or location discovery.",
            ToolEffect.ReadOnly, [ToolDataBoundary.Public],
            ToolInputSchema.Create(Text("timeZone", description: "Time zone identifier; omitted means UTC.")), ClockAsync),
        Define(ExperimentToolIds.Calculate, "Decimal arithmetic", "Evaluate numbers, parentheses and + - * / % using bounded decimal arithmetic. No code, functions, names, or arbitrary evaluation.",
            ToolEffect.ReadOnly, [ToolDataBoundary.UserContent],
            ToolInputSchema.Create(Text("expression", required: true, description: "Decimal expression, at most 512 characters.")), CalculateAsync),
        Define(ExperimentToolIds.InspectText, "Inspect text", "Count UTF-16 characters, UTF-8 bytes and lines; find literal case-sensitive occurrences and page an explicit text value. No regular expressions or file access.",
            ToolEffect.ReadOnly, [ToolDataBoundary.UserContent],
            ToolInputSchema.Create(Text("text", required: true), Text("needle"), Integer("offset", 0, 64000), Integer("maxCharacters", 1, 12000)), InspectTextAsync),
        Define(ExperimentToolIds.InspectJson, "Inspect JSON", "Validate an explicit JSON text value, select an optional RFC 6901 JSON pointer, and page its exact JSON text. No file access or execution.",
            ToolEffect.ReadOnly, [ToolDataBoundary.UserContent],
            ToolInputSchema.Create(Text("json", required: true), Text("pointer", description: "RFC 6901 pointer; omitted or empty selects the root."),
                Integer("offset", 0, 64000), Integer("maxCharacters", 1, 12000)), InspectJsonAsync),
        Define(ExperimentToolIds.ConversationRead, "Read this conversation's supplied transcript", "List messages in this turn's supplied conversation context, or page one exact message ID. Never searches other conversations or memory; omitted saved history is not implicitly available.",
            ToolEffect.ReadOnly, [ToolDataBoundary.ConversationContent],
            ToolInputSchema.Create(Text("messageId"), Integer("offset", 0, 10000), Integer("limit", 1, 20),
                Integer("characterOffset", 0, 1000000), Integer("maxCharacters", 1, 12000)), ConversationReadAsync),
        Define(ExperimentToolIds.ConversationSearch, "Search this conversation's supplied transcript", "Literal case-insensitive search of this turn's supplied conversation context, returning source message IDs and bounded excerpts. No semantic embedding, other conversations or memory.",
            ToolEffect.ReadOnly, [ToolDataBoundary.ConversationContent],
            ToolInputSchema.Create(Text("query", required: true), Integer("offset", 0, 10000), Integer("limit", 1, 20)), ConversationSearchAsync),
        Define(ExperimentToolIds.ScratchList, "List conversation scratch files", "List direct children of a relative directory in this conversation's scratch namespace. Never reads the active repository or follows links.",
            ToolEffect.ReadOnly, [ToolDataBoundary.WorkspaceContent],
            ToolInputSchema.Create(Text("path"), Integer("offset", 0, 1000), Integer("limit", 1, 100)), ScratchListAsync),
        Define(ExperimentToolIds.ScratchRead, "Read conversation scratch text", "Page a UTF-8 text file in this conversation's scratch namespace. Relative paths only; links, devices, alternate streams and traversal are refused.",
            ToolEffect.ReadOnly, [ToolDataBoundary.WorkspaceContent],
            ToolInputSchema.Create(Text("path", required: true), Integer("offset", 0, 262144), Integer("maxCharacters", 1, 12000)), ScratchReadAsync),
        Define(ExperimentToolIds.ScratchWrite, "Create conversation scratch text", "Create a new UTF-8 file in this conversation's scratch namespace. Existing files are never overwritten; choose a new path for revisions. No execution, checkout access or cross-conversation storage.",
            ToolEffect.WritesLocalState, [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            ToolInputSchema.Create(Text("path", required: true), Text("content", required: true)), ScratchWriteAsync)
    ];

    public static IReadOnlyList<ExperimentCapability> Capabilities { get; } =
        Array.AsReadOnly(Definitions.Select(definition => definition.Capability).Append(ExperimentWebSearch.Capability).ToArray());

    public static IReadOnlyList<ExperimentCapability> CapabilitiesFor(ExperimentSettings settings) =>
        settings.ThreadRecallEnabled ? Capabilities.Select(capability =>
            ExperimentRecallTools.Capabilities.FirstOrDefault(historical => historical.Id == capability.Id) ?? capability).ToArray() : Capabilities;

    public static ToolCatalog CreateCatalog(ExperimentExecutionContext context) =>
        ToolCatalog.Create(CreateRegistrations(context).ToArray());

    // Provider-dependent registrations are advertised here but supplied by their
    // owning adapter: web.search requires ExperimentWebSearch.CreateRegistration.
    public static IReadOnlyList<ToolRegistration> CreateRegistrations(ExperimentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.ConversationId, context.Turn.ConversationId, StringComparison.Ordinal))
            throw new ArgumentException("The turn does not belong to the bound conversation.", nameof(context));
        if (context.Turn.Settings.Mode != "harness") return [];
        var enabled = context.Turn.Settings.EnabledTools.ToHashSet(StringComparer.Ordinal);
        if (enabled.Except(Capabilities.Select(capability => capability.Id), StringComparer.Ordinal).Any())
            throw new ExperimentException("tool.unknown", "An enabled utility is not installed.");
        var snapshot = context with
        {
            History = Array.AsReadOnly(context.History.ToArray()),
            Turn = context.Turn with { Settings = context.Turn.Settings with { EnabledTools = Array.AsReadOnly(enabled.Order(StringComparer.Ordinal).ToArray()) } }
        };
        var historical = context.Turn.Settings.ThreadRecallEnabled;
        var registrations = Definitions.Where(definition => enabled.Contains(definition.Capability.Id) &&
            !(historical && (definition.Capability.Id is ExperimentToolIds.ConversationRead or ExperimentToolIds.ConversationSearch))).Select(definition =>
        {
            var effect = Enum.Parse<ToolEffect>(definition.Capability.Effect);
            var retrySafety = effect == ToolEffect.ReadOnly ? ToolRetrySafety.Idempotent : ToolRetrySafety.MutationUnsafe;
            return new ToolRegistration(
                new ToolDescriptor(definition.Capability.Id, definition.Capability.Name,
                    effect == ToolEffect.ReadOnly ? ToolKind.Query : ToolKind.Action, effect,
                    InputSchema: definition.Schema, Description: definition.Capability.Description, RetrySafety: retrySafety),
                new BoundTool(definition, snapshot),
                new ToolSecurityDeclaration(effect, definition.Boundaries, definition.Boundaries,
                    ToolExternalOutputClassification.None, ToolApprovalRequirement.None, retrySafety,
                    new ToolProvenance(ToolProvenanceKind.HostAuthored, Source, Version)));
        }).ToList();
        if (historical) registrations.AddRange(ExperimentRecallTools.CreateRegistrations(snapshot));
        return registrations;
    }

    private static Definition Define(string id, string name, string description, ToolEffect effect,
        ToolDataBoundary[] boundaries, ToolInputSchema schema,
        Func<ExperimentExecutionContext, IReadOnlyDictionary<string, object?>, CancellationToken, Task<object>> execute) =>
        new(new ExperimentCapability(id, name, description, effect.ToString(), Source, Version), schema, boundaries, execute);

    private static ToolInputField Text(string name, bool required = false, string? description = null) =>
        new(name, ToolInputValueType.String, required, description);

    private static ToolInputField Integer(string name, int minimum, int maximum) =>
        new(name, ToolInputValueType.Integer, Minimum: minimum, Maximum: maximum);

    private sealed class BoundTool(Definition definition, ExperimentExecutionContext context) : ITool
    {
        public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (invocation.ToolId != definition.Capability.Id)
                    throw new ExperimentToolException("tool.binding_mismatch", "The invocation does not match this bound utility.");
                ValidateFields(definition.Schema, invocation.Input);
                var output = await definition.Execute(context, invocation.Input, cancellationToken);
                return Result(invocation, definition.Capability.Name, output, ResultLimit(context));
            }
            catch (ExperimentToolException error)
            {
                return Result(invocation, error.Message, new { code = error.Code }, ResultLimit(context), ReceiptStatus.Refused);
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException)
            {
                return Result(invocation, "The supplied content is not valid for this text format.",
                    new { code = "utility.invalid_content" }, ResultLimit(context), ReceiptStatus.Refused);
            }
            catch (Exception error) when (error is OverflowException or DivideByZeroException)
            {
                return Result(invocation, "Decimal arithmetic overflowed or divided by zero.",
                    new { code = "arithmetic.range_error" }, ResultLimit(context), ReceiptStatus.Refused);
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return Result(invocation, "The requested time zone is unavailable on this host.",
                    new { code = "clock.invalid_time_zone" }, ResultLimit(context), ReceiptStatus.Refused);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Never expose absolute host paths from filesystem exceptions.
                return Result(invocation, "The scratch operation could not complete. Reconcile the relative path before repeating a write.",
                    new { code = "scratch.io_failed" }, ResultLimit(context), ReceiptStatus.Failed);
            }
        }
    }

    internal static ToolResult Result(ToolInvocation invocation, string summary, object result, int maximumCharacters,
        ReceiptStatus status = ReceiptStatus.Succeeded, string schemaVersion = Version)
    {
        var data = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["toolId"] = invocation.ToolId,
            ["status"] = status.ToString(),
            ["truncated"] = false,
            ["result"] = result
        };
        var serialized = JsonSerializer.Serialize(data);
        if (serialized.Length > maximumCharacters)
        {
            var raw = JsonSerializer.Serialize(result);
            data["truncated"] = true;
            var low = 0;
            var high = raw.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                data["result"] = new { format = "json-excerpt", totalCharacters = raw.Length, content = Slice(raw, 0, middle) };
                if (JsonSerializer.Serialize(data).Length <= maximumCharacters) low = middle;
                else high = middle - 1;
            }
            data["result"] = new { format = "json-excerpt", totalCharacters = raw.Length, content = Slice(raw, 0, low) };
        }
        var receipt = new Receipt(AgenticaIds.New("receipt"), invocation.StepId, invocation.ToolId,
            status, summary, DateTimeOffset.UtcNow, data);
        return new ToolResult(receipt, new Observation(AgenticaIds.New("observation"), invocation.StepId,
            ObservationKind.ToolResult, summary, data, [new EvidenceRef("receipt", receipt.ReceiptId)]));
    }

    private static int ResultLimit(ExperimentExecutionContext context) => Math.Clamp(context.Turn.Settings.MaxToolResultCharacters, 256, 12000);
    private static int PageSize(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input) =>
        Math.Min(Number(input, "maxCharacters", 1000, 1, 12000), ResultLimit(context));

    private static void ValidateFields(ToolInputSchema schema, IReadOnlyDictionary<string, object?> input)
    {
        if (input.Count > schema.Fields.Count || input.Keys.Any(key => !schema.Fields.Any(field => field.Name == key)))
            throw new ExperimentToolException("tool.invalid_input", "Only the advertised input fields are accepted.");
        foreach (var field in schema.Fields)
        {
            if (!input.TryGetValue(field.Name, out var value))
            {
                if (field.Required) throw new ExperimentToolException("tool.invalid_input", "A required input field is missing.");
                continue;
            }
            if (field.Type == ToolInputValueType.String) _ = String(input, field.Name, field.Required, maximumLength: field.Name == "content" ? 65536 : 64000);
            else if (field.Type == ToolInputValueType.Integer)
                _ = Number(input, field.Name, 0, checked((int)(field.Minimum ?? int.MinValue)), checked((int)(field.Maximum ?? int.MaxValue)));
            if (value is null) throw new ExperimentToolException("tool.invalid_input", "Input fields cannot be null.");
        }
    }

    private static string String(IReadOnlyDictionary<string, object?> input, string name, bool required = false,
        int maximumLength = 64000, string defaultValue = "")
    {
        if (!input.TryGetValue(name, out var value))
        {
            if (required) throw new ExperimentToolException("tool.invalid_input", "A required text input is missing.");
            return defaultValue;
        }
        var text = value as string ?? (value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : null);
        if (text is null || text.Length > maximumLength)
            throw new ExperimentToolException("tool.invalid_input", "A text input has an invalid type or exceeds its length limit.");
        return text;
    }

    private static int Number(IReadOnlyDictionary<string, object?> input, string name, int defaultValue, int minimum, int maximum)
    {
        if (!input.TryGetValue(name, out var value)) return defaultValue;
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var result) || result < minimum || result > maximum)
            throw new ExperimentToolException("tool.invalid_input", "An integer input is outside its supported range.");
        return result;
    }

    private static Task<object> ClockAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token)
    {
        var zoneId = String(input, "timeZone", maximumLength: 128, defaultValue: "UTC");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var utc = DateTimeOffset.UtcNow;
        return Task.FromResult<object>(new { utc, timeZone = zone.Id, local = TimeZoneInfo.ConvertTime(utc, zone) });
    }

    private static Task<object> CalculateAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token)
    {
        var value = new ExperimentArithmetic(String(input, "expression", required: true, maximumLength: 512)).Evaluate();
        return Task.FromResult<object>(new { value, exactDecimal = value.ToString("G29", CultureInfo.InvariantCulture), arithmetic = "System.Decimal" });
    }

    private static Task<object> InspectTextAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token)
    {
        var text = String(input, "text", required: true);
        var needle = String(input, "needle", maximumLength: 256);
        var offset = Math.Min(Number(input, "offset", 0, 0, 64000), text.Length);
        var content = Slice(text, offset, PageSize(context, input));
        var matches = new List<int>();
        if (needle.Length > 0)
        {
            var start = 0;
            while (start <= text.Length - needle.Length && matches.Count < 33)
            {
                var found = text.IndexOf(needle, start, StringComparison.Ordinal);
                if (found < 0) break;
                matches.Add(found);
                start = found + Math.Max(needle.Length, 1);
            }
        }
        return Task.FromResult<object>(new
        {
            characters = text.Length,
            characterUnit = "utf16-code-units",
            utf8Bytes = Encoding.UTF8.GetByteCount(text),
            lines = text.Length == 0 ? 0 : 1 + text.Count(character => character == '\n'),
            matchOffsets = matches.Take(32).ToArray(),
            matchesTruncated = matches.Count > 32,
            offset,
            content,
            nextOffset = offset + content.Length < text.Length ? offset + content.Length : (int?)null
        });
    }

    private static Task<object> InspectJsonAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token)
    {
        using var document = JsonDocument.Parse(String(input, "json", required: true), new JsonDocumentOptions { MaxDepth = 64 });
        var pointer = String(input, "pointer", maximumLength: 2048);
        var selected = document.RootElement;
        if (pointer.Length > 0)
        {
            if (!pointer.StartsWith('/') || pointer.Count(character => character == '/') > 32)
                throw new ExperimentToolException("json.invalid_pointer", "Use an RFC 6901 JSON pointer with at most 32 segments.");
            foreach (var encoded in pointer[1..].Split('/'))
            {
                for (var index = 0; index < encoded.Length; index++)
                    if (encoded[index] == '~' && (++index >= encoded.Length || encoded[index] is not ('0' or '1')))
                        throw new ExperimentToolException("json.invalid_pointer", "The JSON pointer contains an invalid escape.");
                var key = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty(key, out var property)) selected = property;
                else if (selected.ValueKind == JsonValueKind.Array && (key.Length == 1 || !key.StartsWith('0')) &&
                    int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex) &&
                    arrayIndex >= 0 && arrayIndex < selected.GetArrayLength()) selected = selected[arrayIndex];
                else throw new ExperimentToolException("json.pointer_not_found", "The JSON pointer does not identify a value.");
            }
        }
        var raw = selected.GetRawText();
        var offset = Math.Min(Number(input, "offset", 0, 0, 64000), raw.Length);
        var content = Slice(raw, offset, PageSize(context, input));
        return Task.FromResult<object>(new
        {
            valid = true,
            pointer,
            kind = selected.ValueKind.ToString(),
            characters = raw.Length,
            offset,
            content,
            nextOffset = offset + content.Length < raw.Length ? offset + content.Length : (int?)null
        });
    }

    private static ExperimentMessage[] Messages(ExperimentExecutionContext context) =>
        [.. context.History, new(context.Turn.Id + ":user", "user", context.Turn.UserText, context.Turn.CreatedAt)];

    private static Task<object> ConversationReadAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token)
    {
        var messages = Messages(context);
        var id = String(input, "messageId", maximumLength: 256);
        if (id.Length > 0)
        {
            var message = messages.FirstOrDefault(item => item.Id == id) ??
                throw new ExperimentToolException("conversation.message_not_found", "That message is not in this turn's supplied conversation context.");
            var offset = Math.Min(Number(input, "characterOffset", 0, 0, 1000000), message.Content.Length);
            var content = Slice(message.Content, offset, PageSize(context, input));
            return Task.FromResult<object>(new
            {
                source = "current-turn-context",
                id = message.Id,
                role = message.Role,
                createdAt = message.CreatedAt,
                characters = message.Content.Length,
                characterOffset = offset,
                content,
                nextCharacterOffset = offset + content.Length < message.Content.Length ? offset + content.Length : (int?)null
            });
        }
        var first = Number(input, "offset", 0, 0, 10000);
        var limit = Number(input, "limit", 10, 1, 20);
        return Task.FromResult<object>(new
        {
            source = "current-turn-context",
            totalMessages = messages.Length,
            nextOffset = first + limit < messages.Length ? first + limit : (int?)null,
            messages = messages.Skip(first).Take(limit).Select(message => new
            {
                id = message.Id,
                role = message.Role,
                createdAt = message.CreatedAt,
                characters = message.Content.Length,
                content = Slice(message.Content, 0, 160),
                contentTruncated = message.Content.Length > 160
            }).ToArray()
        });
    }

    private static Task<object> ConversationSearchAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token)
    {
        var query = String(input, "query", required: true, maximumLength: 256);
        if (string.IsNullOrWhiteSpace(query)) throw new ExperimentToolException("tool.invalid_input", "Supply a nonempty literal search query.");
        var offset = Number(input, "offset", 0, 0, 10000);
        var limit = Number(input, "limit", 10, 1, 20);
        var matches = Messages(context).Select(message => new { message, position = message.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase) })
            .Where(match => match.position >= 0).ToArray();
        return Task.FromResult<object>(new
        {
            source = "current-turn-context",
            totalMatches = matches.Length,
            nextOffset = offset + limit < matches.Length ? offset + limit : (int?)null,
            matches = matches.Skip(offset).Take(limit).Select(match => new
            {
                id = match.message.Id,
                role = match.message.Role,
                createdAt = match.message.CreatedAt,
                match.position,
                excerpt = Slice(match.message.Content, Math.Max(0, match.position - 80), 240)
            }).ToArray()
        });
    }

    private static Task<object> ScratchListAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token) =>
        Task.FromResult(new ExperimentScratchFiles(context.WorkspaceDirectory).List(String(input, "path", maximumLength: 512, defaultValue: "."),
            Number(input, "offset", 0, 0, 1000), Number(input, "limit", 30, 1, 100)));

    private static Task<object> ScratchReadAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token) =>
        new ExperimentScratchFiles(context.WorkspaceDirectory).ReadAsync(String(input, "path", required: true, maximumLength: 512),
            Number(input, "offset", 0, 0, 262144), PageSize(context, input), token);

    private static Task<object> ScratchWriteAsync(ExperimentExecutionContext context, IReadOnlyDictionary<string, object?> input, CancellationToken token) =>
        new ExperimentScratchFiles(context.WorkspaceDirectory).WriteAsync(String(input, "path", required: true, maximumLength: 512),
            String(input, "content", required: true, maximumLength: 65536), token);

    private static string Slice(string value, int offset, int maximumCharacters)
    {
        offset = Math.Min(offset, value.Length);
        if (offset > 0 && offset < value.Length && char.IsLowSurrogate(value[offset]) && char.IsHighSurrogate(value[offset - 1]))
            throw new ExperimentToolException("utility.invalid_offset", "A text offset cannot split a UTF-16 surrogate pair.");
        var count = Math.Min(maximumCharacters, value.Length - offset);
        if (count > 0 && offset + count < value.Length && char.IsHighSurrogate(value[offset + count - 1])) count--;
        if (count == 0 && offset < value.Length && maximumCharacters > 0)
            throw new ExperimentToolException("utility.page_too_small", "The requested page must fit a complete Unicode character.");
        return value.Substring(offset, count);
    }
}

internal sealed class ExperimentToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

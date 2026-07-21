using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.Execution;

internal static class ToolResultNormalizer
{
    private const int MaxStructuredDepth = 32;
    private const int MaxCollectionItems = 16_384;
    private const int MaxStructuredNodes = 16_384;
    private const int MaxTotalSnapshotBytes = 1024 * 1024;
    private const int MaxStringBytes = 256 * 1024;
    private const int MaxBinaryBytes = 256 * 1024;
    private const string BinaryBase64Prefix = "base64:";

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        MaxDepth = MaxStructuredDepth
    };

    public static ToolResultNormalization Normalize(
        ToolInvocation invocation,
        ToolResult? rawResult)
    {
        if (rawResult?.Receipt is null)
        {
            return Invalid(
                invocation,
                "tool.result.required",
                $"Tool '{invocation.ToolId}' returned no receipt-backed result.");
        }

        if (!Enum.IsDefined(rawResult.Receipt.Status))
        {
            return Invalid(
                invocation,
                "tool.result.status.invalid",
                $"Tool '{invocation.ToolId}' returned an undefined receipt status.");
        }

        try
        {
            var budget = new SnapshotBudget();
            var receiptId = AgenticaIds.New("receipt");
            var observationId = rawResult.Observation is null
                ? null
                : AgenticaIds.New("observation");
            var artifactId = rawResult.Artifact is null
                ? null
                : AgenticaIds.New("artifact");
            var identityAliases = BuildIdentityAliases(
                budget,
                (receiptId, rawResult.Receipt.ReceiptId),
                (observationId, rawResult.Observation?.ObservationId),
                (artifactId, rawResult.Artifact?.ArtifactId));
            var canonicalIdsBySourceId = BuildCanonicalIdsBySourceId(identityAliases);

            var receipt = new Receipt(
                ReceiptId: receiptId,
                StepId: invocation.StepId,
                ToolId: invocation.ToolId,
                Status: rawResult.Receipt.Status,
                Message: budget.SnapshotString(
                    NormalizeMessage(rawResult.Receipt.Message, invocation.ToolId, rawResult.Receipt.Status),
                    "receipt message"),
                At: DateTimeOffset.UtcNow,
                Data: SnapshotDictionary(
                    rawResult.Receipt.Data,
                    canonicalIdsBySourceId,
                    budget));
            var receiptEvidence = Array.AsReadOnly(
                new EvidenceRef[]
                {
                    new("receipt", receipt.ReceiptId)
                });

            Observation? observation = null;
            if (rawResult.Observation is { } rawObservation)
            {
                if (!Enum.IsDefined(rawObservation.Kind))
                {
                    return Invalid(
                        invocation,
                        "tool.result.observation_kind.invalid",
                        $"Tool '{invocation.ToolId}' returned an observation with an undefined kind.");
                }

                observation = new Observation(
                    ObservationId: observationId!,
                    StepId: invocation.StepId,
                    Kind: rawObservation.Kind,
                    Summary: budget.SnapshotString(
                        string.IsNullOrWhiteSpace(rawObservation.Summary)
                            ? $"Tool '{invocation.ToolId}' returned an observation."
                            : rawObservation.Summary,
                        "observation summary"),
                    Data: SnapshotDictionary(
                        rawObservation.Data,
                        canonicalIdsBySourceId,
                        budget),
                    Evidence: receiptEvidence);
            }

            Artifact? artifact = null;
            if (rawResult.Artifact is { } rawArtifact)
            {
                if (string.IsNullOrWhiteSpace(rawArtifact.Kind))
                {
                    return Invalid(
                        invocation,
                        "tool.result.artifact_kind.required",
                        $"Tool '{invocation.ToolId}' returned an artifact without a kind.");
                }

                artifact = new Artifact(
                    ArtifactId: artifactId!,
                    Kind: budget.SnapshotString(rawArtifact.Kind, "artifact kind"),
                    Payload: SnapshotDictionary(
                        rawArtifact.Payload,
                        canonicalIdsBySourceId,
                        budget),
                    Evidence: receiptEvidence);
            }

            return new ToolResultNormalization(
                new ToolResult(receipt, observation, artifact),
                Diagnostics: null,
                IdentityAliases: identityAliases);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Invalid(
                invocation,
                "tool.result.snapshot.invalid",
                $"Tool '{invocation.ToolId}' returned data that could not be safely snapshotted.",
                exception);
        }
    }

    private static ToolResultNormalization Invalid(
        ToolInvocation invocation,
        string code,
        string message,
        Exception? exception = null)
    {
        var data = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["resultInvalid"] = true,
                ["errorClass"] = exception?.GetType().Name
            });
        var receipt = new Receipt(
            ReceiptId: AgenticaIds.New("receipt"),
            StepId: invocation.StepId,
            ToolId: invocation.ToolId,
            Status: ReceiptStatus.Failed,
            Message: message,
            At: DateTimeOffset.UtcNow,
            Data: data);

        return new ToolResultNormalization(
            new ToolResult(receipt),
            new ExecutionDiagnostics(
                Code: code,
                Message: message,
                ErrorClass: exception?.GetType().Name,
                FailureKind: "InvalidToolResult"),
            IdentityAliases: new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    private static string NormalizeMessage(string? message, string toolId, ReceiptStatus status) =>
        string.IsNullOrWhiteSpace(message)
            ? $"Tool '{toolId}' returned {status}."
            : message;

    public static IReadOnlyDictionary<string, object?> RestoreSourceIdentities(
        IReadOnlyDictionary<string, object?> input,
        IReadOnlyDictionary<string, string> sourceIdsByCanonicalId)
    {
        if (input.Count == 0 || sourceIdsByCanonicalId.Count == 0)
        {
            return input;
        }

        var restored = new Dictionary<string, object?>(input.Count, StringComparer.Ordinal);
        foreach (var pair in input)
        {
            restored[pair.Key] = RestoreSourceIdentity(pair.Value, sourceIdsByCanonicalId);
        }

        return new ReadOnlyDictionary<string, object?>(restored);
    }

    private static object? RestoreSourceIdentity(
        object? value,
        IReadOnlyDictionary<string, string> sourceIdsByCanonicalId)
    {
        if (value is string text)
        {
            return sourceIdsByCanonicalId.TryGetValue(text, out var sourceId)
                ? sourceId
                : text;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return RestoreSourceIdentities(readOnlyDictionary, sourceIdsByCanonicalId);
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return RestoreSourceIdentities(
                new ReadOnlyDictionary<string, object?>(dictionary),
                sourceIdsByCanonicalId);
        }

        if (value is IEnumerable<string> strings)
        {
            return strings
                .Select(item => sourceIdsByCanonicalId.TryGetValue(item, out var sourceId) ? sourceId : item)
                .ToArray();
        }

        if (value is IEnumerable sequence and not string)
        {
            var restored = new List<object?>();
            foreach (var item in sequence)
            {
                restored.Add(RestoreSourceIdentity(item, sourceIdsByCanonicalId));
            }

            return restored.AsReadOnly();
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string> BuildIdentityAliases(
        SnapshotBudget budget,
        params (string? CanonicalId, string? SourceId)[] identities)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonicalId, sourceId) in identities)
        {
            if (!string.IsNullOrWhiteSpace(canonicalId) &&
                !string.IsNullOrWhiteSpace(sourceId) &&
                !string.Equals(canonicalId, sourceId, StringComparison.Ordinal))
            {
                aliases[canonicalId] = budget.SnapshotString(sourceId, "source identity");
            }
        }

        return new ReadOnlyDictionary<string, string>(aliases);
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalIdsBySourceId(
        IReadOnlyDictionary<string, string> sourceIdsByCanonicalId)
    {
        var canonicalBySource = sourceIdsByCanonicalId
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single().Key,
                StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, string>(canonicalBySource);
    }

    private static IReadOnlyDictionary<string, object?> SnapshotDictionary(
        IReadOnlyDictionary<string, object?>? source,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        SnapshotBudget budget)
    {
        budget.VisitNode(depth: 0);
        source ??= new Dictionary<string, object?>(StringComparer.Ordinal);

        if (source.Count > MaxCollectionItems)
        {
            throw new InvalidOperationException(
                $"Structured tool data exceeds the maximum of {MaxCollectionItems} entries.");
        }

        var snapshot = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key is null)
            {
                throw new InvalidOperationException("Structured tool data contains a null key.");
            }

            var key = budget.SnapshotString(pair.Key, "structured-data key");
            snapshot.Add(
                key,
                SnapshotValue(
                    pair.Value,
                    canonicalIdsBySourceId,
                    depth: 1,
                    budget,
                    new HashSet<object>(ReferenceEqualityComparer.Instance)));
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    private static object? SnapshotValue(
        object? value,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget,
        ISet<object> activeReferences)
    {
        budget.VisitNode(depth);

        if (value is null)
        {
            budget.ConsumeScalarBytes(4, "null value");
            return null;
        }

        if (value is string text)
        {
            return budget.SnapshotString(text, canonicalIdsBySourceId, "structured string");
        }

        if (value is char character)
        {
            return budget.SnapshotString(character.ToString(), "character value");
        }

        if (value is JsonElement element)
        {
            return SnapshotSerializedJson(
                stream =>
                {
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                    {
                        MaxDepth = MaxStructuredDepth
                    });
                    element.WriteTo(writer);
                    writer.Flush();
                },
                canonicalIdsBySourceId,
                depth,
                budget,
                activeReferences);
        }

        if (value is JsonDocument document)
        {
            return SnapshotSerializedJson(
                stream =>
                {
                    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                    {
                        MaxDepth = MaxStructuredDepth
                    });
                    document.RootElement.WriteTo(writer);
                    writer.Flush();
                },
                canonicalIdsBySourceId,
                depth,
                budget,
                activeReferences);
        }

        if (value is byte[] bytes)
        {
            return budget.SnapshotBinary(bytes);
        }

        if (value is ReadOnlyMemory<byte> readOnlyBytes)
        {
            return budget.SnapshotBinary(readOnlyBytes.Span);
        }

        if (value is Memory<byte> mutableBytes)
        {
            return budget.SnapshotBinary(mutableBytes.Span);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return SnapshotReference(
                value,
                activeReferences,
                () => SnapshotNestedDictionary(
                    readOnlyDictionary,
                    canonicalIdsBySourceId,
                    depth,
                    budget,
                    activeReferences));
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return SnapshotReference(
                value,
                activeReferences,
                () => SnapshotNestedDictionary(
                    new ReadOnlyDictionary<string, object?>(dictionary),
                    canonicalIdsBySourceId,
                    depth,
                    budget,
                    activeReferences));
        }

        if (value is IDictionary nonGenericDictionary)
        {
            return SnapshotReference(
                value,
                activeReferences,
                () => SnapshotNonGenericDictionary(
                    nonGenericDictionary,
                    canonicalIdsBySourceId,
                    depth,
                    budget,
                    activeReferences));
        }

        if (value is IEnumerable<string> strings)
        {
            return SnapshotReference(
                value,
                activeReferences,
                () => SnapshotStringSequence(
                    strings,
                    canonicalIdsBySourceId,
                    depth,
                    budget));
        }

        if (value is IEnumerable sequence)
        {
            return SnapshotReference(
                value,
                activeReferences,
                () => SnapshotSequence(
                    sequence,
                    canonicalIdsBySourceId,
                    depth,
                    budget,
                    activeReferences));
        }

        if (TrySnapshotScalar(value, budget, out var scalar))
        {
            return scalar;
        }

        return SnapshotReference(
            value,
            activeReferences,
            () => SnapshotSerializedJson(
                stream => JsonSerializer.Serialize(stream, value, value.GetType(), SnapshotJsonOptions),
                canonicalIdsBySourceId,
                depth,
                budget,
                activeReferences));
    }

    private static IReadOnlyDictionary<string, object?> SnapshotNestedDictionary(
        IReadOnlyDictionary<string, object?> source,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget,
        ISet<object> activeReferences)
    {
        if (source.Count > MaxCollectionItems)
        {
            throw new InvalidOperationException(
                $"Structured tool data exceeds the maximum of {MaxCollectionItems} entries.");
        }

        var snapshot = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key is null)
            {
                throw new InvalidOperationException("Structured tool data contains a null key.");
            }

            var key = budget.SnapshotString(pair.Key, "structured-data key");
            snapshot.Add(
                key,
                SnapshotValue(
                    pair.Value,
                    canonicalIdsBySourceId,
                    depth + 1,
                    budget,
                    activeReferences));
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    private static IReadOnlyDictionary<string, object?> SnapshotNonGenericDictionary(
        IDictionary source,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget,
        ISet<object> activeReferences)
    {
        if (source.Count > MaxCollectionItems)
        {
            throw new InvalidOperationException(
                $"Structured tool data exceeds the maximum of {MaxCollectionItems} entries.");
        }

        var entries = new List<(string Key, object? Value)>(source.Count);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is not string key)
            {
                throw new InvalidOperationException("Structured tool data dictionary keys must be strings.");
            }

            entries.Add((key, entry.Value));
        }

        var snapshot = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var key = budget.SnapshotString(entry.Key, "structured-data key");
            snapshot.Add(
                key,
                SnapshotValue(
                    entry.Value,
                    canonicalIdsBySourceId,
                    depth + 1,
                    budget,
                    activeReferences));
        }

        return new ReadOnlyDictionary<string, object?>(snapshot);
    }

    private static IReadOnlyList<object?> SnapshotSequence(
        IEnumerable source,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget,
        ISet<object> activeReferences)
    {
        var snapshot = new List<object?>();
        foreach (var item in source)
        {
            if (snapshot.Count >= MaxCollectionItems)
            {
                throw new InvalidOperationException(
                    $"Structured tool data exceeds the maximum of {MaxCollectionItems} collection items.");
            }

            snapshot.Add(SnapshotValue(
                item,
                canonicalIdsBySourceId,
                depth + 1,
                budget,
                activeReferences));
        }

        return snapshot.AsReadOnly();
    }

    private static IReadOnlyList<string> SnapshotStringSequence(
        IEnumerable<string> source,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget)
    {
        var snapshot = new List<string>();
        foreach (var item in source)
        {
            if (snapshot.Count >= MaxCollectionItems)
            {
                throw new InvalidOperationException(
                    $"Structured tool data exceeds the maximum of {MaxCollectionItems} collection items.");
            }

            if (item is null)
            {
                throw new InvalidOperationException("Structured string collections cannot contain null entries.");
            }

            budget.VisitNode(depth + 1);
            snapshot.Add(budget.SnapshotString(
                item,
                canonicalIdsBySourceId,
                "structured string"));
        }

        return snapshot.AsReadOnly();
    }

    private static object? SnapshotSerializedJson(
        Action<Stream> write,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget,
        ISet<object> activeReferences)
    {
        var maximumBytes = budget.RemainingBytes;
        if (maximumBytes <= 0)
        {
            throw new InvalidOperationException("Structured tool data exhausted the global byte budget.");
        }

        using var stream = new BoundedMemoryStream(maximumBytes);
        write(stream);
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxStructuredDepth
        });
        return SnapshotJsonElement(
            document.RootElement,
            canonicalIdsBySourceId,
            depth,
            budget,
            activeReferences,
            visitCurrentNode: false);
    }

    private static object? SnapshotJsonElement(
        JsonElement element,
        IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
        int depth,
        SnapshotBudget budget,
        ISet<object> activeReferences,
        bool visitCurrentNode = true)
    {
        if (visitCurrentNode)
        {
            budget.VisitNode(depth);
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = element.EnumerateObject().ToArray();
                    if (properties.Length > MaxCollectionItems)
                    {
                        throw new InvalidOperationException(
                            $"Structured tool data exceeds the maximum of {MaxCollectionItems} entries.");
                    }

                    var duplicate = properties
                        .GroupBy(property => property.Name, StringComparer.Ordinal)
                        .FirstOrDefault(group => group.Count() > 1);
                    if (duplicate is not null)
                    {
                        throw new InvalidOperationException("Structured JSON tool data contains duplicate properties.");
                    }

                    var snapshot = new Dictionary<string, object?>(properties.Length, StringComparer.Ordinal);
                    foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        var key = budget.SnapshotString(property.Name, "structured-data key");
                        snapshot.Add(
                            key,
                            SnapshotJsonElement(
                                property.Value,
                                canonicalIdsBySourceId,
                                depth + 1,
                                budget,
                                activeReferences));
                    }

                    return new ReadOnlyDictionary<string, object?>(snapshot);
                }
            case JsonValueKind.Array:
                {
                    var snapshot = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                    {
                        if (snapshot.Count >= MaxCollectionItems)
                        {
                            throw new InvalidOperationException(
                                $"Structured tool data exceeds the maximum of {MaxCollectionItems} collection items.");
                        }

                        snapshot.Add(SnapshotJsonElement(
                            item,
                            canonicalIdsBySourceId,
                            depth + 1,
                            budget,
                            activeReferences));
                    }

                    return snapshot.AsReadOnly();
                }
            case JsonValueKind.String:
                return budget.SnapshotString(
                    element.GetString() ?? string.Empty,
                    canonicalIdsBySourceId,
                    "structured JSON string");
            case JsonValueKind.Number:
                {
                    var raw = element.GetRawText();
                    budget.ConsumeStringBytes(raw, "structured JSON number");
                    if (element.TryGetInt64(out var signedInteger))
                    {
                        return signedInteger;
                    }

                    if (element.TryGetUInt64(out var unsignedInteger))
                    {
                        return unsignedInteger;
                    }

                    if (element.TryGetDecimal(out var decimalNumber))
                    {
                        return decimalNumber;
                    }

                    if (element.TryGetDouble(out var floatingPointNumber) && double.IsFinite(floatingPointNumber))
                    {
                        return floatingPointNumber;
                    }

                    throw new InvalidOperationException("Structured JSON tool data contains an unsupported number.");
                }
            case JsonValueKind.True:
                budget.ConsumeScalarBytes(4, "Boolean value");
                return true;
            case JsonValueKind.False:
                budget.ConsumeScalarBytes(5, "Boolean value");
                return false;
            case JsonValueKind.Null:
                budget.ConsumeScalarBytes(4, "null value");
                return null;
            default:
                throw new InvalidOperationException("Structured JSON tool data contains an undefined value.");
        }
    }

    private static bool TrySnapshotScalar(object value, SnapshotBudget budget, out object? snapshot)
    {
        switch (value)
        {
            case bool boolean:
                budget.ConsumeScalarBytes(boolean ? 4 : 5, "Boolean value");
                snapshot = boolean;
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                budget.ConsumeStringBytes(
                    Convert.ToString(value, CultureInfo.InvariantCulture)!,
                    "integer value");
                snapshot = value;
                return true;
            case float single when float.IsFinite(single):
                budget.ConsumeStringBytes(single.ToString("R", CultureInfo.InvariantCulture), "number value");
                snapshot = single;
                return true;
            case double number when double.IsFinite(number):
                budget.ConsumeStringBytes(number.ToString("R", CultureInfo.InvariantCulture), "number value");
                snapshot = number;
                return true;
            case decimal number:
                budget.ConsumeStringBytes(number.ToString(CultureInfo.InvariantCulture), "number value");
                snapshot = number;
                return true;
            case DateTime dateTime:
                snapshot = budget.SnapshotString(
                    dateTime.ToString("O", CultureInfo.InvariantCulture),
                    "date-time value");
                return true;
            case DateTimeOffset dateTimeOffset:
                snapshot = budget.SnapshotString(
                    dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                    "date-time value");
                return true;
            case TimeSpan timeSpan:
                snapshot = budget.SnapshotString(
                    timeSpan.ToString("c", CultureInfo.InvariantCulture),
                    "time-span value");
                return true;
            case DateOnly date:
                snapshot = budget.SnapshotString(date.ToString("O", CultureInfo.InvariantCulture), "date value");
                return true;
            case TimeOnly time:
                snapshot = budget.SnapshotString(time.ToString("O", CultureInfo.InvariantCulture), "time value");
                return true;
            case Guid guid:
                snapshot = budget.SnapshotString(guid.ToString("D"), "guid value");
                return true;
            case Uri uri:
                snapshot = budget.SnapshotString(uri.OriginalString, "URI value");
                return true;
            case Enum enumeration:
                snapshot = budget.SnapshotString(enumeration.ToString(), "enum value");
                return true;
            default:
                snapshot = null;
                return false;
        }
    }

    private static T SnapshotReference<T>(
        object value,
        ISet<object> activeReferences,
        Func<T> snapshot)
    {
        if (!activeReferences.Add(value))
        {
            throw new InvalidOperationException("Structured tool data contains a reference cycle.");
        }

        try
        {
            return snapshot();
        }
        finally
        {
            activeReferences.Remove(value);
        }
    }

    private sealed class SnapshotBudget
    {
        private int _remainingNodes = MaxStructuredNodes;
        private int _remainingBytes = MaxTotalSnapshotBytes;

        public int RemainingBytes => _remainingBytes;

        public void VisitNode(int depth)
        {
            if (depth > MaxStructuredDepth)
            {
                throw new InvalidOperationException(
                    $"Structured tool data exceeds the maximum depth of {MaxStructuredDepth}.");
            }

            if (_remainingNodes <= 0)
            {
                throw new InvalidOperationException(
                    $"Structured tool data exceeds the global maximum of {MaxStructuredNodes} nodes.");
            }

            _remainingNodes--;
        }

        public string SnapshotString(string value, string description)
        {
            ConsumeStringBytes(value, description);
            return value;
        }

        public string SnapshotString(
            string value,
            IReadOnlyDictionary<string, string> canonicalIdsBySourceId,
            string description)
        {
            var canonical = canonicalIdsBySourceId.TryGetValue(value, out var canonicalId)
                ? canonicalId
                : value;
            ConsumeStringBytes(canonical, description);
            return canonical;
        }

        public string SnapshotBinary(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > MaxBinaryBytes)
            {
                throw new InvalidOperationException(
                    $"Structured tool binary data exceeds the maximum of {MaxBinaryBytes} bytes.");
            }

            var encoded = BinaryBase64Prefix + Convert.ToBase64String(bytes);
            ConsumeScalarBytes(Encoding.UTF8.GetByteCount(encoded), "binary value");
            return encoded;
        }

        public void ConsumeStringBytes(string value, string description)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > MaxStringBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-result {description} exceeds the maximum of {MaxStringBytes} UTF-8 bytes.");
            }

            ConsumeScalarBytes(byteCount, description);
        }

        public void ConsumeScalarBytes(int byteCount, string description)
        {
            if (byteCount < 0 || byteCount > _remainingBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-result {description} exceeds the global maximum of {MaxTotalSnapshotBytes} bytes.");
            }

            _remainingBytes -= byteCount;
        }
    }

    private sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count < 0 || Position > maximumBytes - count)
            {
                throw new InvalidOperationException(
                    $"Serialized tool data exceeds the global maximum of {MaxTotalSnapshotBytes} bytes.");
            }
        }
    }
}

internal sealed record ToolResultNormalization(
    ToolResult Result,
    ExecutionDiagnostics? Diagnostics,
    IReadOnlyDictionary<string, string> IdentityAliases);

using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using Agentica.Observations;

namespace Agentica.Events;

/// <summary>
/// Creates bounded, deeply immutable execution-event snapshots. Event payloads are
/// intentionally a small JSON-like value graph; opaque objects are converted to a
/// validated <see cref="JsonElement"/> rather than retained by reference.
/// </summary>
internal static class ExecutionEventSnapshot
{
    internal const int MaxStructuredDepth = 32;
    internal const int MaxCollectionItems = 16_384;
    internal const int MaxStringLength = 65_536;
    internal const int MaxTotalUtf8Bytes = 1_048_576;
    private const int MaxFailureStringLength = 1_024;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        MaxDepth = MaxStructuredDepth
    };

    private static readonly JsonSerializerOptions EventSizeJsonOptions = new()
    {
        MaxDepth = MaxStructuredDepth + 8
    };

    public static IReadOnlyDictionary<string, string> Data(
        IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > MaxCollectionItems)
        {
            throw Limit("Event data", $"more than {MaxCollectionItems} entries");
        }

        var snapshot = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source)
        {
            snapshot.Add(
                BoundedText(pair.Key, "Event data key"),
                BoundedText(pair.Value, "Event data value"));
        }

        return ReadOnly(snapshot);
    }

    public static IReadOnlyDictionary<string, object?> Payload(
        IReadOnlyDictionary<string, object?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var budget = new SnapshotBudget();
        return SnapshotDictionary(source, budget, depth: 0);
    }

    public static ExecutionEventContext? Context(ExecutionEventContext? source) =>
        source is null
            ? null
            : new ExecutionEventContext(
                RunId: BoundedOptionalText(source.RunId, "Event context run id"),
                AttemptNumber: source.AttemptNumber,
                PlanId: BoundedOptionalText(source.PlanId, "Event context plan id"),
                PlanVersion: source.PlanVersion,
                StepId: BoundedOptionalText(source.StepId, "Event context step id"),
                BatchId: BoundedOptionalText(source.BatchId, "Event context batch id"),
                ToolId: BoundedOptionalText(source.ToolId, "Event context tool id"),
                ReceiptId: BoundedOptionalText(source.ReceiptId, "Event context receipt id"),
                ObservationId: BoundedOptionalText(source.ObservationId, "Event context observation id"),
                ArtifactId: BoundedOptionalText(source.ArtifactId, "Event context artifact id"),
                ToolSurfaceId: BoundedOptionalText(source.ToolSurfaceId, "Event context tool-surface id"),
                FromPlanId: BoundedOptionalText(source.FromPlanId, "Event context source plan id"),
                ToPlanId: BoundedOptionalText(source.ToPlanId, "Event context target plan id"));

    public static ExecutionIntent? Intent(ExecutionIntent? source) =>
        source is null
            ? null
            : new ExecutionIntent(
                BoundedText(source.Action, "Event intent action"),
                BoundedText(source.Rationale, "Event intent rationale"),
                BoundedOptionalText(source.ExpectedOutcome, "Event intent expected outcome"));

    public static ExecutionDiagnostics? Diagnostics(ExecutionDiagnostics? source) =>
        source is null
            ? null
            : new ExecutionDiagnostics(
                BoundedOptionalText(source.Code, "Event diagnostic code"),
                BoundedOptionalText(source.Message, "Event diagnostic message"),
                BoundedOptionalText(source.ErrorClass, "Event diagnostic error class"),
                BoundedOptionalText(source.FailureKind, "Event diagnostic failure kind"));

    public static UserFacingReason? Reason(UserFacingReason? source)
    {
        if (source is null)
        {
            return null;
        }

        var tags = SnapshotStrings(source.Tags, "User-facing reason tags");
        return new UserFacingReason(
            BoundedText(source.Summary, "User-facing reason summary"),
            BoundedOptionalText(source.Detail, "User-facing reason detail"),
            BoundedOptionalText(source.Status, "User-facing reason status"))
        {
            Tags = tags,
            ProjectionSource = BoundedText(source.ProjectionSource, "User-facing reason projection source")
        };
    }

    public static IReadOnlyList<EvidenceRef> Evidence(IReadOnlyList<EvidenceRef> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > MaxCollectionItems)
        {
            throw Limit("Event evidence", $"more than {MaxCollectionItems} entries");
        }

        var snapshot = new List<EvidenceRef>(source.Count);
        foreach (var evidence in source)
        {
            if (evidence is null)
            {
                throw new ExecutionEventSnapshotException("Event evidence contains a null reference.");
            }

            snapshot.Add(new EvidenceRef(
                BoundedText(evidence.Kind, "Event evidence kind"),
                BoundedText(evidence.RefId, "Event evidence id")));
        }

        return snapshot.AsReadOnly();
    }

    public static ExecutionEvent Create(
        string eventId,
        string type,
        DateTimeOffset at,
        long sequence,
        string? source,
        ExecutionEventContext? context,
        ExecutionIntent? intent,
        UserFacingReason? reason,
        IReadOnlyDictionary<string, string> data,
        IReadOnlyList<EvidenceRef> evidence,
        IReadOnlyDictionary<string, object?> payload,
        ExecutionDiagnostics? diagnostics)
    {
        var snapshot = new ExecutionEvent(
            EventId: BoundedText(eventId, "Event id"),
            Type: BoundedText(type, "Event type"),
            At: at,
            Data: Data(data))
        {
            Sequence = sequence,
            Source = BoundedOptionalText(source, "Event source"),
            Context = Context(context),
            Intent = Intent(intent),
            UserFacingReason = Reason(reason),
            EvidenceRefs = Evidence(evidence),
            Payload = Payload(payload),
            Diagnostics = Diagnostics(diagnostics)
        };

        EnsureTotalSize(snapshot);
        return snapshot;
    }

    public static ExecutionEvent Clone(ExecutionEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Create(
            source.EventId,
            source.Type,
            source.At,
            source.Sequence ?? 0,
            source.Source,
            source.Context,
            source.Intent,
            source.UserFacingReason,
            source.Data,
            source.EvidenceRefs,
            source.Payload,
            source.Diagnostics);
    }

    public static ExecutionEvent CreateFailure(
        string eventId,
        string type,
        DateTimeOffset at,
        long sequence,
        string? source,
        ExecutionEventContext? context,
        IReadOnlyList<EvidenceRef>? evidence,
        ExecutionDiagnostics? originalDiagnostics,
        Exception exception)
    {
        var safeData = ReadOnly(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["snapshot"] = "failed"
        });
        var safePayload = ReadOnly(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["snapshotStatus"] = "failed",
            ["failureKind"] = "EventSnapshotFailure",
            ["errorClass"] = SafeText(exception.GetType().Name),
            ["originalDiagnosticCode"] = SafeText(originalDiagnostics?.Code)
        });

        return new ExecutionEvent(
            EventId: SafeText(eventId) ?? "event_snapshot_failure",
            Type: SafeText(type) ?? "event.snapshot.failed",
            At: at,
            Data: safeData)
        {
            Sequence = sequence,
            Source = SafeText(source),
            Context = SafeContext(context),
            Intent = null,
            UserFacingReason = null,
            EvidenceRefs = SafeEvidence(evidence),
            Payload = safePayload,
            Diagnostics = new ExecutionDiagnostics(
                Code: "event.snapshot.failed",
                Message: "Event content exceeded the immutable snapshot contract; a bounded diagnostic event was retained.",
                ErrorClass: SafeText(exception.GetType().Name),
                FailureKind: "EventSnapshotFailure")
        };
    }

    private static IReadOnlyDictionary<string, object?> SnapshotDictionary(
        IReadOnlyDictionary<string, object?> source,
        SnapshotBudget budget,
        int depth)
    {
        EnsureDepth(depth);
        if (source.Count > MaxCollectionItems)
        {
            throw Limit("Event payload dictionary", $"more than {MaxCollectionItems} entries");
        }

        var snapshot = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (var pair in source)
        {
            snapshot.Add(
                BoundedText(pair.Key, "Event payload key"),
                SnapshotValue(pair.Value, budget, depth + 1));
        }

        return ReadOnly(snapshot);
    }

    private static object? SnapshotValue(object? value, SnapshotBudget budget, int depth)
    {
        budget.Consume();
        EnsureDepth(depth);

        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return BoundedText(text, "Event payload string");
        }

        if (IsKnownImmutable(value))
        {
            return value;
        }

        if (value is JsonElement element)
        {
            ValidateJson(element, budget, depth);
            return element.Clone();
        }

        if (value is JsonDocument document)
        {
            ValidateJson(document.RootElement, budget, depth);
            return document.RootElement.Clone();
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return SnapshotDictionary(readOnlyDictionary, budget, depth);
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return SnapshotDictionary(
                (IReadOnlyDictionary<string, object?>)new ReadOnlyDictionary<string, object?>(dictionary),
                budget,
                depth);
        }

        if (value is IDictionary nonGenericDictionary)
        {
            return SnapshotDictionary(nonGenericDictionary, budget, depth);
        }

        if (value is byte[] bytes)
        {
            if (bytes.Length > MaxCollectionItems)
            {
                throw Limit("Event byte payload", $"more than {MaxCollectionItems} bytes");
            }

            budget.Consume(bytes.Length);
            return new ReadOnlyCollection<byte>(bytes.ToArray());
        }

        if (value is IEnumerable<string> strings)
        {
            return SnapshotStrings(strings, "Event payload string collection", budget);
        }

        if (value is IEnumerable sequence)
        {
            return SnapshotSequence(sequence, budget, depth);
        }

        try
        {
            var serializedElement = JsonSerializer.SerializeToElement(value, value.GetType(), SnapshotJsonOptions);
            ValidateJson(serializedElement, budget, depth);
            return serializedElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ExecutionEventSnapshotException(
                $"Event payload type '{value.GetType().Name}' could not be safely snapshotted.",
                exception);
        }
    }

    private static IReadOnlyDictionary<string, object?> SnapshotDictionary(
        IDictionary source,
        SnapshotBudget budget,
        int depth)
    {
        EnsureDepth(depth);
        if (source.Count > MaxCollectionItems)
        {
            throw Limit("Event payload dictionary", $"more than {MaxCollectionItems} entries");
        }

        var snapshot = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is not string key)
            {
                throw new ExecutionEventSnapshotException("Event payload dictionary keys must be strings.");
            }

            snapshot.Add(
                BoundedText(key, "Event payload key"),
                SnapshotValue(entry.Value, budget, depth + 1));
        }

        return ReadOnly(snapshot);
    }

    private static IReadOnlyList<object?> SnapshotSequence(
        IEnumerable source,
        SnapshotBudget budget,
        int depth)
    {
        EnsureDepth(depth);
        var snapshot = new List<object?>();
        foreach (var item in source)
        {
            if (snapshot.Count >= MaxCollectionItems)
            {
                throw Limit("Event payload collection", $"more than {MaxCollectionItems} items");
            }

            snapshot.Add(SnapshotValue(item, budget, depth + 1));
        }

        return snapshot.AsReadOnly();
    }

    private static IReadOnlyList<string> SnapshotStrings(
        IEnumerable<string> source,
        string field,
        SnapshotBudget? budget = null)
    {
        var snapshot = new List<string>();
        foreach (var value in source)
        {
            if (snapshot.Count >= MaxCollectionItems)
            {
                throw Limit(field, $"more than {MaxCollectionItems} items");
            }

            budget?.Consume();
            snapshot.Add(BoundedText(value, field));
        }

        return snapshot.AsReadOnly();
    }

    private static void ValidateJson(
        JsonElement element,
        SnapshotBudget budget,
        int depth)
    {
        EnsureDepth(depth);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var propertyCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyCount++ >= MaxCollectionItems)
                    {
                        throw Limit("Event JSON object", $"more than {MaxCollectionItems} properties");
                    }

                    _ = BoundedText(property.Name, "Event JSON property name");
                    budget.Consume();
                    ValidateJson(property.Value, budget, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                var itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount++ >= MaxCollectionItems)
                    {
                        throw Limit("Event JSON array", $"more than {MaxCollectionItems} items");
                    }

                    budget.Consume();
                    ValidateJson(item, budget, depth + 1);
                }

                break;

            case JsonValueKind.String:
                _ = BoundedOptionalText(element.GetString(), "Event JSON string");
                break;

            case JsonValueKind.Number:
                _ = BoundedText(element.GetRawText(), "Event JSON number");
                break;
        }
    }

    private static ExecutionEventContext? SafeContext(ExecutionEventContext? source) =>
        source is null
            ? null
            : new ExecutionEventContext(
                RunId: SafeText(source.RunId),
                AttemptNumber: source.AttemptNumber,
                PlanId: SafeText(source.PlanId),
                PlanVersion: source.PlanVersion,
                StepId: SafeText(source.StepId),
                BatchId: SafeText(source.BatchId),
                ToolId: SafeText(source.ToolId),
                ReceiptId: SafeText(source.ReceiptId),
                ObservationId: SafeText(source.ObservationId),
                ArtifactId: SafeText(source.ArtifactId),
                ToolSurfaceId: SafeText(source.ToolSurfaceId),
                FromPlanId: SafeText(source.FromPlanId),
                ToPlanId: SafeText(source.ToPlanId));

    private static IReadOnlyList<EvidenceRef> SafeEvidence(IReadOnlyList<EvidenceRef>? source)
    {
        if (source is null)
        {
            return new List<EvidenceRef>().AsReadOnly();
        }

        try
        {
            var count = Math.Min(source.Count, 16);
            var evidence = new List<EvidenceRef>(count);
            for (var index = 0; index < count; index++)
            {
                var item = source[index];
                if (item is not null)
                {
                    evidence.Add(new EvidenceRef(
                        SafeText(item.Kind) ?? "unknown",
                        SafeText(item.RefId) ?? "unknown"));
                }
            }

            return evidence.AsReadOnly();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new List<EvidenceRef>().AsReadOnly();
        }
    }

    private static string BoundedText(string? value, string field)
    {
        if (value is null)
        {
            throw new ExecutionEventSnapshotException($"{field} cannot be null.");
        }

        if (value.Length > MaxStringLength)
        {
            throw Limit(field, $"more than {MaxStringLength} characters");
        }

        return value;
    }

    private static string? BoundedOptionalText(string? value, string field) =>
        value is null ? null : BoundedText(value, field);

    private static string? SafeText(string? value) =>
        value is null || value.Length <= MaxFailureStringLength
            ? value
            : value[..MaxFailureStringLength];

    private static void EnsureDepth(int depth)
    {
        if (depth > MaxStructuredDepth)
        {
            throw Limit("Event payload", $"a depth greater than {MaxStructuredDepth}");
        }
    }

    private static void EnsureTotalSize(ExecutionEvent executionEvent)
    {
        using var stream = new BoundedWriteStream(MaxTotalUtf8Bytes);
        try
        {
            JsonSerializer.Serialize(stream, executionEvent, EventSizeJsonOptions);
        }
        catch (ExecutionEventSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ExecutionEventSnapshotException(
                "The complete event could not be measured against the immutable snapshot budget.",
                exception);
        }
    }

    private static ExecutionEventSnapshotException Limit(string field, string limit) =>
        new($"{field} contains {limit}.");

    private static bool IsKnownImmutable(object value) =>
        value is char or bool or
            byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal or
            DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly or
            Guid or Uri or Version or Enum;

    private static ReadOnlyDictionary<string, TValue> ReadOnly<TValue>(
        Dictionary<string, TValue> dictionary) =>
        new(dictionary);

    private sealed class SnapshotBudget
    {
        private int _remaining = MaxCollectionItems;

        public void Consume(int count = 1)
        {
            if (count < 0 || _remaining < count)
            {
                throw Limit("Event payload", $"more than {MaxCollectionItems} structured values");
            }

            _remaining -= count;
        }
    }

    private sealed class BoundedWriteStream(int maximumBytes) : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Add(count);

        public override void Write(ReadOnlySpan<byte> buffer) =>
            Add(buffer.Length);

        private void Add(int count)
        {
            if (count < 0 || _length > maximumBytes - count)
            {
                throw Limit(
                    "Complete event snapshot",
                    $"more than {maximumBytes} UTF-8 bytes");
            }

            _length += count;
        }
    }
}

internal sealed class ExecutionEventSnapshotException : InvalidOperationException
{
    public ExecutionEventSnapshotException(string message)
        : base(message)
    {
    }

    public ExecutionEventSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

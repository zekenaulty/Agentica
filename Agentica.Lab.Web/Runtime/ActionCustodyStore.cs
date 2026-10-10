using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Lab.Web.Contracts;
using Agentica.Tools;

namespace Agentica.Lab.Web.Runtime;

public sealed record ActionCustodyEntry(
    string HostId,
    string SessionId,
    string SessionEpoch,
    string RunId,
    string ObjectiveId,
    HostActionRequest Request,
    HostCapability Capability,
    string RequestHash,
    string CapabilityHash,
    DateTimeOffset ReservedAt,
    DateTimeOffset UpdatedAt,
    HostActionResult? Result = null,
    string? ResultHash = null)
{
    [JsonIgnore]
    public bool IsUnresolved => Result is null || Result.Disposition == "unresolved";
}

/// <summary>
/// Durable custody of requests that may have reached a host. A reservation must commit before sending.
/// This store does not dispatch, retry, claim completion, or reconstruct a running provider call.
/// Use one instance in one service process per storage directory.
/// </summary>
public sealed class ActionCustodyStore
{
    public const int MaximumPendingActions = 128;
    public const int MaximumCompletedPerSession = 256;
    public const int MaximumEntries = 1_024;
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const string LedgerFileName = "action-custody-v1.json";
    private const string MarkerFileName = "action-custody-v1.initialized";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _markerPath;

    public ActionCustodyStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var directory = Path.GetFullPath(rootDirectory);
        _path = Path.Combine(directory, LedgerFileName);
        _markerPath = Path.Combine(directory, MarkerFileName);
        Directory.CreateDirectory(directory);
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                if (File.Exists(_markerPath)) throw new InvalidDataException("Action custody ledger is missing after initialization; admission is fenced.");
                Write(new LedgerPayload(Guid.NewGuid().ToString("N"), [], 0));
            }
            var ledger = Read(requireMarker: false);
            if (File.Exists(_markerPath)) ValidateMarker(ledger.StoreId);
            else
            {
                if (ledger.Entries.Count > 0 || ledger.PrunedCompletedCount > 0)
                    throw new InvalidDataException("Initialized action custody lost its marker; admission is fenced.");
                using var stream = new FileStream(_markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    writer.Write(ledger.StoreId);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
        }
    }

    public long PrunedCompletedCount { get { lock (_gate) return Read().PrunedCompletedCount; } }

    public ActionCustodyEntry Reserve(string hostId, string sessionId, string sessionEpoch, string runId,
        HostActionRequest request, HostCapability capability, string objectiveId)
    {
        ValidateOriginal(hostId, sessionId, sessionEpoch, runId, request, capability, objectiveId);
        lock (_gate)
        {
            var ledger = Read();
            var existing = ledger.Entries.FirstOrDefault(entry => entry.HostId == hostId && entry.SessionId == sessionId && entry.Request.ActionId == request.ActionId);
            var requestHash = ProtocolValidation.Digest(request);
            var capabilityHash = ProtocolValidation.Digest(capability);
            if (existing is not null)
            {
                if (existing.SessionEpoch != sessionEpoch || existing.RunId != runId || existing.ObjectiveId != objectiveId ||
                    existing.RequestHash != requestHash || existing.CapabilityHash != capabilityHash)
                    Fail("custody.identity_changed", "An original action identity cannot be reserved with changed input or binding.");
                return Copy(existing);
            }
            if (ledger.Entries.Any(entry => entry.HostId == hostId && entry.SessionId == sessionId && entry.IsUnresolved))
                Fail("custody.session_unresolved", "Reconcile the original outstanding action before new dispatch, including after an epoch change.");
            if (ledger.Entries.Count(entry => entry.IsUnresolved) >= MaximumPendingActions)
                Fail("custody.pending_limit", "Pending action custody reached its bound; reconcile original actions before new dispatch.");
            var now = DateTimeOffset.UtcNow;
            var reservation = new ActionCustodyEntry(hostId, sessionId, sessionEpoch, runId, objectiveId,
                Copy(request), Copy(capability), requestHash, capabilityHash, now, now);
            Commit(ledger, [.. ledger.Entries, reservation]);
            return Copy(reservation);
        }
    }

    /// <summary>Returns false for exact replay. An unresolved result is retained but never clears the session fence.</summary>
    public bool Resolve(string hostId, string sessionId, string actionId, HostActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var ledger = Read();
            var entry = ValidateAgainstLedger(ledger, hostId, sessionId, actionId, result);
            var hash = ProtocolValidation.Digest(result);
            if (entry.ResultHash == hash) return false;
            var updated = entry with { Result = Copy(result), ResultHash = hash, UpdatedAt = DateTimeOffset.UtcNow };
            var entries = ledger.Entries.Where(other => other != entry).Append(updated).ToList();
            Commit(ledger, entries);
            return true;
        }
    }

    /// <summary>Check durable identity, terminal replay and evidence uniqueness before another store is updated.
    /// Resolve performs the same checks again when committing; prevalidation itself never settles custody.</summary>
    public ActionCustodyEntry PrevalidateResolution(string hostId, string sessionId, string actionId, HostActionResult result, long? minimumRevision = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate) return Copy(ValidateAgainstLedger(Read(), hostId, sessionId, actionId, result, minimumRevision));
    }

    public ActionCustodyEntry Get(string hostId, string sessionId, string actionId)
    {
        lock (_gate) return Copy(Find(Read(), hostId, sessionId, actionId));
    }

    public bool HasUnresolved(string hostId, string sessionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        lock (_gate) return Read().Entries.Any(entry => entry.HostId == hostId && entry.SessionId == sessionId && entry.IsUnresolved);
    }

    public IReadOnlyList<ActionCustodyEntry> ListUnresolved()
    {
        lock (_gate) return Read().Entries.Where(entry => entry.IsUnresolved).Select(Copy).ToArray();
    }

    public IReadOnlyList<ActionCustodyEntry> ListUnresolved(string hostId, string sessionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        lock (_gate) return Read().Entries.Where(entry => entry.HostId == hostId && entry.SessionId == sessionId && entry.IsUnresolved).Select(Copy).ToArray();
    }

    public IReadOnlyList<ActionCustodyEntry> ListUnresolvedForRun(string runId)
    {
        ProtocolValidation.Identifier(runId);
        lock (_gate) return Read().Entries.Where(entry => entry.RunId == runId && entry.IsUnresolved).Select(Copy).ToArray();
    }

    public static void ValidateResolution(ActionCustodyEntry entry, HostActionResult result, long? minimumRevision = null)
    {
        if (result.Operation is { } operation)
        {
            if (!entry.Capability.DurableHandoff || result.Disposition != "applied" || result.Observation is null || result.Completion is not null)
                Fail("operation.admission", "Only a bound durable capability may admit incomplete host work with a fresh observation.");
            ProtocolValidation.Identifier(operation.OperationId);
            ProtocolValidation.Text(operation.Summary, 4000, "operation summary");
            if (operation.Usage is { } usage && (usage.ValueKind != JsonValueKind.Object ||
                JsonSerializer.SerializeToUtf8Bytes(usage, HostProtocol.Json).Length > 8192))
                Fail("operation.usage", "Host operation accounting must be a bounded object.");
        }
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(result);
        if (result.ActionId != entry.Request.ActionId || result.SessionId != entry.SessionId || result.SessionEpoch != entry.SessionEpoch)
            Fail("custody.result_binding", "Resolution must refer to the exact original action and session epoch.");
        if (result.Disposition is not ("applied" or "refused" or "conflict" or "unavailable" or "unresolved"))
            Fail("action.disposition", "Unknown action disposition.");
        ProtocolValidation.Identifier(result.EvidenceId);
        ProtocolValidation.Text(result.Summary, 4000, "result summary");
        if (result.BeforeRevision < 0 || result.AfterRevision < result.BeforeRevision ||
            (minimumRevision is not null && result.AfterRevision < minimumRevision.Value))
            Fail("action.revision", "Invalid result revision ordering.");
        if (result.Disposition is "applied" or "refused" && result.BeforeRevision != entry.Request.ExpectedRevision)
            Fail("action.revision", "A stale action must be reported as a conflict.");
        if ((entry.Capability.Effect == ToolEffect.ReadOnly || result.Disposition is "refused" or "conflict" or "unavailable") &&
            result.BeforeRevision != result.AfterRevision)
            Fail("action.no_mutation", "This capability or disposition cannot report a host mutation.");
        if (result.Disposition is "applied" or "refused" or "conflict" && result.Observation is null)
            Fail("action.observation", "A resolved host action requires a fresh observation.");
        if (result.Observation is not null)
        {
            ProtocolValidation.Observation(result.Observation);
            if (result.Observation.Revision != result.AfterRevision)
                Fail("action.observation_revision", "Observation revision does not match the original action result.");
        }
        if (result.Completion is not null)
        {
            if (result.Disposition != "applied" || result.Observation is null || result.Completion.ObjectiveId != entry.ObjectiveId)
                Fail("completion.binding", "Completion must bind an applied original result to its objective.");
            ProtocolValidation.Identifier(result.Completion.EvidenceId);
            ProtocolValidation.Text(result.Completion.Summary, 4000, "completion summary");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(result, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
            Fail("custody.result_bounds", "Action result exceeds the custody size bound.");
    }

    private static void ValidateOriginal(string hostId, string sessionId, string sessionEpoch, string runId,
        HostActionRequest request, HostCapability capability, string objectiveId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        foreach (var identifier in new[] { hostId, sessionId, sessionEpoch, runId, objectiveId, request.ActionId,
            request.RunnerRunId, request.StepId, request.CapabilityId }) ProtocolValidation.Identifier(identifier);
        ProtocolValidation.Text(request.ManifestHash, 256, "manifest hash");
        if (request.SessionId != sessionId || request.SessionEpoch != sessionEpoch || request.RunId != runId || capability.Id != request.CapabilityId)
            Fail("custody.request_binding", "Custody must retain the exact session, run and capability binding.");
        if (request.ExpectedRevision < 0 || request.DeadlineAt == default || request.Arguments.ValueKind != JsonValueKind.Object)
            Fail("custody.request_invalid", "A bounded original action with revision, deadline and object arguments is required.");
        if (!Enum.IsDefined(capability.Kind) || !Enum.IsDefined(capability.Effect) || capability.Effect == ToolEffect.Unknown || capability.InputSchema?.Fields is null)
            Fail("custody.capability", "A bound capability with known kind, effect and input schema is required.");
        if (JsonSerializer.SerializeToUtf8Bytes(request, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes ||
            JsonSerializer.SerializeToUtf8Bytes(capability, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
            Fail("custody.request_bounds", "Original action or capability exceeds the custody size bound.");
    }

    private static ActionCustodyEntry Find(LedgerPayload ledger, string hostId, string sessionId, string actionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        ProtocolValidation.Identifier(actionId);
        return ledger.Entries.FirstOrDefault(entry => entry.HostId == hostId && entry.SessionId == sessionId && entry.Request.ActionId == actionId)
            ?? throw new HostProtocolException("custody.not_found", "Original action is not retained. Do not reconstruct or resubmit an uncertain action.");
    }

    private static ActionCustodyEntry ValidateAgainstLedger(LedgerPayload ledger, string hostId, string sessionId, string actionId,
        HostActionResult result, long? minimumRevision = null)
    {
        var entry = Find(ledger, hostId, sessionId, actionId);
        ValidateResolution(entry, result, minimumRevision);
        if (!entry.IsUnresolved && entry.ResultHash != ProtocolValidation.Digest(result))
            Fail("custody.result_changed", "A terminal original action result cannot be replaced.");
        if (ledger.Entries.Any(other => other.HostId == hostId && other.SessionId == sessionId && other.Request.ActionId != actionId &&
            other.Result?.EvidenceId == result.EvidenceId))
            Fail("custody.evidence_reused", "Evidence identity already belongs to another original action in this session.");
        return entry;
    }

    private LedgerPayload Read(bool requireMarker = true)
    {
        if (!File.Exists(_path)) throw new InvalidDataException("Action custody ledger is unavailable; admission is fenced.");
        if (new FileInfo(_path).Length > MaximumFileBytes) throw new InvalidDataException("Action custody ledger exceeds its size bound.");
        LedgerFile file;
        try
        {
            file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(_path), HostProtocol.Json)
                ?? throw new InvalidDataException("Action custody ledger is empty.");
            if (file.Version != 1 || file.Payload is null || file.Payload.Entries is null ||
                !Guid.TryParseExact(file.Payload.StoreId, "N", out _) || file.Payload.PrunedCompletedCount < 0 ||
                ProtocolValidation.Digest(file.Payload) != file.ContentHash)
                throw new InvalidDataException("Action custody ledger version, identity or hash is invalid.");
            if (file.Payload.Entries.Count > MaximumEntries || file.Payload.Entries.Count(entry => entry.IsUnresolved) > MaximumPendingActions ||
                file.Payload.Entries.GroupBy(entry => (entry.HostId, entry.SessionId, entry.Request.ActionId)).Any(group => group.Count() != 1))
                throw new InvalidDataException("Action custody identities or retention bounds are invalid.");
            foreach (var entry in file.Payload.Entries)
            {
                ValidateOriginal(entry.HostId, entry.SessionId, entry.SessionEpoch, entry.RunId, entry.Request, entry.Capability, entry.ObjectiveId);
                if (entry.ReservedAt == default || entry.UpdatedAt == default || entry.RequestHash != ProtocolValidation.Digest(entry.Request) ||
                    entry.CapabilityHash != ProtocolValidation.Digest(entry.Capability) ||
                    (entry.Result is null ? entry.ResultHash is not null : entry.ResultHash != ProtocolValidation.Digest(entry.Result)))
                    throw new InvalidDataException("An original action or result hash is invalid.");
                if (entry.Result is not null) ValidateResolution(entry, entry.Result);
            }
        }
        catch (Exception exception) when (exception is JsonException or HostProtocolException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("Action custody ledger is invalid; admission is fenced.", exception);
        }
        if (requireMarker) ValidateMarker(file.Payload.StoreId);
        return file.Payload;
    }

    private void ValidateMarker(string storeId)
    {
        if (!File.Exists(_markerPath) || new FileInfo(_markerPath).Length > 128 || File.ReadAllText(_markerPath) != storeId)
            throw new InvalidDataException("Action custody initialization marker is unavailable or changed; admission is fenced.");
    }

    private void Commit(LedgerPayload previous, List<ActionCustodyEntry> entries)
    {
        var pruned = previous.PrunedCompletedCount;
        foreach (var group in entries.Where(entry => !entry.IsUnresolved).GroupBy(entry => (entry.HostId, entry.SessionId)).ToArray())
            foreach (var old in group.Take(Math.Max(0, group.Count() - MaximumCompletedPerSession)).ToArray())
            {
                entries.Remove(old);
                pruned++;
            }
        while (entries.Count > MaximumEntries)
        {
            var completed = entries.FirstOrDefault(entry => !entry.IsUnresolved)
                ?? throw new InvalidDataException("Pending action custody cannot be pruned.");
            entries.Remove(completed);
            pruned++;
        }
        var payload = previous with { Entries = entries.ToArray(), PrunedCompletedCount = pruned };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, HostProtocol.Json).Length + 256;
        while (bytes > MaximumFileBytes)
        {
            var completed = entries.FirstOrDefault(entry => !entry.IsUnresolved);
            if (completed is null) Fail("custody.storage_limit", "Unresolved custody reached its storage bound; reconcile original actions before dispatch.");
            bytes -= JsonSerializer.SerializeToUtf8Bytes(completed, HostProtocol.Json).Length + 1;
            entries.Remove(completed!);
            pruned++;
        }
        Write(previous with { Entries = entries.ToArray(), PrunedCompletedCount = pruned });
    }

    private void Write(LedgerPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LedgerFile(1, payload, ProtocolValidation.Digest(payload)), HostProtocol.Json);
        if (bytes.Length > MaximumFileBytes) Fail("custody.storage_limit", "Action custody exceeded its storage bound.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, HostProtocol.Json), HostProtocol.Json)!;
    private static void Fail(string code, string message) => throw new HostProtocolException(code, message);
    private sealed record LedgerPayload(string StoreId, IReadOnlyList<ActionCustodyEntry> Entries, long PrunedCompletedCount);
    private sealed record LedgerFile(int Version, LedgerPayload Payload, string ContentHash);
}

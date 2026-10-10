using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

public sealed record HostOperationEntry(
    HostRunRequest Request,
    HostActionRequest Action,
    HostRunUsage Usage,
    HostActionResult? Admission,
    string ActiveEpoch,
    HostOperationEvent? LatestEvent,
    string? WakeRunId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HostRunUsage? SettledUsage = null);

/// <summary>
/// Durable objective handoffs and semantic event identities. This store never dispatches work,
/// retries an action, schedules a wake, or reconstructs a private provider session.
/// Use one instance in one service process per storage directory.
/// </summary>
public sealed class HostOperationStore
{
    public const string LedgerFileName = "host-operations-v1.json";
    public const int MaximumEntries = 512;
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumUsageBytes = 8 * 1024;
    private const string MarkerFileName = "host-operations-v1.initialized";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _markerPath;

    public HostOperationStore(string rootDirectory)
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
                if (File.Exists(_markerPath)) throw new InvalidDataException("Initialized operation ledger is missing; continuation is fenced.");
                Write(new LedgerPayload(Guid.NewGuid().ToString("N"), []));
            }
            var ledger = Read(requireMarker: false);
            if (File.Exists(_markerPath)) ValidateMarker(ledger.StoreId);
            else
            {
                if (ledger.Entries.Count != 0) throw new InvalidDataException("Initialized operation ledger lost its marker; continuation is fenced.");
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

    /// <summary>Freeze the original bounded request before custody reservation and possible dispatch.</summary>
    public void Reserve(HostRunRequest request, HostActionRequest action, HostRunUsage usage)
    {
        ValidateReservation(request, action, usage);
        lock (_gate)
        {
            var ledger = Read();
            var existing = ledger.Entries.FirstOrDefault(item => SameAction(item.Entry, request.HostId, request.SessionId, action.ActionId));
            var requestHash = ProtocolValidation.Digest(request);
            var actionHash = ProtocolValidation.Digest(action);
            if (existing is not null)
            {
                if (existing.RequestHash != requestHash || existing.ActionHash != actionHash || existing.Entry.Usage != usage)
                    Fail("operation.reservation_changed", "An original operation action cannot be reserved with changed input or usage.");
                return;
            }
            if (ledger.Entries.Count >= MaximumEntries)
                Fail("operation.capacity", "Operation retention is full; retained handoffs and event identities cannot be evicted automatically.");
            var now = DateTimeOffset.UtcNow;
            var entry = new HostOperationEntry(Copy(request), Copy(action), usage, null, request.SessionEpoch,
                null, null, "reserved", now, now);
            Write(ledger with { Entries = [.. ledger.Entries, new StoredOperation(entry, requestHash, actionHash, null, [])] });
        }
    }

    /// <summary>Persist admission before action custody is resolved or admission is acknowledged.</summary>
    public HostOperationEntry Admit(string hostId, string sessionId, HostActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var ledger = Read();
            var stored = Find(ledger, hostId, sessionId, result.ActionId);
            ValidateAdmission(stored.Entry.Request, stored.Entry.Action, result);
            var hash = ProtocolValidation.Digest(result);
            if (stored.Entry.Admission is not null)
            {
                if (stored.AdmissionHash != hash) Fail("operation.admission_changed", "An original operation admission cannot be replaced.");
                // Also repairs a previously interrupted parent settlement when reading a ledger
                // produced before admission and handoff settlement shared one atomic write.
                SettleParentsAndWrite(ledger, stored, stored);
                return Copy(stored.Entry);
            }
            if (ledger.Entries.Any(other => other != stored && other.Entry.Request.HostId == hostId && other.Entry.Request.SessionId == sessionId &&
                other.Entry.Admission?.Operation?.OperationId == result.Operation!.OperationId))
                Fail("operation.identity_reused", "The operation identity already belongs to another original admission in this session.");
            var entry = stored.Entry with { Admission = Copy(result), State = "parked", UpdatedAt = DateTimeOffset.UtcNow };
            SettleParentsAndWrite(ledger, stored, stored with { Entry = entry, AdmissionHash = hash });
            return Copy(entry);
        }
    }

    private void SettleParentsAndWrite(LedgerPayload ledger, StoredOperation original, StoredOperation admitted)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = original != admitted;
        var entries = ledger.Entries.Select(item =>
        {
            if (item == original) return admitted;
            if (item.Entry.State != "waking" || !IsAdmittedChild(item.Entry, admitted.Entry)) return item;
            changed = true;
            // The admitted child is durable proof that this exact claimed activation handed off.
            // Commit both sides together so a crash cannot strand the parent in waking forever.
            return item with
            {
                Entry = Finish(item.Entry, "continued", admitted.Entry.Usage) with { UpdatedAt = now },
                FinishState = "continued",
                FinishUsage = admitted.Entry.Usage
            };
        }).ToArray();
        if (changed) Write(ledger with { Entries = entries });
    }

    private static bool IsAdmittedChild(HostOperationEntry parent, HostOperationEntry child) =>
        child.Admission is not null && parent.WakeRunId == child.Action.RunId &&
        parent.Request.HostId == child.Request.HostId && parent.Request.SessionId == child.Request.SessionId &&
        parent.Request.ObjectiveId == child.Request.ObjectiveId && parent.Request.ScopeId == child.Request.ScopeId &&
        parent.Request.PerspectiveId == child.Request.PerspectiveId && parent.ActiveEpoch == child.Request.SessionEpoch;

    public HostOperationEntry Get(string hostId, string sessionId, string actionId)
    {
        lock (_gate) return Copy(Find(Read(), hostId, sessionId, actionId).Entry);
    }

    public IReadOnlyList<HostOperationEntry> List(string hostId, string sessionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        lock (_gate) return Read().Entries.Where(item => item.Entry.Request.HostId == hostId && item.Entry.Request.SessionId == sessionId)
            .Select(item => Copy(item.Entry)).ToArray();
    }

    public bool HasOpen(string hostId, string sessionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        lock (_gate) return Read().Entries.Any(item => item.Entry.Request.HostId == hostId && item.Entry.Request.SessionId == sessionId &&
            item.Entry.State is "parked" or "waking");
    }

    /// <summary>Settle the claimed activation after its bounded execution ends; the original host signal remains immutable.</summary>
    public HostOperationEntry FinishWake(string hostId, string sessionId, string actionId, string runId, string state, HostRunUsage usage)
    {
        ProtocolValidation.Identifier(runId);
        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate)
        {
            var ledger = Read();
            var stored = Find(ledger, hostId, sessionId, actionId);
            if (stored.Entry.WakeRunId != runId)
                Fail("operation.wake_binding", "Only the claimed successor activation may settle this wake.");
            if (stored.FinishState is not null)
            {
                if (stored.FinishState != state || stored.FinishUsage != usage)
                    Fail("operation.wake_changed", "A settled successor activation cannot receive a different terminal state or cumulative usage.");
                return Copy(stored.Entry);
            }
            var entry = Finish(stored.Entry, state, usage) with { UpdatedAt = DateTimeOffset.UtcNow };
            Replace(ledger, stored, stored with { Entry = entry, FinishState = state, FinishUsage = usage });
            return Copy(entry);
        }
    }

    /// <summary>Persist a semantic event and its successor identity before any possible cognition activation.</summary>
    public HostOperationEntry Apply(HostOperationEvent evt, string? successorRunId = null)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            var ledger = Read();
            var stored = Find(ledger, evt.HostId, evt.SessionId, evt.ActionId);
            var hash = ProtocolValidation.Digest(evt);
            var duplicate = ledger.Entries.SelectMany(item => item.Events.Select(delivery => (item, delivery)))
                .FirstOrDefault(pair => pair.item.Entry.Request.HostId == evt.HostId && pair.item.Entry.Request.SessionId == evt.SessionId &&
                    pair.delivery.Event.EventId == evt.EventId);
            if (duplicate.delivery is not null)
            {
                if (duplicate.item != stored || duplicate.delivery.EventHash != hash ||
                    (successorRunId is not null && duplicate.delivery.SuccessorRunId != successorRunId))
                    Fail("operation.event_changed", "A semantic event identity cannot be reused with changed content or successor.");
                if (stored.Entry.LatestEvent?.EventId != evt.EventId)
                    Fail("operation.event_stale", "An older semantic event cannot become current again.");
                return Copy(stored.Entry);
            }
            ValidateEventHistory(stored.Entry, stored.Events, evt);
            var updated = Transition(stored.Entry, evt, successorRunId) with { UpdatedAt = DateTimeOffset.UtcNow };
            Replace(ledger, stored, stored with
            {
                Entry = updated,
                Events = [.. stored.Events, new StoredEvent(Copy(evt), hash, successorRunId)]
            });
            return Copy(updated);
        }
    }

    public static void ValidateAdmission(HostRunRequest request, HostActionRequest action, HostActionResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Operation is null) Fail("operation.admission_required", "A typed durable operation admission is required.");
        if (result.ActionId != action.ActionId || result.SessionId != request.SessionId || result.SessionEpoch != request.SessionEpoch ||
            action.SessionId != request.SessionId || action.SessionEpoch != request.SessionEpoch ||
            result.Disposition != "applied" || result.Observation is null || result.Completion is not null)
            Fail("operation.admission_binding", "An admission must bind an applied original action and fresh observation without objective completion.");
        var capability = request.Capabilities?.SingleOrDefault(item => item.Id == action.CapabilityId);
        if (capability is null || !capability.DurableHandoff || action.ManifestHash != ProtocolValidation.Digest(request.Capabilities!))
            Fail("operation.capability", "The original action must bind a capability that declares durable handoff.");
        var custody = new ActionCustodyEntry(request.HostId, request.SessionId, request.SessionEpoch, action.RunId,
            request.ObjectiveId, action, capability!, ProtocolValidation.Digest(action), ProtocolValidation.Digest(capability!),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        ActionCustodyStore.ValidateResolution(custody, result, action.ExpectedRevision);
        ProtocolValidation.Identifier(result.Operation!.OperationId);
        ProtocolValidation.Text(result.Operation.Summary, 4000, "operation summary");
        ValidateUsage(result.Operation.Usage);
    }

    private static HostOperationEntry Transition(HostOperationEntry entry, HostOperationEvent evt, string? successorRunId)
    {
        if (entry.Admission?.Operation is null || (entry.State != "parked" && !(entry.State == "waking" && evt.Kind == "cancelled")))
            Fail("operation.not_parked", "Only a parked admitted operation accepts a new semantic event.");
        foreach (var identifier in new[] { evt.HostId, evt.SessionId, evt.SessionEpoch, evt.ActionId, evt.OperationId, evt.EventId })
            ProtocolValidation.Identifier(identifier);
        if (!SameAction(entry, evt.HostId, evt.SessionId, evt.ActionId) || evt.SessionEpoch != entry.ActiveEpoch ||
            evt.OperationId != entry.Admission!.Operation!.OperationId)
            Fail("operation.event_binding", "The event must bind the admitted operation and its active session epoch.");
        var sequence = entry.LatestEvent?.Sequence ?? 0;
        if (sequence == long.MaxValue || evt.Sequence != sequence + 1)
            Fail("operation.sequence", "Semantic event sequence must advance exactly once from the latest committed event.");
        if (evt.Kind is not ("progress" or "transfer" or "decision" or "completed" or "blocked" or "cancelled"))
            Fail("operation.kind", "Unknown semantic event kind.");
        ProtocolValidation.Observation(evt.Observation);
        ProtocolValidation.Text(evt.Summary, 4000, "operation event summary");
        ValidateUsage(evt.Usage);
        var previousObservation = entry.LatestEvent?.Observation ?? entry.Admission.Observation!;
        if (evt.Observation.Revision < previousObservation.Revision ||
            (evt.Observation.ObservationId == previousObservation.ObservationId &&
             ProtocolValidation.Digest(evt.Observation) != ProtocolValidation.Digest(previousObservation)))
            Fail("operation.observation", "A semantic event cannot rewind revision or replace an existing observation identity.");
        if (evt.Kind == "transfer")
        {
            ProtocolValidation.Identifier(evt.NextSessionEpoch!);
            if (evt.NextSessionEpoch == entry.ActiveEpoch)
                Fail("operation.epoch", "Ownership transfer requires a distinct next session epoch.");
        }
        else if (evt.NextSessionEpoch is not null) Fail("operation.epoch", "Only a transfer event may change the active epoch.");
        if (evt.Kind == "completed")
        {
            if (evt.Completion is null || evt.Completion.ObjectiveId != entry.Request.ObjectiveId)
                Fail("operation.completion", "Completion must refer to this operation's original objective.");
            ProtocolValidation.Identifier(evt.Completion!.EvidenceId);
            ProtocolValidation.Text(evt.Completion.Summary, 4000, "operation completion summary");
            if (evt.Completion.EvidenceId == entry.Admission.EvidenceId)
                Fail("operation.completion", "Completion needs its own evidence identity, separate from admission.");
        }
        else if (evt.Completion is not null) Fail("operation.completion", "Only a completed event may carry objective completion.");
        if (evt.Kind == "decision") ProtocolValidation.Identifier(successorRunId!);
        else if (successorRunId is not null) Fail("operation.successor", "Only a decision event may reserve a successor activation.");
        if (JsonSerializer.SerializeToUtf8Bytes(evt, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
            Fail("operation.event_bounds", "Semantic event exceeds the wire bound.");
        return entry with
        {
            ActiveEpoch = evt.NextSessionEpoch ?? entry.ActiveEpoch,
            LatestEvent = Copy(evt),
            WakeRunId = successorRunId ?? entry.WakeRunId,
            State = evt.Kind switch { "decision" => "waking", "completed" => "completed", "blocked" => "blocked", "cancelled" => "cancelled", _ => "parked" }
        };
    }

    private static HostOperationEntry Finish(HostOperationEntry entry, string state, HostRunUsage usage)
    {
        if (entry.State != "waking" || entry.WakeRunId is null || entry.LatestEvent?.Kind != "decision")
            Fail("operation.not_waking", "Only a claimed semantic wake can be settled by its successor activation.");
        if (state is not ("continued" or "completed" or "blocked" or "cancelled"))
            Fail("operation.wake_state", "Unknown successor activation settlement state.");
        if (usage is null || usage.Steps < entry.Usage.Steps || usage.Refinements < entry.Usage.Refinements ||
            usage.Continuations <= entry.Usage.Continuations || usage.ProviderCalls < entry.Usage.ProviderCalls)
            Fail("operation.wake_usage", "Settled usage cannot decrease admission totals and must include the claimed continuation.");
        return entry with { State = state, SettledUsage = usage };
    }

    private static void ValidateEventHistory(HostOperationEntry entry, IReadOnlyList<StoredEvent> history, HostOperationEvent evt)
    {
        if (evt.Kind == "transfer" && (evt.NextSessionEpoch == entry.Request.SessionEpoch ||
            history.Any(item => item.Event.SessionEpoch == evt.NextSessionEpoch || item.Event.NextSessionEpoch == evt.NextSessionEpoch)))
            Fail("operation.epoch_reused", "A retired session epoch cannot become the operation owner again.");
    }

    private static void ValidateReservation(HostRunRequest request, HostActionRequest action, HostRunUsage usage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(usage);
        ProtocolValidation.Validate(request);
        foreach (var id in new[] { action.ActionId, action.RunId, action.RunnerRunId, action.StepId, action.CapabilityId }) ProtocolValidation.Identifier(id);
        if (action.SessionId != request.SessionId || action.SessionEpoch != request.SessionEpoch ||
            action.ManifestHash != ProtocolValidation.Digest(request.Capabilities) ||
            !request.Capabilities.Any(item => item.Id == action.CapabilityId && item.DurableHandoff))
            Fail("operation.reservation_binding", "Operation reservation must bind an original action and declared durable capability.");
        if (action.ExpectedRevision < request.Observation.Revision || action.DeadlineAt == default || action.Arguments.ValueKind != JsonValueKind.Object ||
            usage.Steps < 0 || usage.Refinements < 0 || usage.Continuations < 0 || usage.ProviderCalls < 0)
            Fail("operation.reservation_invalid", "Operation reservation needs an ordered revision, deadline, object arguments and nonnegative usage.");
        if (JsonSerializer.SerializeToUtf8Bytes(action, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
            Fail("operation.reservation_bounds", "Original operation action exceeds the wire bound.");
    }

    private static void ValidateUsage(JsonElement? usage)
    {
        if (usage is { } value && (value.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(value, HostProtocol.Json).Length > MaximumUsageBytes))
            Fail("operation.usage", "Host operation usage must be a bounded object.");
    }

    private static bool SameAction(HostOperationEntry entry, string hostId, string sessionId, string actionId) =>
        entry.Request.HostId == hostId && entry.Request.SessionId == sessionId && entry.Action.ActionId == actionId;

    private static StoredOperation Find(LedgerPayload ledger, string hostId, string sessionId, string actionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        ProtocolValidation.Identifier(actionId);
        return ledger.Entries.FirstOrDefault(item => SameAction(item.Entry, hostId, sessionId, actionId))
            ?? throw new HostProtocolException("operation.not_found", "No original operation reservation is retained for this action.");
    }

    private void Replace(LedgerPayload ledger, StoredOperation previous, StoredOperation next) =>
        Write(ledger with { Entries = ledger.Entries.Select(item => item == previous ? next : item).ToArray() });

    private LedgerPayload Read(bool requireMarker = true)
    {
        if (!File.Exists(_path)) throw new InvalidDataException("Operation ledger is unavailable; continuation is fenced.");
        if (new FileInfo(_path).Length > MaximumFileBytes) throw new InvalidDataException("Operation ledger exceeds its structural size bound.");
        try
        {
            var file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(_path), HostProtocol.Json)
                ?? throw new InvalidDataException("Operation ledger is empty.");
            if (file.Version != 1 || file.Payload?.Entries is null || !Guid.TryParseExact(file.Payload.StoreId, "N", out _) ||
                file.ContentHash != ProtocolValidation.Digest(file.Payload) || file.Payload.Entries.Count > MaximumEntries)
                throw new InvalidDataException("Operation ledger version, identity, hash or bounds are invalid.");
            var identities = new HashSet<(string, string, string)>();
            var operations = new HashSet<(string, string, string)>();
            var events = new HashSet<(string, string, string)>();
            foreach (var stored in file.Payload.Entries)
            {
                var entry = stored.Entry;
                ValidateReservation(entry.Request, entry.Action, entry.Usage);
                if (stored.Events is null || stored.RequestHash != ProtocolValidation.Digest(entry.Request) || stored.ActionHash != ProtocolValidation.Digest(entry.Action) ||
                    entry.CreatedAt == default || entry.UpdatedAt < entry.CreatedAt ||
                    !identities.Add((entry.Request.HostId, entry.Request.SessionId, entry.Action.ActionId)))
                    throw new InvalidDataException("Operation reservation identity or hashes are invalid.");
                var replay = entry with { Admission = null, ActiveEpoch = entry.Request.SessionEpoch, LatestEvent = null, WakeRunId = null, State = "reserved", SettledUsage = null };
                if (entry.Admission is not null)
                {
                    ValidateAdmission(entry.Request, entry.Action, entry.Admission);
                    if (stored.AdmissionHash != ProtocolValidation.Digest(entry.Admission) ||
                        !operations.Add((entry.Request.HostId, entry.Request.SessionId, entry.Admission.Operation!.OperationId)))
                        throw new InvalidDataException("Operation admission identity or hash is invalid.");
                    replay = replay with { Admission = entry.Admission, State = "parked" };
                }
                else if (stored.AdmissionHash is not null) throw new InvalidDataException("Operation admission hash has no source.");
                var history = new List<StoredEvent>();
                foreach (var delivery in stored.Events)
                {
                    if (delivery.EventHash != ProtocolValidation.Digest(delivery.Event) ||
                        !events.Add((entry.Request.HostId, entry.Request.SessionId, delivery.Event.EventId)))
                        throw new InvalidDataException("Operation event identity or hash is invalid.");
                    ValidateEventHistory(replay, history, delivery.Event);
                    replay = Transition(replay, delivery.Event, delivery.SuccessorRunId);
                    history.Add(delivery);
                }
                if (stored.FinishState is not null)
                {
                    if (stored.FinishUsage is null) throw new InvalidDataException("Settled operation usage is missing.");
                    replay = Finish(replay, stored.FinishState, stored.FinishUsage);
                }
                else if (stored.FinishUsage is not null) throw new InvalidDataException("Settled operation usage has no successor settlement.");
                if (ProtocolValidation.Digest(replay) != ProtocolValidation.Digest(entry))
                    throw new InvalidDataException("Operation state does not resolve to its retained admission and events.");
            }
            if (requireMarker) ValidateMarker(file.Payload.StoreId);
            return file.Payload;
        }
        catch (Exception exception) when (exception is JsonException or HostProtocolException or ArgumentException or NullReferenceException or InvalidOperationException)
        {
            throw new InvalidDataException("Operation ledger is invalid; continuation is fenced.", exception);
        }
    }

    private void ValidateMarker(string storeId)
    {
        if (!File.Exists(_markerPath) || new FileInfo(_markerPath).Length > 128 || File.ReadAllText(_markerPath) != storeId)
            throw new InvalidDataException("Operation initialization marker is unavailable or changed; continuation is fenced.");
    }

    private void Write(LedgerPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LedgerFile(1, payload, ProtocolValidation.Digest(payload)), HostProtocol.Json);
        if (bytes.Length > MaximumFileBytes) Fail("operation.storage_limit", "Operation retention is full; source and deduplication records cannot be pruned automatically.");
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
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, HostProtocol.Json), HostProtocol.Json)!;
    [DoesNotReturn]
    private static void Fail(string code, string message) => throw new HostProtocolException(code, message);
    private sealed record StoredEvent(HostOperationEvent Event, string EventHash, string? SuccessorRunId);
    private sealed record StoredOperation(HostOperationEntry Entry, string RequestHash, string ActionHash, string? AdmissionHash,
        IReadOnlyList<StoredEvent> Events, string? FinishState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HostRunUsage? FinishUsage = null);
    private sealed record LedgerPayload(string StoreId, IReadOnlyList<StoredOperation> Entries);
    private sealed record LedgerFile(int Version, LedgerPayload Payload, string ContentHash);
}

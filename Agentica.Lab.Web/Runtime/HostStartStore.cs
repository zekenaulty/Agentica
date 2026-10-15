using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

/// <summary>
/// Durable initial-start identities and Stop tombstones. Never executes, retries, wakes, or decides quiescence.
/// Only the registry may certify settlement after checking all live windows, action custody and operations.
/// Use one instance in one service process per storage directory. Entries are never evicted.
/// </summary>
public sealed partial class HostStartStore
{
    public const string LedgerFileName = "host-starts-v1.json";
    public const int MaximumEntries = 512;
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumRequestBytes = 65_536;
    private const string MarkerFileName = "host-starts-v1.initialized";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _markerPath;

    public HostStartEntry? Find(HostStartKey key)
    {
        ValidateKey(key);
        lock (_gate)
        {
            var entry = Read().Entries.FirstOrDefault(item => SameKey(item, key));
            return entry is null ? null : Copy(entry);
        }
    }

    public bool HasOpen(string hostId, string sessionId)
    {
        ValidateSession(hostId, sessionId);
        lock (_gate) return Read().Entries.Any(item => SameSession(item, hostId, sessionId) && item.Terminal is null);
    }

    public bool HasRetainedObjective(string hostId, string sessionId, string objectiveId)
    {
        ValidateSession(hostId, sessionId);
        ProtocolValidation.Identifier(objectiveId);
        lock (_gate) return Read().Entries.Any(item => SameSession(item, hostId, sessionId) && item.OriginalRequest.ObjectiveId == objectiveId);
    }

    public bool IsStopRequested(string hostId, string sessionId, string objectiveId)
    {
        ValidateSession(hostId, sessionId);
        ProtocolValidation.Identifier(objectiveId);
        lock (_gate) return Read().Entries.Any(item => SameSession(item, hostId, sessionId) &&
            item.OriginalRequest.ObjectiveId == objectiveId && item.StopRequested);
    }

    public HostStartEntry Admit(string attemptId, HostRunRequest original, HostRunRequest accepted, string runId)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(accepted);
        original = Copy(original);
        accepted = Copy(accepted);
        ValidateAdmission(attemptId, original, accepted, runId);
        var originalHash = ProtocolValidation.Digest(original);
        var acceptedHash = ProtocolValidation.Digest(accepted);
        var key = new HostStartKey(original.HostId, original.SessionId, attemptId);
        lock (_gate)
        {
            var ledger = Read();
            var existing = ledger.Entries.FirstOrDefault(item => SameKey(item, key));
            if (existing is not null)
            {
                if (existing.RunId != runId || existing.OriginalRequestHash != originalHash || existing.AcceptedRequestHash != acceptedHash)
                    Fail("start.changed", "An original start attempt cannot change its run, requested input or accepted input.");
                return Copy(existing);
            }
            if (ledger.Entries.Any(item => item.RunId == runId))
                Fail("start.run_collision", "This run identity already belongs to another initial start.");
            if (ledger.Entries.Any(item => SameSession(item, original.HostId, original.SessionId) && item.OriginalRequest.ObjectiveId == original.ObjectiveId))
                Fail("start.objective_retained", "This objective already has an initial start, including after Stop.");
            if (ledger.Entries.Any(item => SameSession(item, original.HostId, original.SessionId) && item.Terminal is null))
                Fail("start.session_open", "Settle the original start before admitting another attempt for this session.");
            if (ledger.Entries.Count >= MaximumEntries)
                Fail("start.capacity", "Start retention is full; original attempts and Stop tombstones cannot be evicted.");
            var entry = new HostStartEntry(1, attemptId, runId, original, accepted, originalHash, acceptedHash,
                ProtocolValidation.Digest(accepted.Capabilities), false, null, DateTimeOffset.UtcNow);
            Write(ledger with { Entries = [.. ledger.Entries, entry] });
            return Copy(entry);
        }
    }

    public HostStartEntry RequestStop(HostStartReference reference)
    {
        ValidateReference(reference);
        lock (_gate)
        {
            var ledger = Read();
            var original = Referenced(ledger, reference);
            if (original.StopRequested) return Copy(original);
            var stopped = original with { StopRequested = true };
            Replace(ledger, original, stopped);
            return Copy(stopped);
        }
    }

    /// <summary>The caller must prove session quiescence; this store records that decision without executing checks.</summary>
    public HostStartEntry SettleStopped(HostStartReference reference, string reason, HostRunUsage? usage, bool usageUnknown)
    {
        ValidateReference(reference);
        ValidateSettlement(reason, usage, usageUnknown);
        lock (_gate)
        {
            var ledger = Read();
            var original = Referenced(ledger, reference);
            if (!original.StopRequested) Fail("start.stop_required", "Persist the original Stop request before certifying settlement.");
            if (original.Terminal is { } terminal)
            {
                if (terminal.Reason != reason || terminal.Usage != usage || terminal.UsageUnknown != usageUnknown)
                    Fail("start.terminal_changed", "An original Stop settlement is immutable, including its usage evidence.");
                return Copy(original);
            }
            var stopped = original with { Terminal = new HostStartTerminal(reason, usage, usageUnknown, DateTimeOffset.UtcNow) };
            Replace(ledger, original, stopped);
            return Copy(stopped);
        }
    }

    private static bool SameSession(HostStartEntry entry, string hostId, string sessionId) =>
        entry.OriginalRequest.HostId == hostId && entry.OriginalRequest.SessionId == sessionId;
    private static bool SameKey(HostStartEntry entry, HostStartKey key) =>
        SameSession(entry, key.HostId, key.SessionId) && entry.AttemptId == key.AttemptId;
    private static HostStartEntry Referenced(LedgerPayload ledger, HostStartReference reference)
    {
        var key = new HostStartKey(reference.HostId, reference.SessionId, reference.AttemptId);
        var entry = ledger.Entries.FirstOrDefault(item => SameKey(item, key))
            ?? throw new HostProtocolException("start.not_found", "No original start is retained for this attempt.");
        if (entry.RunId != reference.RunId || entry.AcceptedRequestHash != reference.AcceptedRequestHash)
            Fail("start.reference", "Stop must bind the original run and accepted full-request digest.");
        return entry;
    }
    private void Replace(LedgerPayload ledger, HostStartEntry original, HostStartEntry updated) =>
        Write(ledger with { Entries = ledger.Entries.Select(item => item == original ? updated : item).ToArray() });
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, HostProtocol.Json), HostProtocol.Json)!;
    [DoesNotReturn]
    private static void Fail(string code, string message) => throw new HostProtocolException(code, message);
}

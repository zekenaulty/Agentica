using System.Text.Json;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

public sealed partial class HostStartStore
{
    // Logical headroom for the bounded terminal record, not a physical disk-space guarantee.
    private const int UnsettledEntryAllowanceBytes = 512;

    public HostStartStore(string rootDirectory)
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
                if (File.Exists(_markerPath)) throw new InvalidDataException("Initialized start ledger is missing; admission is fenced.");
                var initial = new LedgerPayload(Guid.NewGuid().ToString("N"), []);
                Write(initial);
                using var stream = new FileStream(_markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    writer.Write(initial.StoreId);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            // Partial initialization is not an empty-history fallback, even if the ledger is empty.
            Read();
        }
    }

    private LedgerPayload Read()
    {
        if (!File.Exists(_path)) throw new InvalidDataException("Start ledger is unavailable; admission is fenced.");
        if (new FileInfo(_path).Length > MaximumFileBytes) throw new InvalidDataException("Start ledger exceeds its structural size bound.");
        try
        {
            var file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(_path), HostProtocol.Json)
                ?? throw new InvalidDataException("Start ledger is empty.");
            if (file.Version != 1 || file.Payload?.Entries is null || !Guid.TryParseExact(file.Payload.StoreId, "N", out _) ||
                file.Payload.Entries.Count > MaximumEntries || file.ContentHash != ProtocolValidation.Digest(file.Payload))
                throw new InvalidDataException("Start ledger version, identity, checksum or bounds are invalid.");
            ValidateMarker(file.Payload.StoreId);
            var keys = new HashSet<(string, string, string)>();
            var runs = new HashSet<string>(StringComparer.Ordinal);
            var objectives = new HashSet<(string, string, string)>();
            var openSessions = new HashSet<(string, string)>();
            foreach (var entry in file.Payload.Entries)
            {
                ValidateEntry(entry);
                var request = entry.OriginalRequest;
                if (!keys.Add((request.HostId, request.SessionId, entry.AttemptId)) || !runs.Add(entry.RunId) ||
                    !objectives.Add((request.HostId, request.SessionId, request.ObjectiveId)) ||
                    (entry.Terminal is null && !openSessions.Add((request.HostId, request.SessionId))))
                    throw new InvalidDataException("Start attempt, run, objective or open-session identities collide.");
            }
            if (JsonSerializer.SerializeToUtf8Bytes(file, HostProtocol.Json).LongLength + SettlementAllowance(file.Payload) > MaximumFileBytes)
                throw new InvalidDataException("Start ledger exceeds its logical size bound including reserved Stop settlement space.");
            return file.Payload;
        }
        catch (Exception exception) when (exception is JsonException or HostProtocolException or ArgumentException or NullReferenceException or InvalidOperationException)
        {
            throw new InvalidDataException("Start ledger is invalid; admission is fenced.", exception);
        }
    }

    private void ValidateMarker(string storeId)
    {
        if (!File.Exists(_markerPath) || new FileInfo(_markerPath).Length > 128 || File.ReadAllText(_markerPath) != storeId)
            throw new InvalidDataException("Start initialization marker is unavailable or changed; admission is fenced.");
    }

    private void Write(LedgerPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LedgerFile(1, payload, ProtocolValidation.Digest(payload)), HostProtocol.Json);
        if (bytes.LongLength + SettlementAllowance(payload) > MaximumFileBytes)
            Fail("start.storage_limit", "Start retention including reserved Stop settlement space exceeds 64 MiB; original attempts and Stop tombstones cannot be pruned.");
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

    private static long SettlementAllowance(LedgerPayload payload) =>
        (long)UnsettledEntryAllowanceBytes * payload.Entries.Count(entry => entry.Terminal is null);

    private sealed record LedgerPayload(string StoreId, IReadOnlyList<HostStartEntry> Entries);
    private sealed record LedgerFile(int Version, LedgerPayload Payload, string ContentHash);
}

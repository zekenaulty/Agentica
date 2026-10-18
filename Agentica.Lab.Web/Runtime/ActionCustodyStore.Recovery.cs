using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

public sealed partial class ActionCustodyStore
{
    public const int RecoveryPageSize = 16;

    public HostRunRecoveryPage ListRetainedForRun(string hostId, string sessionId, string sessionEpoch,
        string runId, string? afterActionId = null)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        ProtocolValidation.Identifier(sessionEpoch);
        ProtocolValidation.Identifier(runId);
        if (afterActionId is not null) ProtocolValidation.Identifier(afterActionId);
        lock (_gate)
        {
            // Paging bounds the response, not I/O: Read validates the entire 1,024-entry/64 MiB ledger.
            var entries = Read().Entries.Where(entry => entry.HostId == hostId && entry.SessionId == sessionId &&
                    entry.SessionEpoch == sessionEpoch && entry.RunId == runId &&
                    (afterActionId is null || StringComparer.Ordinal.Compare(entry.Request.ActionId, afterActionId) > 0))
                .OrderBy(entry => entry.Request.ActionId, StringComparer.Ordinal)
                .Take(RecoveryPageSize + 1).ToArray();
            var items = entries.Take(RecoveryPageSize).Select(entry => new HostRunRecoveryItem(
                entry.Request.ActionId, entry.RequestHash, entry.CapabilityHash,
                entry.Result?.Disposition, entry.RecoveryRetention)).ToArray();
            return new(hostId, sessionId, sessionEpoch, runId, items,
                entries.Length > RecoveryPageSize ? items[^1].ActionId : null);
        }
    }
}

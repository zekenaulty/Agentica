namespace Agentica.Lab.Web.Contracts;

/// <summary>A page of retained evidence, not a claim that the run or host obligations are complete.</summary>
public sealed record HostRunRecoveryPage(
    string HostId,
    string SessionId,
    string SessionEpoch,
    string RunId,
    IReadOnlyList<HostRunRecoveryItem> Items,
    string? NextAfterActionId);

public sealed record HostRunRecoveryItem(
    string ActionId,
    string RequestHash,
    string CapabilityHash,
    string? Disposition,
    string? RecoveryRetention);

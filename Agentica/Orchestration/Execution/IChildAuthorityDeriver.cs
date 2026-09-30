namespace Agentica.Orchestration.Execution;

/// <summary>
/// The host resolves a proposed child objective against its durable parent authority.
/// A planner's task graph is a proposal and cannot issue child authority itself.
/// </summary>
public interface IChildAuthorityDeriver
{
    Task<ChildAuthorityResolution> DeriveAsync(
        ChildAuthorityDerivationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ChildAuthorityDerivationRequest(
    string ParentAuthorizationScopeId,
    string ParentObjective,
    string ChildTaskId,
    string ChildObjective,
    string ChildDispatchId,
    int ChildRunNumber,
    IReadOnlyDictionary<string, object?> ChildContextProjection);

public enum ChildAuthorityDisposition
{
    Derived,
    OutOfScope,
    TemporarilyUnavailable
}

/// <summary>
/// Derived requires a distinct host-owned child scope and a durable derivation receipt.
/// An unavailable execution control does not extinguish the parent objective.
/// </summary>
public sealed record ChildAuthorityResolution(
    ChildAuthorityDisposition Disposition,
    string? ChildAuthorizationScopeId = null,
    string? DerivationReceiptId = null,
    string? Reason = null);

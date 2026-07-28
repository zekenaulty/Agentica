using Agentica.Tools;

namespace Agentica.Execution;

/// <summary>
/// Authoritative evidence that a one-shot grant was atomically consumed before tool dispatch.
/// </summary>
public sealed record ToolGrantConsumption(
    string GrantId,
    string AuthorizationScopeId,
    string RunId,
    int AttemptNumber,
    string StepId,
    string ToolId,
    string ManifestHash,
    string InvocationInputDigest,
    string Issuer,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ToolDataBoundary> AllowedOutboundBoundaries,
    IReadOnlyList<ToolExternalOutputClassification> AllowedExternalOutputs,
    DateTimeOffset ConsumedAt);

using System.Collections.Immutable;

namespace Agentica.Clients.Llm;

public enum LlmCapabilitySupport
{
    Unknown,
    Supported,
    Unsupported
}

public enum LlmControlDisposition
{
    ProviderDefault,
    AdapterDefault,
    Applied,
    NotApplied,
    Unsupported,
    Unresolved
}

public sealed record LlmControlReport(
    string Name,
    string? RequestedValue,
    string? AppliedValue,
    LlmControlDisposition Disposition,
    LlmCapabilitySupport AdapterSupport,
    string Explanation);

/// <summary>Adapter support describes the installed serializer, not every model or account.</summary>
public sealed record LlmCapabilityReport(
    string Name,
    LlmCapabilitySupport AdapterSupport,
    string Explanation);

public sealed record LlmConfigurationEvidence(
    LlmCapabilitySupport State,
    string Source,
    DateTimeOffset? AsOf,
    string Explanation);

/// <summary>A host observation is bound to the exact nonsecret configuration identity.
/// Hosts remain responsible for its source, age and account/credential scope.</summary>
public sealed record LlmConfigurationObservation(
    string ConfigurationIdentity,
    LlmCapabilitySupport State,
    string Source,
    DateTimeOffset AsOf,
    string Explanation);

public sealed record LlmConfigurationReportContext(
    DateTimeOffset? AsOf = null,
    string? HostRouteReference = null,
    string? HostExecutionBundleReference = null,
    LlmConfigurationObservation? TechnicalReadiness = null,
    LlmConfigurationObservation? ModelAccountQualification = null);

/// <summary>A stable classification, never a provider exception message or request content.</summary>
public sealed record LlmConfigurationIssue(string Code, string Explanation);

/// <summary>
/// A provider-only configuration snapshot. This is neither a dispatch grant nor a request hash.
/// Credentials, prompts, continuation payloads, metadata and host bundle references are excluded
/// from ConfigurationIdentity. Observations do not establish future readiness or authorization.
/// </summary>
public sealed record LlmConfigurationReport(
    string ProviderName,
    string ApiMode,
    string RequestedModelId,
    string EffectiveModelId,
    string ConfigurationIdentity,
    string InputAccountingBasis,
    ImmutableArray<LlmControlReport> Controls,
    ImmutableArray<LlmCapabilityReport> Capabilities,
    ImmutableArray<LlmConfigurationIssue> Issues,
    LlmConfigurationEvidence AdapterEvidence,
    LlmConfigurationEvidence RouteResolution,
    LlmConfigurationEvidence TechnicalReadiness,
    LlmConfigurationEvidence ModelAccountQualification,
    string? HostRouteReference,
    string? HostExecutionBundleReference)
{
    /// <summary>False when the actual adapter builder rejects the request. True does not prove
    /// model/account support, credentials, transport readiness or successful inference.</summary>
    public bool AdapterAcceptsRequest => Issues.IsEmpty;
}

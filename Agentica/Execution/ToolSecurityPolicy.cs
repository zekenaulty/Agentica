using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Agentica.Tools;

namespace Agentica.Execution;

/// <summary>
/// A manifest- and tool-bound authorization for one security-sensitive dispatch.
/// Boundary authorization is outbound; allowed output classifications authorize
/// accepting the declared untrusted inbound result class.
/// </summary>
public sealed class ToolExecutionGrant
{
    public ToolExecutionGrant(
        string ManifestHash,
        string ToolId,
        IEnumerable<ToolDataBoundary> AllowedOutboundBoundaries,
        IEnumerable<ToolExternalOutputClassification> AllowedExternalOutputs,
        DateTimeOffset ExpiresAt,
        string Issuer)
    {
        ArgumentNullException.ThrowIfNull(ManifestHash);
        ArgumentNullException.ThrowIfNull(ToolId);
        ArgumentNullException.ThrowIfNull(AllowedOutboundBoundaries);
        ArgumentNullException.ThrowIfNull(AllowedExternalOutputs);
        ArgumentNullException.ThrowIfNull(Issuer);

        if (!IsVersionedManifestHash(ManifestHash))
        {
            throw new ArgumentException("Grant ManifestHash must be a nonblank versioned manifest hash.", nameof(ManifestHash));
        }

        if (string.IsNullOrWhiteSpace(ToolId))
        {
            throw new ArgumentException("Grant ToolId is required.", nameof(ToolId));
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new ArgumentException("Grant Issuer is required.", nameof(Issuer));
        }

        var outboundBoundaries = AllowedOutboundBoundaries.ToArray();
        if (outboundBoundaries.Contains(ToolDataBoundary.Unknown))
        {
            throw new ArgumentException("Grant outbound boundaries cannot contain Unknown.", nameof(AllowedOutboundBoundaries));
        }

        var externalOutputs = AllowedExternalOutputs.ToArray();
        if (externalOutputs.Contains(ToolExternalOutputClassification.Unknown))
        {
            throw new ArgumentException("Grant external-output classifications cannot contain Unknown.", nameof(AllowedExternalOutputs));
        }

        this.ManifestHash = ManifestHash;
        this.ToolId = ToolId;
        this.AllowedOutboundBoundaries = outboundBoundaries.ToFrozenSet();
        this.AllowedExternalOutputs = externalOutputs.ToFrozenSet();
        this.ExpiresAt = ExpiresAt;
        this.Issuer = Issuer;
    }

    public string ManifestHash { get; }

    public string ToolId { get; }

    public IReadOnlySet<ToolDataBoundary> AllowedOutboundBoundaries { get; }

    public IReadOnlySet<ToolExternalOutputClassification> AllowedExternalOutputs { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string Issuer { get; }

    private static bool IsVersionedManifestHash(string value)
    {
        const string prefix = "sha256-v1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 64)
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Frozen security policy for a run. A null ExternalPlannerAllowedBoundaries means
/// the planner is local. A non-null (including empty) set means the planner is
/// external and may receive only the listed boundaries.
/// </summary>
public sealed class ToolSecurityPolicy
{
    public ToolSecurityPolicy(
        IEnumerable<ToolDataBoundary>? InitialBoundaries = null,
        IEnumerable<ToolDataBoundary>? ExternalPlannerAllowedBoundaries = null,
        IEnumerable<ToolExecutionGrant>? ExecutionGrants = null)
    {
        var initialBoundaries = (InitialBoundaries ?? []).ToArray();
        var plannerBoundaries = ExternalPlannerAllowedBoundaries?.ToArray();
        if (initialBoundaries.Contains(ToolDataBoundary.Unknown) ||
            plannerBoundaries?.Contains(ToolDataBoundary.Unknown) == true)
        {
            throw new ArgumentException("Security-policy boundary sets cannot contain Unknown.");
        }

        var grants = (ExecutionGrants ?? []).Select(CloneGrant).ToArray();
        var duplicate = FindAmbiguousDuplicate(grants);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Security policy contains duplicate ambiguous grants for manifest '{duplicate.ManifestHash}' " +
                $"and tool '{duplicate.ToolId}'.",
                nameof(ExecutionGrants));
        }

        this.InitialBoundaries = initialBoundaries.ToFrozenSet();
        this.ExternalPlannerAllowedBoundaries = plannerBoundaries?.ToFrozenSet();
        this.ExecutionGrants = new ReadOnlyCollection<ToolExecutionGrant>(grants);
    }

    public static ToolSecurityPolicy Local { get; } = new();

    public IReadOnlySet<ToolDataBoundary> InitialBoundaries { get; }

    public IReadOnlySet<ToolDataBoundary>? ExternalPlannerAllowedBoundaries { get; }

    public IReadOnlyList<ToolExecutionGrant> ExecutionGrants { get; }

    public bool UsesExternalPlanner => ExternalPlannerAllowedBoundaries is not null;

    private static ToolExecutionGrant CloneGrant(ToolExecutionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new ToolExecutionGrant(
            grant.ManifestHash,
            grant.ToolId,
            grant.AllowedOutboundBoundaries,
            grant.AllowedExternalOutputs,
            grant.ExpiresAt,
            grant.Issuer);
    }

    private static ToolExecutionGrant? FindAmbiguousDuplicate(IReadOnlyList<ToolExecutionGrant> grants)
    {
        for (var leftIndex = 0; leftIndex < grants.Count; leftIndex++)
        {
            var left = grants[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < grants.Count; rightIndex++)
            {
                var right = grants[rightIndex];
                if (string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal) &&
                    string.Equals(left.ToolId, right.ToolId, StringComparison.Ordinal) &&
                    left.AllowedOutboundBoundaries.SetEquals(right.AllowedOutboundBoundaries) &&
                    left.AllowedExternalOutputs.SetEquals(right.AllowedExternalOutputs))
                {
                    return left;
                }
            }
        }

        return null;
    }
}

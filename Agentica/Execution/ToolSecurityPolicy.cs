using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text;
using Agentica.Tools;

namespace Agentica.Execution;

/// <summary>
/// A one-shot, invocation-bound authorization for one security-sensitive dispatch.
/// Boundary authorization is outbound; allowed output classifications authorize
/// accepting the declared untrusted inbound result class. Policy snapshots share the
/// atomic consumption state so a grant cannot be replayed across runners or attempts.
/// </summary>
public sealed class ToolExecutionGrant
{
    public ToolExecutionGrant(
        string GrantId,
        string AuthorizationScopeId,
        int AttemptNumber,
        string StepId,
        string InvocationInputDigest,
        string ManifestHash,
        string ToolId,
        IEnumerable<ToolDataBoundary> AllowedOutboundBoundaries,
        IEnumerable<ToolExternalOutputClassification> AllowedExternalOutputs,
        DateTimeOffset ExpiresAt,
        string Issuer)
        : this(
            GrantId,
            AuthorizationScopeId,
            AttemptNumber,
            StepId,
            InvocationInputDigest,
            ManifestHash,
            ToolId,
            AllowedOutboundBoundaries,
            AllowedExternalOutputs,
            ExpiresAt,
            Issuer,
            new GrantConsumptionState())
    {
    }

    private ToolExecutionGrant(
        string GrantId,
        string AuthorizationScopeId,
        int AttemptNumber,
        string StepId,
        string InvocationInputDigest,
        string ManifestHash,
        string ToolId,
        IEnumerable<ToolDataBoundary> AllowedOutboundBoundaries,
        IEnumerable<ToolExternalOutputClassification> AllowedExternalOutputs,
        DateTimeOffset ExpiresAt,
        string Issuer,
        GrantConsumptionState consumptionState)
    {
        ArgumentNullException.ThrowIfNull(GrantId);
        ArgumentNullException.ThrowIfNull(AuthorizationScopeId);
        ArgumentNullException.ThrowIfNull(StepId);
        ArgumentNullException.ThrowIfNull(InvocationInputDigest);
        ArgumentNullException.ThrowIfNull(ManifestHash);
        ArgumentNullException.ThrowIfNull(ToolId);
        ArgumentNullException.ThrowIfNull(AllowedOutboundBoundaries);
        ArgumentNullException.ThrowIfNull(AllowedExternalOutputs);
        ArgumentNullException.ThrowIfNull(Issuer);
        ArgumentNullException.ThrowIfNull(consumptionState);

        if (string.IsNullOrWhiteSpace(GrantId))
        {
            throw new ArgumentException("GrantId is required.", nameof(GrantId));
        }

        if (string.IsNullOrWhiteSpace(AuthorizationScopeId))
        {
            throw new ArgumentException("Grant AuthorizationScopeId is required.", nameof(AuthorizationScopeId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(AttemptNumber);

        if (string.IsNullOrWhiteSpace(StepId))
        {
            throw new ArgumentException("Grant StepId is required.", nameof(StepId));
        }

        if (!ToolInvocationAuthorization.IsVersionedDigest(InvocationInputDigest))
        {
            throw new ArgumentException(
                "Grant InvocationInputDigest must be a nonblank versioned SHA-256 digest.",
                nameof(InvocationInputDigest));
        }

        if (!ToolInvocationAuthorization.IsVersionedDigest(ManifestHash))
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

        var outboundBoundaries = ToolSecurityBounds.SnapshotEnumValues(
            AllowedOutboundBoundaries,
            nameof(AllowedOutboundBoundaries));
        if (outboundBoundaries.Any(boundary => !Enum.IsDefined(boundary)))
        {
            throw new ArgumentException(
                "Grant outbound boundaries cannot contain undefined values.",
                nameof(AllowedOutboundBoundaries));
        }

        if (outboundBoundaries.Contains(ToolDataBoundary.Unknown))
        {
            throw new ArgumentException("Grant outbound boundaries cannot contain Unknown.", nameof(AllowedOutboundBoundaries));
        }

        var externalOutputs = ToolSecurityBounds.SnapshotEnumValues(
            AllowedExternalOutputs,
            nameof(AllowedExternalOutputs));
        if (externalOutputs.Any(output => !Enum.IsDefined(output)))
        {
            throw new ArgumentException(
                "Grant external-output classifications cannot contain undefined values.",
                nameof(AllowedExternalOutputs));
        }

        if (externalOutputs.Contains(ToolExternalOutputClassification.Unknown))
        {
            throw new ArgumentException("Grant external-output classifications cannot contain Unknown.", nameof(AllowedExternalOutputs));
        }

        this.GrantId = ToolSecurityBounds.RequiredText(GrantId, nameof(GrantId));
        this.AuthorizationScopeId = ToolSecurityBounds.RequiredText(
            AuthorizationScopeId,
            nameof(AuthorizationScopeId));
        this.AttemptNumber = AttemptNumber;
        this.StepId = ToolSecurityBounds.RequiredText(StepId, nameof(StepId));
        this.InvocationInputDigest = InvocationInputDigest;
        this.ManifestHash = ManifestHash;
        this.ToolId = ToolSecurityBounds.RequiredText(ToolId, nameof(ToolId));
        this.AllowedOutboundBoundaries = outboundBoundaries.ToFrozenSet();
        this.AllowedExternalOutputs = externalOutputs.ToFrozenSet();
        this.ExpiresAt = ExpiresAt;
        this.Issuer = ToolSecurityBounds.RequiredText(Issuer, nameof(Issuer));
        _consumptionState = consumptionState;
    }

    private readonly GrantConsumptionState _consumptionState;

    public string GrantId { get; }

    public string AuthorizationScopeId { get; }

    public int AttemptNumber { get; }

    public string StepId { get; }

    public string InvocationInputDigest { get; }

    public string ManifestHash { get; }

    public string ToolId { get; }

    public IReadOnlySet<ToolDataBoundary> AllowedOutboundBoundaries { get; }

    public IReadOnlySet<ToolExternalOutputClassification> AllowedExternalOutputs { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string Issuer { get; }

    public bool IsConsumed => _consumptionState.IsConsumed;

    internal bool TryConsume() => _consumptionState.TryConsume();

    internal ToolExecutionGrant Snapshot() =>
        new(
            GrantId,
            AuthorizationScopeId,
            AttemptNumber,
            StepId,
            InvocationInputDigest,
            ManifestHash,
            ToolId,
            AllowedOutboundBoundaries,
            AllowedExternalOutputs,
            ExpiresAt,
            Issuer,
            _consumptionState);

    private sealed class GrantConsumptionState
    {
        private int _consumed;

        public bool IsConsumed => Volatile.Read(ref _consumed) != 0;

        public bool TryConsume() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
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
        var initialBoundaries = ToolSecurityBounds.SnapshotEnumValues(
            InitialBoundaries ?? [],
            nameof(InitialBoundaries));
        var plannerBoundaries = ExternalPlannerAllowedBoundaries is null
            ? null
            : ToolSecurityBounds.SnapshotEnumValues(
                ExternalPlannerAllowedBoundaries,
                nameof(ExternalPlannerAllowedBoundaries));
        if (initialBoundaries.Any(boundary => !Enum.IsDefined(boundary)) ||
            plannerBoundaries?.Any(boundary => !Enum.IsDefined(boundary)) == true)
        {
            throw new ArgumentException("Security-policy boundary sets cannot contain undefined values.");
        }

        if (initialBoundaries.Contains(ToolDataBoundary.Unknown) ||
            plannerBoundaries?.Contains(ToolDataBoundary.Unknown) == true)
        {
            throw new ArgumentException("Security-policy boundary sets cannot contain Unknown.");
        }

        var grants = ToolSecurityBounds.SnapshotItems(
                ExecutionGrants ?? [],
                ToolSecurityBounds.MaxGrants,
                nameof(ExecutionGrants))
            .Select(CloneGrant)
            .ToArray();
        var duplicateGrantId = grants
            .GroupBy(grant => grant.GrantId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateGrantId is not null)
        {
            throw new ArgumentException(
                $"Security policy contains duplicate grant id '{duplicateGrantId}'.",
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
        return grant.Snapshot();
    }
}

internal static class ToolSecurityBounds
{
    private const int MaxTextUtf8Bytes = 4_096;
    private const int MaxEnumValues = 32;
    internal const int MaxGrants = 4_096;

    public static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        if (value.Length > MaxTextUtf8Bytes || Encoding.UTF8.GetByteCount(value) > MaxTextUtf8Bytes)
        {
            throw new ArgumentException(
                $"{parameterName} cannot exceed {MaxTextUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }

        return value;
    }

    public static T[] SnapshotEnumValues<T>(IEnumerable<T> source, string parameterName)
        where T : struct, Enum =>
        SnapshotItems(source, MaxEnumValues, parameterName);

    public static T[] SnapshotItems<T>(
        IEnumerable<T> source,
        int maximumItems,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = new List<T>();
        foreach (var item in source)
        {
            if (snapshot.Count >= maximumItems)
            {
                throw new ArgumentException(
                    $"{parameterName} cannot contain more than {maximumItems} items.",
                    parameterName);
            }

            snapshot.Add(item);
        }

        return snapshot.ToArray();
    }
}

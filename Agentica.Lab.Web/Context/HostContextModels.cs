using System.Text.Json;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Context;

public sealed record HostContextIdentity(string HostId, string SessionId, string SessionEpoch, string ScopeId, string PerspectiveId);

/// <summary>A content-bound source reference. Retention can remove the payload without making a claim new evidence.</summary>
public sealed record HostEvidenceReference(string ObservationId, long Revision, DateTimeOffset ObservedAt, string ContentHash);

public sealed record HostKnowledgeEntry(
    string Id, string Key, string Summary, JsonElement Value, string State, string Source,
    IReadOnlyList<HostEvidenceReference> Evidence, string? Supersedes, string Change,
    long Revision, DateTimeOffset UpdatedAt, bool IsCurrent);

public sealed record HostEvidenceManifest(HostEvidenceReference Reference, bool Available);

public sealed record HostContextSnapshot(
    HostContextIdentity Identity,
    HostObservation CurrentObservation,
    IReadOnlyList<HostKnowledgeEntry> Facts,
    IReadOnlyList<HostEvidenceManifest> Evidence,
    int RetainedObservationCount,
    long PrunedObservationCount,
    long PrunedFactCount);

public sealed record HostEvidenceReadResult(string Status, HostEvidenceReference? Reference, HostObservation? Observation);

public sealed record HostKnowledgeQueryItem(HostKnowledgeEntry Fact, IReadOnlyList<HostEvidenceManifest> Evidence);
public sealed record HostKnowledgeQueryResult(IReadOnlyList<HostKnowledgeQueryItem> Entries, int TotalMatched,
    int RemainingCount, string? NextCursor, long PrunedFactCount);

internal sealed record StoredObservation(HostObservation Observation, string ContentHash);
internal sealed record ContextPayload(HostContextIdentity Identity, IReadOnlyList<StoredObservation> Observations,
    IReadOnlyList<HostKnowledgeEntry> Facts, long PrunedObservationCount, long PrunedFactCount);
internal sealed record ContextFile(int Version, ContextPayload Payload, string ContentHash);
internal sealed record KnowledgeCursor(int Offset, string QueryHash, string KnowledgeHash);

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
    long PrunedFactCount,
    IReadOnlyList<HostThoughtTest> ThoughtTests,
    IReadOnlyList<HostActionEvidence> ActionEvidence);

public sealed record HostEvidenceReadResult(string Status, HostEvidenceReference? Reference, HostObservation? Observation);

public sealed record HostKnowledgeQueryItem(HostKnowledgeEntry Fact, IReadOnlyList<HostEvidenceManifest> Evidence, HostThoughtTest? ThoughtTest = null);
public sealed record HostKnowledgeQueryResult(IReadOnlyList<HostKnowledgeQueryItem> Entries, int TotalMatched,
    int RemainingCount, string? NextCursor, long PrunedFactCount);

public sealed record HostThoughtTest(string TestId, string HypothesisId, string CapabilityId, string ExpectedResult,
    string Falsifier, DateTimeOffset PlannedAt, HostEvidenceReference PlannedObservation, string? ActionId = null,
    HostThoughtAssessment? Assessment = null);

public sealed record HostThoughtAssessment(string Assessment, string Summary, string ActionId, string HostEvidenceId,
    string ObservationId, string ResultHash, string ObservationHash, string AssessedHypothesisId, DateTimeOffset AssessedAt);

public sealed record HostActionEvidence(string ActionId, string CapabilityId, string RunId, string RequestHash,
    DateTimeOffset IntentRecordedAt, IReadOnlyList<string> ThoughtTestIds, HostResultEvidence? Result = null);

public sealed record HostResultEvidence(string EvidenceId, string Disposition, long BeforeRevision, long AfterRevision,
    string ResultHash, HostEvidenceReference? Observation);

internal sealed record StoredObservation(HostObservation Observation, string ContentHash);
internal sealed record ContextPayload(HostContextIdentity Identity, IReadOnlyList<StoredObservation> Observations,
    IReadOnlyList<HostKnowledgeEntry> Facts, long PrunedObservationCount, long PrunedFactCount,
    IReadOnlyList<HostActionEvidence>? Actions = null, IReadOnlyList<HostThoughtTest>? ThoughtTests = null);
internal sealed record ContextFile(int Version, ContextPayload Payload, string ContentHash);
internal sealed record KnowledgeCursor(int Offset, string QueryHash, string KnowledgeHash);

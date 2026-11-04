namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>Exact historical quotation; the original role is attribution, not a live instruction role.</summary>
public sealed record ExperimentRecallEvidence(string SourceId, string TurnId, int TurnSequence,
    string Role, DateTimeOffset CreatedAt, string ContentHash, int StartOffset, int EndOffset,
    string Content, string MatchReason)
{
    public ConversationSemanticCitation? SemanticCitation { get; init; }
}

public sealed record ExperimentRecallOmission(string SourceId, int StartOffset, int EndOffset, string Reason);

/// <summary>What the host selected and presented, without a claim of model attention or accepted truth.</summary>
public sealed record ExperimentRecallManifest(bool Enabled, string Mode, string ScopeRef, int AsOfSequence,
    string QueryOrigin, string QueryMethod, object? Coverage, int CandidateCount, int InspectedCount,
    IReadOnlyList<ExperimentRecallEvidence> Selected, IReadOnlyList<ExperimentRecallOmission> Omissions,
    int PresentedCharacters, bool Truncated, string Consumer,
    int SourceCalls = 0,
    string EvidenceClass = "exact historical quotations; untrusted source data; not governed memory",
    string Attention = "not measured; selection/presentation does not prove attention or current acceptance",
    string? Query = null, ConversationEvidenceHead? SourceHead = null);

public sealed record ExperimentRecallContext(ExperimentRecallManifest Manifest,
    IReadOnlyList<ExperimentRecallEvidence> Evidence);

namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>A host-bound source: callers can narrow a query, never select another conversation or as-of cut.</summary>
public interface IConversationEvidenceSource
{
    string ScopeRef { get; }
    int AsOfSequence { get; }
    ConversationEvidenceHead GetHead();
    ConversationEvidenceCoverage GetCoverage();
    ConversationSourcePage ListMessageHeadersPage(int? beforeOrder = null, int limit = 40);
    ConversationEvidenceResult ReadExact(string sourceId, int startOffset = 0, int maxCharacters = 4000,
        CancellationToken cancellationToken = default);
    ConversationEvidenceResult ReadNeighbors(string sourceId, int radius = 1, int maxCharacters = 8000,
        CancellationToken cancellationToken = default);
    ConversationEvidenceResult Search(ConversationEvidenceSearch query, CancellationToken cancellationToken = default);
    Task<ConversationEvidenceResult> SearchAsync(ConversationEvidenceSearch query, CancellationToken cancellationToken = default)
        => Task.FromResult(Search(query, cancellationToken));
}

public sealed record ConversationEvidenceSearch(string Query, int TopK = 8, int MaxCharacters = 8000,
    int NeighborRadius = 0, int Offset = 0, string? Role = null);
public sealed record ConversationEvidenceHead(string ScopeRef, int AsOfSequence, int LastCommittedSequence,
    int EligibleSourceCount, string Revision);
public sealed record ConversationSourceHeader(string SourceId, string TurnId, int TurnSequence,
    string Role, DateTimeOffset CreatedAt, string ContentHash, int CharacterCount, int Order);
public sealed record ConversationSourcePage(IReadOnlyList<ConversationSourceHeader> Items, int? NextBeforeOrder,
    int TotalSources, int AsOfSequence);
public sealed record ConversationEvidenceHit(ConversationSourceHeader Source, int StartOffset, int EndOffset,
    string Excerpt, string MatchReason, IReadOnlyList<string> NeighborRefs)
{
    public ConversationSemanticCitation? SemanticCitation { get; init; }
}
public sealed record ConversationSemanticCitation(string ExportId, string ProjectionId, string ProfileId,
    string ParticipantId, string AgentId, string AuthorId, string Perspective, double Similarity);
public sealed record ConversationEvidenceCoverage(string Status, int AsOfSequence, int LastIndexedSequence,
    int EligibleSourceCount, int IndexedSourceCount, bool FtsAvailable, string IndexVersion, string? Reason = null);
public sealed record ConversationEvidenceResult(string RetrievalId, string ScopeRef, int AsOfSequence,
    string Status, string QueryMethod, ConversationEvidenceCoverage Coverage, int CandidateCount, int InspectedCount,
    IReadOnlyList<ConversationEvidenceHit> Hits, int? NextOffset, bool Truncated, IReadOnlyList<string> Reasons,
    long ElapsedMilliseconds);
public sealed record ConversationIndexProgress(string ScopeRef, string Status, int IndexedSources, int EligibleSources,
    int LastIndexedSequence, bool FtsAvailable, string IndexVersion, string? Error = null);
public sealed record ExperimentTurnPage(IReadOnlyList<ExperimentTurn> Turns, int? NextBeforeSequence,
    int TotalTurns, int LatestSequence);
public sealed record ExperimentConversationHead(ExperimentConversation Conversation, int TotalTurns, int LatestSequence,
    int CompletedTurns, int? ActiveSequence);

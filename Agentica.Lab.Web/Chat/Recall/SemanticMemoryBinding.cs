using Agentica.Memory;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>Host-selected identities and horizon; requests can narrow them, never choose another owner or conversation.</summary>
public sealed class SemanticMemoryBinding : IMemoryProjectionReader, IMemorySourceReader
{
    private readonly SemanticMemoryStorageProvider _storage;
    private readonly ExperimentStore _canonical;
    public MemoryScope Scope { get; }
    public long AsOfSequence { get; }
    public DateTimeOffset AsOfAt { get; }
    public IMemoryProjectionReader ProjectionReader => this;
    public IMemorySourceReader SourceReader => this;

    internal SemanticMemoryBinding(SemanticMemoryStorageProvider storage, ExperimentStore canonical,
        MemoryScope scope, long asOfSequence, DateTimeOffset asOfAt)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (asOfSequence is < 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(asOfSequence));
        // Validate this exact conversation's horizon without creating any lexical/vector index.
        _ = canonical.BindEvidenceSource(scope.ConversationId, (int)asOfSequence);
        _storage = storage; _canonical = canonical; Scope = scope;
        AsOfSequence = asOfSequence; AsOfAt = asOfAt.ToUniversalTime();
    }

    Task<MemoryProjectionPage> IMemoryProjectionReader.ReadAsync(MemoryProjectionQuery query, CancellationToken cancellationToken)
    {
        SemanticMemoryStorageProvider.ValidateQuery(query); cancellationToken.ThrowIfCancellationRequested();
        if (query.Scope != Scope || query.AsOfSequence > AsOfSequence || query.AsOfAt > AsOfAt)
            throw new ArgumentException("The projection query leaves its host-bound ownership or horizon.", nameof(query));
        return _storage.ReadAsync(query, cancellationToken);
    }

    Task<MemorySourceRead> IMemorySourceReader.ReadAsync(MemorySourceLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup); cancellationToken.ThrowIfCancellationRequested();
        if (lookup.Scope != Scope || lookup.Source is null || lookup.Source.Kind != "conversation" ||
            lookup.SourceKind != MemorySourceKind.Conversation || !Enum.IsDefined(lookup.Perspective) ||
            lookup.AsOfSequence < 0 || lookup.AsOfSequence > AsOfSequence || lookup.AsOfAt > AsOfAt)
            return Task.FromResult(new MemorySourceRead(MemoryReadState.Unavailable));
        try
        {
            var current = _canonical.CanonicalSource(Scope.ConversationId, (int)lookup.AsOfSequence, lookup.Source.RefId);
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null || current.Value.Header.CreatedAt > lookup.AsOfAt || current.Value.Content.Length == 0)
                return Task.FromResult(new MemorySourceRead(MemoryReadState.Unavailable));
            var perspective = current.Value.Header.Role switch
            {
                "user" => MemoryPerspective.Participant,
                "assistant" => MemoryPerspective.Agent,
                _ => throw new InvalidDataException("The canonical conversation source has an unsupported original role.")
            };
            if (perspective != lookup.Perspective) return Task.FromResult(new MemorySourceRead(MemoryReadState.Unavailable));
            var source = new MemorySource(lookup.Source, Scope, perspective, MemorySourceKind.Conversation,
                perspective == MemoryPerspective.Participant ? Scope.ParticipantId : Scope.AgentId,
                "sha256:" + current.Value.Header.ContentHash.ToLowerInvariant(), current.Value.Header.TurnSequence,
                current.Value.Header.CreatedAt, current.Value.Content);
            return Task.FromResult(new MemorySourceRead(MemoryReadState.Available, source));
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException or ExperimentException)
        { return Task.FromResult(new MemorySourceRead(MemoryReadState.Indeterminate)); }
    }
}

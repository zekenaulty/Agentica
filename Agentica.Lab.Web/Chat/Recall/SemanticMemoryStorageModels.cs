using Agentica.Memory;
using Agentica.Observations;

namespace Agentica.Lab.Web.Chat.Recall;

// These versioned Lab storage records are deliberately separate from core contracts.
// No source text, query text, provider secret or canonical conversation body is persisted here.
public sealed record SemanticMemoryStorageScope(string ScopeId, string NamespaceId,
    string ParticipantId, string AgentId, string ConversationId);
public sealed record SemanticMemoryStorageProfile(string ProfileId, string ProviderId, string ModelId,
    string ConfigurationRevision, int Dimensions, string DocumentProjectionRevision, string QueryProjectionRevision);
public sealed record SemanticMemoryStorageProjection(string ProjectionId, string ReferenceKind, string ReferenceId,
    string AuthorId, string SourceRevision, long Sequence, DateTimeOffset CreatedAt, string SourceContentHash,
    int StartUtf16, int LengthUtf16, string SliceContentHash, IReadOnlyList<float> Vector);
public sealed record SemanticMemoryStorageCoverage(bool IsComplete, int EligibleSources, int IndexedSources,
    int OmittedSources, int OmittedProjections, IReadOnlyList<string> Reasons);
public sealed record SemanticMemoryStorageExport(int SchemaVersion, string ExportId,
    SemanticMemoryStorageScope Scope, string Perspective, string SourceKind, SemanticMemoryStorageProfile Profile,
    long AsOfSequence, DateTimeOffset AsOfAt, IReadOnlyList<SemanticMemoryStorageProjection> Projections,
    SemanticMemoryStorageCoverage Coverage, long? InputTokens);

/// <summary>Explicit host mapping; computed core identities are rechecked on reconstruction.</summary>
public sealed class SemanticMemoryStorageMapper : IMemoryExportMapper<SemanticMemoryStorageExport>
{
    public SemanticMemoryStorageExport Map(MemoryIndexExport memoryExport)
    {
        var export = memoryExport;
        ArgumentNullException.ThrowIfNull(export);
        var scope = export.Scope;
        var profile = export.Profile;
        return new(1, export.ExportId,
            new(scope.ScopeId, scope.NamespaceId, scope.ParticipantId, scope.AgentId, scope.ConversationId),
            export.Perspective.ToString(), export.SourceKind.ToString(),
            new(profile.ProfileId, profile.ProviderId, profile.ModelId, profile.ConfigurationRevision,
                profile.Dimensions, profile.DocumentProjectionRevision, profile.QueryProjectionRevision),
            export.AsOfSequence, export.AsOfAt,
            Array.AsReadOnly(export.Projections.Select(item => new SemanticMemoryStorageProjection(
                item.ProjectionId, item.Source.Kind, item.Source.RefId, item.AuthorId, item.SourceRevision,
                item.Sequence, item.CreatedAt, item.SourceContentHash, item.StartUtf16, item.LengthUtf16,
                item.SliceContentHash, Array.AsReadOnly(item.Vector.ToArray()))).ToArray()),
            new(export.Coverage.IsComplete, export.Coverage.EligibleSources, export.Coverage.IndexedSources,
                export.Coverage.OmittedSources, export.Coverage.OmittedProjections,
                Array.AsReadOnly(export.Coverage.Reasons.ToArray())), export.InputTokens);
    }

    public MemoryIndexExport ToCore(SemanticMemoryStorageExport model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SchemaVersion != 1) throw new InvalidDataException("Unsupported semantic memory storage version.");
        ArgumentNullException.ThrowIfNull(model.Scope);
        ArgumentNullException.ThrowIfNull(model.Profile);
        ArgumentNullException.ThrowIfNull(model.Coverage);
        ArgumentNullException.ThrowIfNull(model.Projections);
        var scope = new MemoryScope(model.Scope.NamespaceId, model.Scope.ParticipantId,
            model.Scope.AgentId, model.Scope.ConversationId);
        var profile = new MemoryEmbeddingProfile(model.Profile.ProviderId, model.Profile.ModelId,
            model.Profile.ConfigurationRevision, model.Profile.Dimensions,
            model.Profile.DocumentProjectionRevision, model.Profile.QueryProjectionRevision);
        if (scope.ScopeId != model.Scope.ScopeId || profile.ProfileId != model.Profile.ProfileId)
            throw new InvalidDataException("Semantic memory scope or profile identity does not match its model.");
        var perspective = model.Perspective switch
        {
            nameof(MemoryPerspective.Participant) => MemoryPerspective.Participant,
            nameof(MemoryPerspective.Agent) => MemoryPerspective.Agent,
            _ => throw new InvalidDataException("Unsupported stored memory perspective.")
        };
        var kind = model.SourceKind switch
        {
            nameof(MemorySourceKind.Conversation) => MemorySourceKind.Conversation,
            nameof(MemorySourceKind.GovernedMemory) => MemorySourceKind.GovernedMemory,
            _ => throw new InvalidDataException("Unsupported stored memory source kind.")
        };
        if (model.Projections.Count > 512) throw new InvalidDataException("Semantic memory projection limit exceeded.");
        var projections = model.Projections.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            var projection = new MemoryProjection(new EvidenceRef(item.ReferenceKind, item.ReferenceId), scope,
                perspective, kind, item.AuthorId, item.SourceRevision, item.Sequence, item.CreatedAt,
                item.SourceContentHash, item.StartUtf16, item.LengthUtf16, item.SliceContentHash,
                profile.ProfileId, item.Vector);
            if (projection.ProjectionId != item.ProjectionId)
                throw new InvalidDataException("Stored semantic projection identity does not match its model.");
            return projection;
        }).ToArray();
        foreach (var source in projections.GroupBy(item => item.Source))
        {
            var first = source.First();
            if (source.Any(item => item.SourceRevision != first.SourceRevision ||
                item.SourceContentHash != first.SourceContentHash || item.Sequence != first.Sequence ||
                item.CreatedAt != first.CreatedAt || item.AuthorId != first.AuthorId))
                throw new InvalidDataException("One export cannot contain contradictory revisions of the same source.");
        }
        var coverage = new MemoryCoverage(model.Coverage.IsComplete, model.Coverage.EligibleSources,
            model.Coverage.IndexedSources, model.Coverage.OmittedSources, model.Coverage.OmittedProjections,
            model.Coverage.Reasons);
        var export = new MemoryIndexExport(scope, perspective, kind, profile, model.AsOfSequence, model.AsOfAt,
            projections, coverage, model.InputTokens);
        if (export.ExportId != model.ExportId)
            throw new InvalidDataException("Stored semantic export identity does not match its model.");
        return export;
    }
}

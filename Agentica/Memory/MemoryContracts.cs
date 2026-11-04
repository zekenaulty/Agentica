using Agentica.Observations;
using Agentica.Projection;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.Memory;

/// <summary>Exact host-bound ownership; a role label is not a participant or agent identity.</summary>
public sealed record MemoryScope
{
    public MemoryScope(string namespaceId, string participantId, string agentId, string conversationId)
    {
        NamespaceId = MemoryGuard.Id(namespaceId);
        ParticipantId = MemoryGuard.Id(participantId);
        AgentId = MemoryGuard.Id(agentId);
        ConversationId = MemoryGuard.Id(conversationId);
    }

    public string NamespaceId { get; }
    public string ParticipantId { get; }
    public string AgentId { get; }
    public string ConversationId { get; }
    public string ScopeId => MemoryGuard.Identity("scope.v1", NamespaceId, ParticipantId, AgentId, ConversationId);
}

public enum MemoryPerspective { Participant, Agent }
public enum MemorySourceKind { Conversation, GovernedMemory }
public enum MemoryEmbeddingPurpose { Document, Query }
public enum MemoryReadState { Available, Unavailable, Indeterminate }
public enum MemorySearchState { Completed, Unavailable }

/// <summary>Host-presented canonical text. Construction does not admit or promote a memory claim.</summary>
public sealed class MemorySource
{
    public MemorySource(EvidenceRef source, MemoryScope scope, MemoryPerspective perspective,
        MemorySourceKind kind, string authorId, string revision, long sequence, DateTimeOffset createdAt, string text)
    {
        Source = MemoryGuard.Reference(source);
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        MemoryGuard.Defined(perspective);
        MemoryGuard.Defined(kind);
        Perspective = perspective;
        Kind = kind;
        AuthorId = MemoryGuard.Author(scope, perspective, authorId);
        Revision = MemoryGuard.Id(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        Sequence = sequence;
        CreatedAt = createdAt.ToUniversalTime();
        Text = MemoryGuard.Text(text, MemoryGuard.MaximumSourceCharacters);
        ContentHash = MemoryGuard.ContentHash(Text);
    }

    public EvidenceRef Source { get; }
    public MemoryScope Scope { get; }
    public MemoryPerspective Perspective { get; }
    public MemorySourceKind Kind { get; }
    public string AuthorId { get; }
    public string Revision { get; }
    public long Sequence { get; }
    public DateTimeOffset CreatedAt { get; }
    public string Text { get; }
    public string ContentHash { get; }
}

/// <summary>Embedding space identity, including both document and query projection revisions.</summary>
public sealed class MemoryEmbeddingProfile
{
    public MemoryEmbeddingProfile(string providerId, string modelId, string configurationRevision,
        int dimensions, string documentProjectionRevision, string queryProjectionRevision)
    {
        ProviderId = MemoryGuard.Id(providerId);
        ModelId = MemoryGuard.Id(modelId);
        ConfigurationRevision = MemoryGuard.Id(configurationRevision);
        if (dimensions is < 1 or > MemoryGuard.MaximumDimensions)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        Dimensions = dimensions;
        DocumentProjectionRevision = MemoryGuard.Id(documentProjectionRevision);
        QueryProjectionRevision = MemoryGuard.Id(queryProjectionRevision);
        ProfileId = MemoryGuard.Identity("profile.v1", ProviderId, ModelId, ConfigurationRevision,
            Dimensions, DocumentProjectionRevision, QueryProjectionRevision);
    }

    public string ProviderId { get; }
    public string ModelId { get; }
    public string ConfigurationRevision { get; }
    public int Dimensions { get; }
    public string DocumentProjectionRevision { get; }
    public string QueryProjectionRevision { get; }
    public string ProfileId { get; }
}

/// <summary>Vectors correspond exactly to input order. Usage is optional raw provider input usage, not cost.</summary>
public sealed record MemoryEmbeddingBatch(string ProfileId, IReadOnlyList<IReadOnlyList<float>> Vectors,
    long? InputTokens = null);

public interface IMemoryEmbeddingProvider
{
    Task<MemoryEmbeddingBatch> EmbedAsync(MemoryEmbeddingProfile profile, MemoryEmbeddingPurpose purpose,
        IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

public sealed record MemoryIndexRequest(MemoryScope Scope, MemoryPerspective Perspective,
    MemorySourceKind SourceKind, MemoryEmbeddingProfile Profile, long AsOfSequence, DateTimeOffset AsOfAt,
    int ChunkCharacters = 2000, int MaxSources = 256, int MaxProjections = 512, bool SourcesComplete = true);

public sealed record MemorySearchRequest(MemoryScope Scope, MemoryPerspective Perspective,
    MemorySourceKind SourceKind, MemoryEmbeddingProfile Profile, long AsOfSequence, DateTimeOffset AsOfAt,
    string Query, int TopK = 8, int MaxCandidates = 512, int MaxCitationCharacters = 8000,
    double MinimumSimilarity = 0);

/// <summary>A disposable derived projection. It retains coordinates and hashes, never source text.</summary>
public sealed class MemoryProjection
{
    public MemoryProjection(EvidenceRef source, MemoryScope scope, MemoryPerspective perspective,
        MemorySourceKind kind, string authorId, string sourceRevision, long sequence, DateTimeOffset createdAt,
        string sourceContentHash, int startUtf16, int lengthUtf16, string sliceContentHash,
        string profileId, IReadOnlyList<float> vector)
    {
        Source = MemoryGuard.Reference(source);
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        MemoryGuard.Defined(perspective);
        MemoryGuard.Defined(kind);
        Perspective = perspective;
        Kind = kind;
        AuthorId = MemoryGuard.Author(scope, perspective, authorId);
        SourceRevision = MemoryGuard.Id(sourceRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        Sequence = sequence;
        CreatedAt = createdAt.ToUniversalTime();
        SourceContentHash = MemoryGuard.Digest(sourceContentHash);
        if (startUtf16 < 0 || lengthUtf16 is < 1 or > MemoryGuard.MaximumChunkCharacters ||
            (long)startUtf16 + lengthUtf16 > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(startUtf16));
        StartUtf16 = startUtf16;
        LengthUtf16 = lengthUtf16;
        SliceContentHash = MemoryGuard.Digest(sliceContentHash);
        ProfileId = MemoryGuard.Id(profileId);
        Vector = MemoryGuard.Vector(vector);
        ProjectionId = MemoryGuard.Identity("projection.v1", Source.Kind, Source.RefId, Scope.ScopeId,
            Perspective, Kind, AuthorId, SourceRevision, Sequence, CreatedAt, SourceContentHash,
            StartUtf16, LengthUtf16, SliceContentHash, ProfileId, Vector);
    }

    public string ProjectionId { get; }
    public EvidenceRef Source { get; }
    public MemoryScope Scope { get; }
    public MemoryPerspective Perspective { get; }
    public MemorySourceKind Kind { get; }
    public string AuthorId { get; }
    public string SourceRevision { get; }
    public long Sequence { get; }
    public DateTimeOffset CreatedAt { get; }
    public string SourceContentHash { get; }
    public int StartUtf16 { get; }
    public int LengthUtf16 { get; }
    public string SliceContentHash { get; }
    public string ProfileId { get; }
    public IReadOnlyList<float> Vector { get; }
}

/// <summary>Completeness is relative to the host-declared source/projection cut, never universal memory.</summary>
public sealed class MemoryCoverage
{
    public MemoryCoverage(bool isComplete, int eligibleSources, int indexedSources, int omittedSources,
        int omittedProjections, IReadOnlyList<string> reasons)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eligibleSources);
        ArgumentOutOfRangeException.ThrowIfNegative(indexedSources);
        ArgumentOutOfRangeException.ThrowIfNegative(omittedSources);
        ArgumentOutOfRangeException.ThrowIfNegative(omittedProjections);
        var reasonCopy = MemoryGuard.Snapshot(reasons, 32);
        IsComplete = isComplete && omittedSources == 0 && omittedProjections == 0;
        EligibleSources = eligibleSources;
        IndexedSources = indexedSources;
        OmittedSources = omittedSources;
        OmittedProjections = omittedProjections;
        Reasons = Array.AsReadOnly(reasonCopy.Select(MemoryGuard.Id).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray());
    }

    public bool IsComplete { get; }
    public int EligibleSources { get; }
    public int IndexedSources { get; }
    public int OmittedSources { get; }
    /// <summary>Known omissions; a lower bound when a source/projection work limit prevents scanning the remaining tail.</summary>
    public int OmittedProjections { get; }
    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>Public constructors support host serialization/reconstruction; all retained collections are copied.</summary>
public sealed class MemoryIndexExport
{
    public MemoryIndexExport(MemoryScope scope, MemoryPerspective perspective, MemorySourceKind sourceKind,
        MemoryEmbeddingProfile profile, long asOfSequence, DateTimeOffset asOfAt,
        IReadOnlyList<MemoryProjection> projections, MemoryCoverage coverage, long? inputTokens = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        MemoryGuard.Defined(perspective);
        MemoryGuard.Defined(sourceKind);
        Perspective = perspective;
        SourceKind = sourceKind;
        ArgumentOutOfRangeException.ThrowIfNegative(asOfSequence);
        AsOfSequence = asOfSequence;
        AsOfAt = asOfAt.ToUniversalTime();
        var copy = MemoryGuard.Snapshot(projections, MemoryGuard.MaximumProjections)
            .Select(item => item ?? throw new ArgumentException("Null projection.", nameof(projections)))
            .OrderBy(item => item.ProjectionId, StringComparer.Ordinal).ToArray();
        if (copy.Select(item => item.ProjectionId).Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            copy.Any(item => item.Scope != scope || item.Perspective != perspective || item.Kind != sourceKind ||
                item.ProfileId != profile.ProfileId || item.Vector.Count != profile.Dimensions ||
                item.Sequence > asOfSequence || item.CreatedAt > AsOfAt))
            throw new ArgumentException("Export projections do not belong to its exact scope, profile and cut.", nameof(projections));
        Projections = Array.AsReadOnly(copy);
        if (inputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        InputTokens = inputTokens;
        ExportId = MemoryGuard.Identity("export.v1", Scope.ScopeId, Perspective, SourceKind, Profile.ProfileId,
            AsOfSequence, AsOfAt, Projections.Select(item => item.ProjectionId).ToArray(), Coverage, InputTokens);
    }

    public int SchemaVersion => 1;
    public string ExportId { get; }
    public MemoryScope Scope { get; }
    public MemoryPerspective Perspective { get; }
    public MemorySourceKind SourceKind { get; }
    public MemoryEmbeddingProfile Profile { get; }
    public long AsOfSequence { get; }
    public DateTimeOffset AsOfAt { get; }
    public IReadOnlyList<MemoryProjection> Projections { get; }
    public MemoryCoverage Coverage { get; }
    public long? InputTokens { get; }
}

public sealed record MemoryLinkedProjection(string ExportId, MemoryProjection Projection);
public sealed record MemoryProjectionQuery(MemoryScope Scope, MemoryPerspective Perspective,
    MemorySourceKind SourceKind, string ProfileId, long AsOfSequence, DateTimeOffset AsOfAt, int Limit);

/// <summary>Readers must filter to the bound query before enumeration and declare incomplete scans honestly.</summary>
public interface IMemoryProjectionReader
{
    Task<MemoryProjectionPage> ReadAsync(MemoryProjectionQuery query, CancellationToken cancellationToken = default);
}

public sealed record MemoryProjectionPage(IReadOnlyList<MemoryLinkedProjection> Items, bool IsComplete);
public sealed record MemorySourceLookup(EvidenceRef Source, MemoryScope Scope,
    MemoryPerspective Perspective, MemorySourceKind SourceKind, long AsOfSequence, DateTimeOffset AsOfAt);

/// <summary>The host rechecks current deletion, authorization, visibility and other eligibility on every read.</summary>
public interface IMemorySourceReader
{
    Task<MemorySourceRead> ReadAsync(MemorySourceLookup lookup, CancellationToken cancellationToken = default);
}

public sealed record MemorySourceRead(MemoryReadState State, MemorySource? Source = null);
public sealed record MemorySearchHit(string ExportId, string ProjectionId, double Similarity,
    MemoryScope Scope, MemoryPerspective Perspective, MemorySourceKind SourceKind, string SourceContentHash,
    PresentedEvidenceSegment Segment, PresentedEvidenceCitation Citation);
public sealed record MemorySearchResult(MemorySearchState State, string ScopeId, string ProfileId,
    IReadOnlyList<MemorySearchHit> Hits, MemoryCoverage Coverage, bool Truncated, long? InputTokens = null)
{
    /// <summary>Valid unique projections in the exact bound scope/profile offered to cosine scoring.</summary>
    public int CandidateCount { get; init; }
    /// <summary>Attempted current canonical-source reads, including unavailable or rejected reads.</summary>
    public int InspectedCount { get; init; }
}

internal static class MemoryGuard
{
    internal const int MaximumDimensions = 8192;
    internal const int MaximumSourceCharacters = 1_048_576;
    internal const int MaximumChunkCharacters = 32_768;
    internal const int MaximumProjections = 512;
    internal const int MaximumInputCharacters = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SearchValues<char> DigestCharacters = SearchValues.Create("0123456789abcdef");

    internal static string Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("An exact bounded identity is required.");
        Unicode(value);
        return value;
    }

    internal static string Text(string value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum)
            throw new ArgumentException("Source or query text is empty or exceeds its bound.");
        Unicode(value);
        return value;
    }

    internal static void Unicode(string value)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException exception) { throw new ArgumentException("Text must contain valid UTF-16.", exception); }
    }

    internal static void Defined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("Undefined memory contract state.");
    }

    internal static string Author(MemoryScope scope, MemoryPerspective perspective, string authorId)
    {
        var id = Id(authorId);
        if (id != (perspective == MemoryPerspective.Participant ? scope.ParticipantId : scope.AgentId))
            throw new ArgumentException("Attribution must identify the bound participant or agent.", nameof(authorId));
        return id;
    }

    internal static EvidenceRef Reference(EvidenceRef source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(Id(source.Kind), Id(source.RefId));
    }

    internal static string ContentHash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(text)));
    internal static string Identity(params object?[] parts) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts)));

    internal static string Digest(string value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept(DigestCharacters))
            throw new ArgumentException("An exact SHA-256 content digest is required.");
        return value;
    }

    internal static IReadOnlyList<float> Vector(IReadOnlyList<float> values, int? dimensions = null)
    {
        var copy = Snapshot(values, MaximumDimensions);
        if (copy.Length < 1 || dimensions is { } size && copy.Length != size)
            throw new ArgumentException("Embedding dimension mismatch.");
        if (copy.Any(value => !float.IsFinite(value)) || !copy.Any(value => value != 0))
            throw new ArgumentException("Embedding vectors must be finite and nonzero.");
        return Array.AsReadOnly(copy);
    }

    /// <summary>Never trusts Count to bound enumeration, allocation or the retained snapshot.</summary>
    internal static T[] Snapshot<T>(IReadOnlyList<T> values, int maximum)
    {
        ArgumentNullException.ThrowIfNull(values);
        var declaredCount = values.Count;
        if (declaredCount < 0 || declaredCount > maximum)
            throw new ArgumentException("Collection exceeds its allowed bound.", nameof(values));
        var copy = new List<T>(declaredCount);
        foreach (var value in values)
        {
            // At most the declared count plus one MoveNext can succeed, even for an infinite source.
            if (copy.Count >= declaredCount || copy.Count >= maximum)
                throw new ArgumentException("Collection enumeration exceeds its declared count.", nameof(values));
            copy.Add(value);
        }
        if (copy.Count != declaredCount)
            throw new ArgumentException("Collection enumeration differs from its declared count.", nameof(values));
        return copy.ToArray();
    }
}

using Agentica.Observations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.Projection;

/// <summary>Host identity, independent of provider, realization and activation identity.</summary>
public sealed record ObligationIdentity(
    string ObligationId,
    string GoverningObjectiveId,
    string AcceptanceContractId,
    string AcceptanceRevision,
    string SubjectId,
    string SubjectRevision,
    string? ParentObligationId = null);

public enum ObligationCoverageStatus { Unknown, Partial, Complete }

/// <summary>Coverage of the host's declared required horizon, not of every possible source.</summary>
public sealed record ObligationCoverage(
    ObligationCoverageStatus Status,
    string Horizon,
    string? CorpusRevision = null);

/// <summary>Exact host-presented text at a UTF-16 offset in one attributable source revision.</summary>
public sealed record PresentedEvidenceSegment(
    string SegmentId,
    EvidenceRef Source,
    string SourceRevision,
    int StartUtf16,
    string Text,
    string AttributionId);

/// <summary>Absolute UTF-16 source coordinates wholly inside one presented segment.</summary>
public sealed record PresentedEvidenceCitation(string SegmentId, int StartUtf16, int LengthUtf16);

public enum ObligationSatisfaction { Unsatisfied, PartiallySatisfied, Satisfied, Indeterminate }
public enum ObligationResolutionState { Ready, Pending, Unavailable, Indeterminate }
public enum ObligationExecutionState { NotAttempted, Active, Succeeded, Failed, Cancelled, OutcomeUnknown }
public enum ObligationEffectCustody { NotAttempted, Admitted, Confirmed, Failed, OutcomeUnknown }

/// <summary>References host-owned original custody; this contract does not execute or reconcile it.</summary>
public sealed record ObligationAttemptReference(
    string AttemptId,
    string OwnerId,
    string RealizationId,
    ObligationExecutionState Execution,
    ObligationEffectCustody EffectCustody);

/// <summary>
/// Bounded immutable input declaration for one interpretation. The host must actually present
/// these exact segments and resolve current source eligibility; this object proves neither.
/// </summary>
public sealed class ObligationInputManifest
{
    public const int SchemaVersion = 1;
    public const int MaximumSegments = 128;
    public const int MaximumSegmentCharacters = 32_768;
    public const int MaximumPresentedCharacters = 131_072;
    private readonly IReadOnlyDictionary<string, PresentedEvidenceSegment> _byId;

    public ObligationInputManifest(
        ObligationIdentity obligation,
        string question,
        string scopeId,
        string perspectiveId,
        IReadOnlyList<PresentedEvidenceSegment> presentedEvidence,
        ObligationCoverage coverage,
        string compilerRevision,
        string inputSchemaRevision,
        string outputSchemaRevision,
        string generationPolicyRevision)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        ArgumentNullException.ThrowIfNull(presentedEvidence);
        ArgumentNullException.ThrowIfNull(coverage);
        Obligation = ObligationContractGuard.Identity(obligation);
        Question = ObligationContractGuard.Text(question, 8192, nameof(question));
        ScopeId = ObligationContractGuard.Identifier(scopeId, nameof(scopeId));
        PerspectiveId = ObligationContractGuard.Identifier(perspectiveId, nameof(perspectiveId));
        CompilerRevision = ObligationContractGuard.Identifier(compilerRevision, nameof(compilerRevision));
        InputSchemaRevision = ObligationContractGuard.Identifier(inputSchemaRevision, nameof(inputSchemaRevision));
        OutputSchemaRevision = ObligationContractGuard.Identifier(outputSchemaRevision, nameof(outputSchemaRevision));
        GenerationPolicyRevision = ObligationContractGuard.Identifier(generationPolicyRevision, nameof(generationPolicyRevision));
        ObligationContractGuard.Defined(coverage.Status, nameof(coverage));
        Coverage = new(coverage.Status,
            ObligationContractGuard.Text(coverage.Horizon, 4096, nameof(coverage)),
            coverage.CorpusRevision is null ? null : ObligationContractGuard.Identifier(coverage.CorpusRevision, nameof(coverage)));
        if (presentedEvidence.Count > MaximumSegments)
            throw new ArgumentException("Presented evidence exceeds the segment limit.", nameof(presentedEvidence));

        var copies = new List<PresentedEvidenceSegment>(presentedEvidence.Count);
        var index = new Dictionary<string, PresentedEvidenceSegment>(StringComparer.Ordinal);
        var characters = 0;
        foreach (var segment in presentedEvidence)
        {
            if (copies.Count >= MaximumSegments)
                throw new ArgumentException("Presented evidence exceeds the segment limit.", nameof(presentedEvidence));
            ArgumentNullException.ThrowIfNull(segment);
            ArgumentNullException.ThrowIfNull(segment.Source);
            var id = ObligationContractGuard.Identifier(segment.SegmentId, nameof(presentedEvidence));
            var source = new EvidenceRef(
                ObligationContractGuard.Identifier(segment.Source.Kind, nameof(presentedEvidence)),
                ObligationContractGuard.Identifier(segment.Source.RefId, nameof(presentedEvidence)));
            var text = ObligationContractGuard.Text(segment.Text, MaximumSegmentCharacters, nameof(presentedEvidence));
            ArgumentOutOfRangeException.ThrowIfNegative(segment.StartUtf16);
            if ((long)segment.StartUtf16 + text.Length > int.MaxValue)
                throw new ArgumentException("Presented source range overflows UTF-16 coordinates.", nameof(presentedEvidence));
            characters += text.Length;
            if (characters > MaximumPresentedCharacters)
                throw new ArgumentException("Presented evidence exceeds the aggregate character limit.", nameof(presentedEvidence));
            var copy = new PresentedEvidenceSegment(id, source,
                ObligationContractGuard.Identifier(segment.SourceRevision, nameof(presentedEvidence)),
                segment.StartUtf16, text,
                ObligationContractGuard.Identifier(segment.AttributionId, nameof(presentedEvidence)));
            if (!index.TryAdd(id, copy))
                throw new ArgumentException("Presented segment identities must be unique.", nameof(presentedEvidence));
            copies.Add(copy);
        }

        PresentedEvidence = Array.AsReadOnly(copies.ToArray());
        _byId = index;
        ContentIdentity = ComputeContentIdentity();
    }

    public ObligationIdentity Obligation { get; }
    public string Question { get; }
    public string ScopeId { get; }
    public string PerspectiveId { get; }
    public IReadOnlyList<PresentedEvidenceSegment> PresentedEvidence { get; }
    public ObligationCoverage Coverage { get; }
    public string CompilerRevision { get; }
    public string InputSchemaRevision { get; }
    public string OutputSchemaRevision { get; }
    public string GenerationPolicyRevision { get; }

    /// <summary>
    /// Identity of interpretation inputs, not a consumer acceptance receipt or authorization.
    /// Excludes obligation/objective/parent/activation identities and consumer acceptance revision.
    /// </summary>
    public string ContentIdentity { get; }

    public string ReadCitation(PresentedEvidenceCitation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        if (citation.SegmentId is null || !_byId.TryGetValue(citation.SegmentId, out var segment))
            throw new ArgumentException("Citation does not resolve to presented evidence.", nameof(citation));
        var offset = (long)citation.StartUtf16 - segment.StartUtf16;
        if (offset < 0 || citation.LengthUtf16 <= 0 || offset + citation.LengthUtf16 > segment.Text.Length)
            throw new ArgumentException("Citation range leaves the presented segment.", nameof(citation));
        var text = segment.Text.Substring((int)offset, citation.LengthUtf16);
        ObligationContractGuard.Unicode(text, nameof(citation));
        return text;
    }

    private string ComputeContentIdentity()
    {
        // Fixed property and segment order; exact text is digested without normalization.
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("subjectId", Obligation.SubjectId);
            writer.WriteString("subjectRevision", Obligation.SubjectRevision);
            writer.WriteString("question", Question);
            writer.WriteString("scopeId", ScopeId);
            writer.WriteString("perspectiveId", PerspectiveId);
            writer.WriteString("compilerRevision", CompilerRevision);
            writer.WriteString("inputSchemaRevision", InputSchemaRevision);
            writer.WriteString("outputSchemaRevision", OutputSchemaRevision);
            writer.WriteString("generationPolicyRevision", GenerationPolicyRevision);
            writer.WriteString("coverage", Coverage.Status.ToString());
            writer.WriteString("horizon", Coverage.Horizon);
            writer.WriteString("corpusRevision", Coverage.CorpusRevision);
            writer.WriteStartArray("presentedEvidence");
            foreach (var segment in PresentedEvidence)
            {
                writer.WriteStartObject();
                writer.WriteString("sourceKind", segment.Source.Kind);
                writer.WriteString("sourceId", segment.Source.RefId);
                writer.WriteString("sourceRevision", segment.SourceRevision);
                writer.WriteString("attributionId", segment.AttributionId);
                writer.WriteNumber("startUtf16", segment.StartUtf16);
                writer.WriteNumber("lengthUtf16", segment.Text.Length);
                writer.WriteString("textSha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(segment.Text))));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return "sha256-v1:" + Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}

/// <summary>
/// A frozen host-declared fulfillment. Structural checks do not replace the host's semantic
/// acceptance evaluator, current authority checks or authoritative attempt/result ledger.
/// </summary>
public sealed class ObligationFulfillment
{
    public const int SchemaVersion = 1;
    public const int MaximumReferences = 128;

    public ObligationFulfillment(
        ObligationInputManifest manifest,
        ObligationSatisfaction satisfaction,
        ObligationResolutionState resolution,
        IReadOnlyList<PresentedEvidenceCitation> citations,
        IReadOnlyList<string> residualRequirements,
        IReadOnlyList<ObligationAttemptReference>? attempts = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(residualRequirements);
        attempts ??= [];
        ObligationContractGuard.Defined(satisfaction, nameof(satisfaction));
        ObligationContractGuard.Defined(resolution, nameof(resolution));
        if (citations.Count > MaximumReferences || residualRequirements.Count > MaximumReferences ||
            attempts.Count > MaximumReferences)
            throw new ArgumentException("Fulfillment exceeds its reference limits.");

        var citationCopies = new List<PresentedEvidenceCitation>(citations.Count);
        foreach (var citation in citations)
        {
            if (citationCopies.Count >= MaximumReferences)
                throw new ArgumentException("Citations exceed the reference limit.", nameof(citations));
            _ = manifest.ReadCitation(citation);
            citationCopies.Add(new(citation.SegmentId, citation.StartUtf16, citation.LengthUtf16));
        }
        var residualCopies = new List<string>(residualRequirements.Count);
        var residualIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in residualRequirements)
        {
            if (residualCopies.Count >= MaximumReferences)
                throw new ArgumentException("Residual requirements exceed the reference limit.", nameof(residualRequirements));
            var id = ObligationContractGuard.Identifier(value, nameof(residualRequirements));
            if (!residualIds.Add(id))
                throw new ArgumentException("Residual requirement identities must be unique.", nameof(residualRequirements));
            residualCopies.Add(id);
        }

        var attemptCopies = new List<ObligationAttemptReference>(attempts.Count);
        var attemptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (attemptCopies.Count >= MaximumReferences)
                throw new ArgumentException("Attempts exceed the reference limit.", nameof(attempts));
            ArgumentNullException.ThrowIfNull(attempt);
            ObligationContractGuard.Defined(attempt.Execution, nameof(attempts));
            ObligationContractGuard.Defined(attempt.EffectCustody, nameof(attempts));
            var copy = new ObligationAttemptReference(
                ObligationContractGuard.Identifier(attempt.AttemptId, nameof(attempts)),
                ObligationContractGuard.Identifier(attempt.OwnerId, nameof(attempts)),
                ObligationContractGuard.Identifier(attempt.RealizationId, nameof(attempts)),
                attempt.Execution, attempt.EffectCustody);
            if (!attemptIds.Add(copy.AttemptId))
                throw new ArgumentException("Original attempt identities must be unique.", nameof(attempts));
            if (copy.Execution == ObligationExecutionState.NotAttempted &&
                copy.EffectCustody != ObligationEffectCustody.NotAttempted)
                throw new ArgumentException("An unattempted execution cannot claim an effect.", nameof(attempts));
            attemptCopies.Add(copy);
        }

        if (satisfaction == ObligationSatisfaction.Satisfied)
        {
            if (resolution != ObligationResolutionState.Ready ||
                manifest.Coverage.Status != ObligationCoverageStatus.Complete ||
                citationCopies.Count == 0 || residualCopies.Count != 0 ||
                attemptCopies.Any(attempt =>
                    attempt.Execution is ObligationExecutionState.Active or ObligationExecutionState.OutcomeUnknown ||
                    attempt.EffectCustody is ObligationEffectCustody.Admitted or ObligationEffectCustody.OutcomeUnknown))
                throw new ArgumentException("Satisfied requires ready, complete evidence with no residual or unresolved original attempt.");
        }
        else if (residualCopies.Count == 0)
        {
            throw new ArgumentException("Unfulfilled work must retain at least one residual requirement.", nameof(residualRequirements));
        }

        Manifest = manifest;
        Satisfaction = satisfaction;
        Resolution = resolution;
        Citations = Array.AsReadOnly(citationCopies.ToArray());
        ResidualRequirements = Array.AsReadOnly(residualCopies.ToArray());
        Attempts = Array.AsReadOnly(attemptCopies.ToArray());
    }

    public ObligationInputManifest Manifest { get; }
    public ObligationIdentity Obligation => Manifest.Obligation;
    public ObligationSatisfaction Satisfaction { get; }
    public ObligationResolutionState Resolution { get; }
    public IReadOnlyList<PresentedEvidenceCitation> Citations { get; }
    public IReadOnlyList<string> ResidualRequirements { get; }
    public IReadOnlyList<ObligationAttemptReference> Attempts { get; }
}

internal static class ObligationContractGuard
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Identifier(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("A bounded exact identifier is required.", parameter);
        Unicode(value, parameter);
        return value;
    }

    public static string Text(string? value, int maximum, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Nonempty text exceeds its allowed bounds or is missing.", parameter);
        Unicode(value, parameter);
        return value;
    }

    public static void Defined<T>(T value, string parameter) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentException("An undefined contract state is not permitted.", parameter);
    }

    public static ObligationIdentity Identity(ObligationIdentity identity)
    {
        if (identity.ParentObligationId == identity.ObligationId)
            throw new ArgumentException("An obligation cannot parent itself.", nameof(identity));
        return new(
            Identifier(identity.ObligationId, nameof(identity)),
            Identifier(identity.GoverningObjectiveId, nameof(identity)),
            Identifier(identity.AcceptanceContractId, nameof(identity)),
            Identifier(identity.AcceptanceRevision, nameof(identity)),
            Identifier(identity.SubjectId, nameof(identity)),
            Identifier(identity.SubjectRevision, nameof(identity)),
            identity.ParentObligationId is null ? null : Identifier(identity.ParentObligationId, nameof(identity)));
    }

    public static void Unicode(string value, string parameter)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Contract text requires valid UTF-16.", parameter);
        }
    }
}


using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;

namespace Agentica.Lab.Scenarios.Campaign;

public sealed record CampaignDefinition(
    string CampaignId,
    string Title,
    string Goal,
    IReadOnlyList<CampaignMilestone> Milestones,
    IReadOnlyList<CampaignRequiredEvidence> DefinitionOfDone);

public sealed record CampaignMilestone(
    string MilestoneId,
    string Objective,
    IReadOnlyList<string> DependsOn,
    bool Optional,
    int Priority,
    RunOutcomeStatus RequiredOutcomeStatus,
    IReadOnlyList<CampaignRequiredEvidence> RequiredEvidence,
    IReadOnlyDictionary<string, object?> ContextProjection);

public sealed record CampaignRequiredEvidence(
    CampaignEvidenceKind Kind,
    string? ArtifactKind = null,
    string? ToolId = null,
    ReceiptStatus? ReceiptStatus = null,
    string? HostStateKey = null,
    object? HostStateValue = null);

public enum CampaignEvidenceKind
{
    Artifact,
    Receipt,
    HostState
}

public sealed record CampaignPriorRunRef(
    string MilestoneId,
    string RunId,
    RunOutcomeStatus Status,
    IReadOnlyList<EvidenceRef> Evidence);

public sealed record CampaignFact(
    string FactId,
    string Summary,
    IReadOnlyList<EvidenceRef> Evidence);

public sealed record CampaignProgressSnapshot(
    string CampaignId,
    string Goal,
    string? CurrentMilestoneId,
    IReadOnlyList<string> CompletedMilestones,
    IReadOnlyList<CampaignFact> ProvenFacts,
    IReadOnlyList<string> OutstandingFacts,
    IReadOnlyList<EvidenceRef> ArtifactRefs,
    IReadOnlyList<EvidenceRef> ReceiptRefs,
    IReadOnlyList<string> Blockers,
    IReadOnlyDictionary<string, object?> HostStateProjection);

public sealed record CampaignWorkingContextSnapshot(
    string CampaignId,
    string Scope,
    string ActivePlanSummary,
    IReadOnlyList<CampaignFact> ProvenFacts,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> KnownBlockers,
    IReadOnlyList<string> NextConsiderations,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    DateTimeOffset UpdatedAt);

public enum CampaignRunStatus
{
    Ready,
    Running,
    Succeeded,
    Blocked,
    Failed
}

public sealed class CampaignState
{
    public CampaignState(CampaignDefinition definition)
    {
        ProgressSnapshot = CampaignProgressSnapshotCompiler.Initial(definition, new Dictionary<string, object?>(StringComparer.Ordinal));
        WorkingContext = CampaignWorkingContextCompiler.Initial(definition, ProgressSnapshot);
    }

    public string? ActiveMilestoneId { get; set; }

    public List<string> CompletedMilestones { get; } = [];

    public List<string> BlockedMilestones { get; } = [];

    public List<string> AvailableMilestones { get; } = [];

    public List<CampaignPriorRunRef> PriorRunRefs { get; } = [];

    public CampaignProgressSnapshot ProgressSnapshot { get; set; }

    public CampaignWorkingContextSnapshot WorkingContext { get; set; }

    public CampaignRunStatus Status { get; set; } = CampaignRunStatus.Ready;
}

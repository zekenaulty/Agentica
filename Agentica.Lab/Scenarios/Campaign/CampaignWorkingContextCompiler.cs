using Agentica.Observations;
using Agentica.Outcomes;

namespace Agentica.Lab.Scenarios.Campaign;

public static class CampaignWorkingContextCompiler
{
    private const int MaxFacts = 12;
    private const int MaxHypotheses = 8;
    private const int MaxOpenQuestions = 8;
    private const int MaxBlockers = 8;
    private const int MaxNextConsiderations = 8;
    private const int MaxEvidenceRefs = 24;
    private const int MaxTextLength = 180;

    public static CampaignWorkingContextSnapshot Initial(
        CampaignDefinition definition,
        CampaignProgressSnapshot progressSnapshot) =>
        Compile(
            definition,
            null,
            progressSnapshot,
            null,
            []);

    public static CampaignWorkingContextSnapshot Compile(
        CampaignDefinition definition,
        CampaignMilestone? activeMilestone,
        CampaignProgressSnapshot progressSnapshot,
        OutcomeEnvelope? latestEnvelope,
        IReadOnlyList<string> acceptanceBlockers)
    {
        var facts = progressSnapshot.ProvenFacts
            .Where(fact => fact.Evidence.Count > 0 || fact.FactId.StartsWith("host.", StringComparison.Ordinal))
            .TakeLast(MaxFacts)
            .ToArray();
        var evidenceRefs = facts
            .SelectMany(fact => fact.Evidence)
            .Concat(progressSnapshot.ArtifactRefs)
            .Concat(progressSnapshot.ReceiptRefs)
            .DistinctBy(evidence => $"{evidence.Kind}:{evidence.RefId}")
            .TakeLast(MaxEvidenceRefs)
            .ToArray();
        var openQuestions = progressSnapshot.OutstandingFacts
            .Select(item => $"What evidence is still needed for: {item}")
            .Select(Trim)
            .Take(MaxOpenQuestions)
            .ToArray();
        var blockers = acceptanceBlockers
            .Concat(progressSnapshot.Blockers)
            .Where(blocker => !string.IsNullOrWhiteSpace(blocker))
            .Select(Trim)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxBlockers)
            .ToArray();
        var nextConsiderations = definition.Milestones
            .Where(milestone => !progressSnapshot.CompletedMilestones.Contains(milestone.MilestoneId, StringComparer.Ordinal))
            .OrderBy(milestone => milestone.Priority)
            .Select(milestone => $"Consider milestone '{milestone.MilestoneId}': {milestone.Objective}")
            .Select(Trim)
            .Take(MaxNextConsiderations)
            .ToArray();
        var hypotheses = latestEnvelope?.Outcome.Status == RunOutcomeStatus.Blocked
            ? blockers.Select(blocker => $"Blocked state may require another evidence-gathering run: {blocker}")
                .Select(Trim)
                .Take(MaxHypotheses)
                .ToArray()
            : [];

        return new CampaignWorkingContextSnapshot(
            definition.CampaignId,
            Scope: activeMilestone is null
                ? "campaign"
                : $"campaign.milestone.{activeMilestone.MilestoneId}",
            ActivePlanSummary: BuildPlanSummary(definition, activeMilestone, progressSnapshot),
            ProvenFacts: facts,
            Hypotheses: hypotheses,
            OpenQuestions: openQuestions,
            KnownBlockers: blockers,
            NextConsiderations: nextConsiderations,
            EvidenceRefs: evidenceRefs,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static string BuildPlanSummary(
        CampaignDefinition definition,
        CampaignMilestone? activeMilestone,
        CampaignProgressSnapshot progressSnapshot)
    {
        var completed = progressSnapshot.CompletedMilestones.Count;
        var required = definition.Milestones.Count(milestone => !milestone.Optional);
        var current = activeMilestone is null
            ? "No active milestone."
            : $"Active milestone '{activeMilestone.MilestoneId}': {activeMilestone.Objective}";
        return Trim($"{current} Required progress: {completed}/{required} required milestones completed.");
    }

    private static string Trim(string value) =>
        value.Length <= MaxTextLength
            ? value
            : value[..MaxTextLength];
}

using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;

namespace Agentica.Orchestration.Acceptance;

public sealed class EvidenceTaskAcceptanceEvaluator : ITaskAcceptanceEvaluator
{
    public Task<TaskAcceptanceResult> EvaluateAsync(
        TaskNode task,
        OutcomeEnvelope outcome,
        TaskAcceptanceContext context,
        CancellationToken cancellationToken = default)
    {
        var blockers = new List<string>();
        var evidence = new List<EvidenceRef>();

        foreach (var requirement in task.AcceptanceRequirements)
        {
            switch (requirement.Kind)
            {
                case TaskAcceptanceRequirementKind.OutcomeStatus:
                    if (requirement.RequiredOutcomeStatus.HasValue &&
                        outcome.Outcome.Status != requirement.RequiredOutcomeStatus.Value)
                    {
                        blockers.Add($"Outcome status expected {requirement.RequiredOutcomeStatus.Value} actual {outcome.Outcome.Status}.");
                    }
                    else
                    {
                        evidence.AddRange(outcome.Outcome.CompletionEvidence);
                    }

                    break;

                case TaskAcceptanceRequirementKind.Artifact:
                    var artifact = outcome.Details.Artifacts.FirstOrDefault(item =>
                        string.Equals(item.Kind, requirement.ArtifactKind, StringComparison.Ordinal));
                    if (artifact is null)
                    {
                        blockers.Add($"Missing artifact kind '{requirement.ArtifactKind}'.");
                    }
                    else
                    {
                        evidence.Add(new EvidenceRef("artifact", artifact.ArtifactId));
                    }

                    break;

                case TaskAcceptanceRequirementKind.Receipt:
                    var receipt = outcome.Receipts.Items.FirstOrDefault(item =>
                        string.Equals(item.ToolId, requirement.ToolId, StringComparison.Ordinal) &&
                        item.Status == ReceiptStatus.Succeeded);
                    if (receipt is null)
                    {
                        blockers.Add($"Missing succeeded receipt for tool '{requirement.ToolId}'.");
                    }
                    else
                    {
                        evidence.Add(new EvidenceRef("receipt", receipt.ReceiptId));
                    }

                    break;

                case TaskAcceptanceRequirementKind.HostState:
                    if (string.IsNullOrWhiteSpace(requirement.HostStateKey) ||
                        !context.HostState.TryGetValue(requirement.HostStateKey, out var value) ||
                        !HostValueEquals(value, requirement.HostStateValue))
                    {
                        blockers.Add($"Host state '{requirement.HostStateKey}' did not satisfy task requirement.");
                    }

                    break;
            }
        }

        if (blockers.Count == 0)
        {
            return Task.FromResult(new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                evidence
                    .DistinctBy(item => $"{item.Kind}:{item.RefId}")
                    .ToArray()));
        }

        var status = outcome.Outcome.Status switch
        {
            RunOutcomeStatus.Blocked => TaskAcceptanceStatus.Blocked,
            RunOutcomeStatus.Failed or RunOutcomeStatus.PlanInvalid or RunOutcomeStatus.Cancelled => TaskAcceptanceStatus.Rejected,
            RunOutcomeStatus.PartiallyComplete => TaskAcceptanceStatus.PartiallyAccepted,
            _ => TaskAcceptanceStatus.PartiallyAccepted
        };

        return Task.FromResult(new TaskAcceptanceResult(
            status,
            blockers,
            evidence
                .DistinctBy(item => $"{item.Kind}:{item.RefId}")
                .ToArray()));
    }

    private static bool HostValueEquals(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return string.Equals(actual.ToString(), expected.ToString(), StringComparison.Ordinal);
    }
}

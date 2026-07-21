using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;

namespace Agentica.Orchestration.Acceptance;

public static class DefinitionOfDoneEvaluator
{
    public static DefinitionOfDoneResult Evaluate(
        TaskGraphPlan plan,
        OrchestrationState state,
        IReadOnlyList<OutcomeEnvelope> outcomes,
        IReadOnlyDictionary<string, object?> hostState)
    {
        try
        {
            TaskGraphValidator.ValidateRequirements(plan.DefinitionOfDone, "Task graph definition of done");
        }
        catch (TaskGraphValidationException exception)
        {
            return new DefinitionOfDoneResult(false, [exception.Message], []);
        }

        var reasons = new List<string>();
        var evidence = new List<EvidenceRef>();
        var acceptedRunIds = state.RunRefs
            .Select(runRef => runRef.RunId)
            .ToHashSet(StringComparer.Ordinal);
        var attempts = outcomes
            .Where(outcome => acceptedRunIds.Contains(outcome.Outcome.RunId))
            .SelectMany(AcceptanceEvidenceResolver.Attempts)
            .ToArray();

        foreach (var requirement in plan.DefinitionOfDone)
        {
            switch (requirement.Kind)
            {
                case TaskAcceptanceRequirementKind.OutcomeStatus:
                    EvaluateOutcomeStatus(plan, state, requirement.RequiredOutcomeStatus!.Value, reasons, evidence);
                    break;

                case TaskAcceptanceRequirementKind.Artifact:
                    var artifacts = attempts
                        .SelectMany(outcome => outcome.Details.Artifacts)
                        .Where(artifact => string.Equals(
                            artifact.Kind,
                            requirement.ArtifactKind,
                            StringComparison.Ordinal))
                        .ToArray();
                    if (artifacts.Length == 0)
                    {
                        reasons.Add($"Definition of done is missing artifact kind '{requirement.ArtifactKind}'.");
                    }
                    else
                    {
                        evidence.AddRange(artifacts.Select(artifact => new EvidenceRef("artifact", artifact.ArtifactId)));
                    }

                    break;

                case TaskAcceptanceRequirementKind.Receipt:
                    var receipts = attempts
                        .SelectMany(outcome => outcome.Receipts.Items)
                        .Where(receipt =>
                            string.Equals(receipt.ToolId, requirement.ToolId, StringComparison.Ordinal) &&
                            receipt.Status == ReceiptStatus.Succeeded)
                        .ToArray();
                    if (receipts.Length == 0)
                    {
                        reasons.Add($"Definition of done is missing a succeeded receipt for tool '{requirement.ToolId}'.");
                    }
                    else
                    {
                        evidence.AddRange(receipts.Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId)));
                    }

                    break;

                case TaskAcceptanceRequirementKind.HostState:
                    if (string.IsNullOrWhiteSpace(requirement.HostStateKey) ||
                        !hostState.TryGetValue(requirement.HostStateKey, out var actual) ||
                        !StructuralValueEquality.AreEqual(actual, requirement.HostStateValue))
                    {
                        reasons.Add($"Host state '{requirement.HostStateKey}' did not satisfy the definition of done.");
                    }
                    else
                    {
                        evidence.Add(new EvidenceRef("host_state", requirement.HostStateKey));
                    }

                    break;
            }
        }

        return new DefinitionOfDoneResult(
            reasons.Count == 0,
            reasons,
            evidence.Distinct().ToArray());
    }

    private static void EvaluateOutcomeStatus(
        TaskGraphPlan plan,
        OrchestrationState state,
        RunOutcomeStatus requiredStatus,
        ICollection<string> reasons,
        ICollection<EvidenceRef> evidence)
    {
        var tasks = plan.Tasks.Where(task => !task.Optional).ToArray();
        if (tasks.Length == 0)
        {
            tasks = plan.Tasks
                .Where(task => state.CompletedTaskIds.Contains(task.TaskId, StringComparer.Ordinal))
                .ToArray();
        }

        if (tasks.Length == 0)
        {
            reasons.Add($"Definition of done outcome status '{requiredStatus}' has no completed task run to verify.");
            return;
        }

        foreach (var task in tasks)
        {
            var run = state.RunRefs.LastOrDefault(item =>
                string.Equals(item.TaskId, task.TaskId, StringComparison.Ordinal));
            if (run is null)
            {
                reasons.Add($"Definition of done has no accepted run for task '{task.TaskId}'.");
                continue;
            }

            if (run.Status != requiredStatus)
            {
                reasons.Add(
                    $"Definition of done expected task '{task.TaskId}' status {requiredStatus} actual {run.Status}.");
                continue;
            }

            evidence.Add(new EvidenceRef("run", run.RunId));
        }
    }
}

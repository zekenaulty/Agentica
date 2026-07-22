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
        var acceptedOutcomes = ResolveAcceptedOutcomes(state, outcomes, reasons);
        if (reasons.Count > 0)
        {
            return new DefinitionOfDoneResult(false, reasons, []);
        }

        var attempts = acceptedOutcomes
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

    private static IReadOnlyList<OutcomeEnvelope> ResolveAcceptedOutcomes(
        OrchestrationState state,
        IReadOnlyList<OutcomeEnvelope> outcomes,
        ICollection<string> reasons)
    {
        var acceptedRunIds = new HashSet<string>(StringComparer.Ordinal);
        var acceptedOutcomes = new List<OutcomeEnvelope>(state.RunRefs.Count);
        foreach (var runRef in state.RunRefs)
        {
            if (string.IsNullOrWhiteSpace(runRef.RunId))
            {
                reasons.Add($"Accepted run for task '{runRef.TaskId}' has an empty run id.");
                continue;
            }

            if (!acceptedRunIds.Add(runRef.RunId))
            {
                reasons.Add($"Accepted run id '{runRef.RunId}' is duplicated in orchestration state.");
                continue;
            }

            var matchingOutcomes = outcomes
                .Where(outcome => string.Equals(
                    outcome.Outcome.RunId,
                    runRef.RunId,
                    StringComparison.Ordinal))
                .ToArray();
            if (matchingOutcomes.Length != 1)
            {
                reasons.Add(
                    $"Accepted run id '{runRef.RunId}' resolves to {matchingOutcomes.Length} child outcomes; exactly one is required.");
                continue;
            }

            acceptedOutcomes.Add(matchingOutcomes[0]);
        }

        return acceptedOutcomes;
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

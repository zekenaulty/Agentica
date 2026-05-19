using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Tools;

namespace Agentica.Orchestration.Context;

public sealed class DeterministicWorkContextCompiler : IWorkContextCompiler
{
    private const int MaxFacts = 16;
    private const int MaxOpenQuestions = 16;
    private const int MaxHypotheses = 8;
    private const int MaxBlockers = 12;
    private const int MaxImpacts = 12;
    private const int MaxEvidenceRefs = 32;
    private const int MaxTextLength = 220;

    public WorkContextSnapshot Compile(WorkContextCompilationRequest request)
    {
        var completed = request.State.CompletedTaskIds.ToArray();
        var newEvidence = EvidenceFrom(request.LatestOutcome);
        var evidenceRefs = (request.Previous?.EvidenceRefs ?? [])
            .Concat(newEvidence)
            .DistinctBy(item => $"{item.Kind}:{item.RefId}")
            .TakeLast(MaxEvidenceRefs)
            .ToArray();
        var facts = (request.Previous?.ProvenFacts ?? [])
            .Concat(CompletedTaskFacts(request, newEvidence))
            .Concat(HostFacts(request.HostState))
            .Where(fact => fact.EvidenceRefs.Count > 0 || fact.FactId.StartsWith("host.", StringComparison.Ordinal))
            .DistinctBy(fact => fact.FactId)
            .TakeLast(MaxFacts)
            .ToArray();
        var blockers = (request.LatestAcceptance?.Status is TaskAcceptanceStatus.Blocked
                or TaskAcceptanceStatus.Rejected
                or TaskAcceptanceStatus.InvalidatedPlan
            ? request.LatestAcceptance.Reasons
            : [])
            .Concat(request.Previous?.KnownBlockers ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Trim)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxBlockers)
            .ToArray();
        var openQuestions = request.Plan.Tasks
            .Where(task => !task.Optional && !request.State.CompletedTaskIds.Contains(task.TaskId, StringComparer.Ordinal))
            .Select(task => $"What evidence is still needed for task '{task.TaskId}': {task.Objective}")
            .Concat(request.LatestAcceptance?.Status == TaskAcceptanceStatus.PartiallyAccepted
                ? request.LatestAcceptance.Reasons
                : [])
            .Select(Trim)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxOpenQuestions)
            .ToArray();
        var hypotheses = request.LatestAcceptance?.Status is TaskAcceptanceStatus.PartiallyAccepted
                or TaskAcceptanceStatus.InvalidatedPlan
            ? request.LatestAcceptance.Reasons
                .Select(reason => $"The task graph may need refinement: {reason}")
                .Select(Trim)
                .Take(MaxHypotheses)
                .ToArray()
            : [];
        var impacts = CompilePlanImpacts(request, newEvidence)
            .Concat(request.Previous?.PlanImpacts ?? [])
            .DistinctBy(impact => $"{impact.Kind}:{impact.Summary}")
            .Take(MaxImpacts)
            .ToArray();

        return new WorkContextSnapshot(
            request.Plan.Objective,
            request.ActiveTask?.TaskId,
            completed,
            facts,
            openQuestions,
            hypotheses,
            blockers,
            impacts,
            evidenceRefs,
            Compact(request.HostState),
            DateTimeOffset.UtcNow);
    }

    private static IEnumerable<ProvenFact> CompletedTaskFacts(
        WorkContextCompilationRequest request,
        IReadOnlyList<EvidenceRef> evidence)
    {
        if (request.ActiveTask is null ||
            !request.State.CompletedTaskIds.Contains(request.ActiveTask.TaskId, StringComparer.Ordinal))
        {
            yield break;
        }

        yield return new ProvenFact(
            $"task.{request.ActiveTask.TaskId}.completed",
            Trim($"Task '{request.ActiveTask.TaskId}' completed: {request.ActiveTask.Objective}"),
            evidence);
    }

    private static IEnumerable<ProvenFact> HostFacts(IReadOnlyDictionary<string, object?> hostState)
    {
        foreach (var pair in hostState)
        {
            if (pair.Value is bool boolValue && boolValue)
            {
                yield return new ProvenFact(
                    $"host.{pair.Key}",
                    $"Host state '{pair.Key}' is true.",
                    []);
            }
        }
    }

    private static IReadOnlyList<PlanImpact> CompilePlanImpacts(
        WorkContextCompilationRequest request,
        IReadOnlyList<EvidenceRef> evidence)
    {
        if (request.LatestAcceptance is null)
        {
            return [];
        }

        var kind = request.LatestAcceptance.Status switch
        {
            TaskAcceptanceStatus.PartiallyAccepted => PlanImpactKind.TaskTooBroad,
            TaskAcceptanceStatus.InvalidatedPlan => PlanImpactKind.NewDependencyDiscovered,
            TaskAcceptanceStatus.Blocked => PlanImpactKind.ExternalBlockerDiscovered,
            _ => (PlanImpactKind?)null
        };

        if (kind is null)
        {
            return [];
        }

        return request.LatestAcceptance.Reasons
            .DefaultIfEmpty(request.LatestAcceptance.Status.ToString())
            .Select(reason => new PlanImpact(kind.Value, Trim(reason), evidence))
            .ToArray();
    }

    private static IReadOnlyList<EvidenceRef> EvidenceFrom(Agentica.Outcomes.OutcomeEnvelope? envelope)
    {
        if (envelope is null)
        {
            return [];
        }

        return envelope.Details.Artifacts
            .Select(artifact => new EvidenceRef("artifact", artifact.ArtifactId))
            .Concat(envelope.Receipts.Items
                .Where(receipt => receipt.Status == ReceiptStatus.Succeeded)
                .Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId)))
            .DistinctBy(item => $"{item.Kind}:{item.RefId}")
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?> Compact(IReadOnlyDictionary<string, object?> source)
    {
        var compact = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (TryCompactValue(pair.Value, out var value))
            {
                compact[pair.Key] = value;
            }
        }

        return compact;
    }

    private static bool TryCompactValue(object? source, out object? value)
    {
        switch (source)
        {
            case null:
            case bool:
            case int:
            case long:
            case double:
            case decimal:
                value = source;
                return true;
            case string text when text.Length <= 128:
                value = text;
                return true;
            case string:
                value = null;
                return false;
            case IEnumerable<string> strings:
                value = strings.Take(16).ToArray();
                return true;
            case IEnumerable<bool> bools:
                value = bools.Take(16).ToArray();
                return true;
            case IEnumerable<int> ints:
                value = ints.Take(16).ToArray();
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static string Trim(string value) =>
        value.Length <= MaxTextLength
            ? value
            : value[..MaxTextLength];
}

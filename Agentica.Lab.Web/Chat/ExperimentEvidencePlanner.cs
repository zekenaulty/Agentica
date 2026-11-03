using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Lab.Web.Chat;

/// <summary>Budgets planner projections only; the runner's authoritative proof remains intact.</summary>
internal sealed class ExperimentEvidencePlanner(IWorkflowPlanner inner, ExperimentExecutionContext context)
    : IExternalWorkflowPlanner, IWorkflowPlannerSessionFactory
{
    public string? FailureCode { get; private set; }
    private void Failed(string code) => FailureCode = code;
    public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(inner, context, request, null, cancellationToken, Failed);
    public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default) =>
        InvokeAsync(inner, context, request, observation, cancellationToken, Failed);
    public IWorkflowPlannerSession BeginSession(PlanningSessionContext session) =>
        inner is IWorkflowPlannerSessionFactory factory
            ? new Session(factory.BeginSession(session), context, ownsSession: true, Failed)
            : new Session(inner, context, ownsSession: false, Failed);

    private static async Task<WorkflowPlan> InvokeAsync(IWorkflowPlanner planner, ExperimentExecutionContext context,
        PlanningRequest request, Observation? observation, CancellationToken cancellationToken, Action<string> failed)
    {
        var selectedObservations = new List<Observation>();
        var selectedReceipts = new List<Receipt>();
        var limit = context.Turn.Settings.MaxToolEvidenceCharacters;
        var used = 0;
        if (observation is not null)
        {
            var requiredSize = JsonSerializer.Serialize(observation).Length;
            if (requiredSize > limit)
            {
                failed("context.evidence_limit");
                throw new ExperimentException("context.evidence_limit", "The latest tool observation alone exceeds the selected cumulative evidence allowance. Increase that allowance before starting a new turn.");
            }
            selectedObservations.Add(observation); used += requiredSize;
        }
        // Include complete typed records, never silently rewrite successful evidence into a
        // shortened claim. Older records remain in the durable run trace and can be inspected.
        foreach (var receipt in request.Receipts.Reverse())
        {
            var size = JsonSerializer.Serialize(receipt).Length;
            if (size <= limit - used) { selectedReceipts.Add(receipt); used += size; }
        }
        foreach (var older in request.Observations.Reverse())
        {
            if (selectedObservations.Any(item => item.ObservationId == older.ObservationId)) continue;
            var size = JsonSerializer.Serialize(older).Length;
            if (size <= limit - used) { selectedObservations.Add(older); used += size; }
        }
        var includedObservations = selectedObservations.Select(item => item.ObservationId).ToHashSet(StringComparer.Ordinal);
        var includedReceipts = selectedReceipts.Select(item => item.ReceiptId).ToHashSet(StringComparer.Ordinal);
        await context.Trace(new("context.tool-evidence", new
        {
            limitCharacters = limit,
            selectedCharacters = used,
            selectedObservationIds = includedObservations.ToArray(),
            selectedReceiptIds = includedReceipts.ToArray(),
            omittedObservationIds = request.Observations.Where(item => !includedObservations.Contains(item.ObservationId)).Select(item => item.ObservationId).ToArray(),
            omittedReceiptIds = request.Receipts.Where(item => !includedReceipts.Contains(item.ReceiptId)).Select(item => item.ReceiptId).ToArray(),
            policy = "complete projections; current observation required; newest receipts then older observations",
            authoritativeEvidence = "unchanged in runner outcome"
        })).ConfigureAwait(false);
        var projected = request with
        {
            Observations = request.Observations.Where(item => includedObservations.Contains(item.ObservationId)).ToArray(),
            Receipts = request.Receipts.Where(item => includedReceipts.Contains(item.ReceiptId)).ToArray()
        };
        return observation is null
            ? await planner.CreatePlanAsync(projected, cancellationToken).ConfigureAwait(false)
            : await planner.RefinePlanAsync(projected, observation, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Session(IWorkflowPlanner planner, ExperimentExecutionContext context, bool ownsSession, Action<string> failed)
        : IWorkflowPlannerSession, IExternalWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            InvokeAsync(planner, context, request, null, cancellationToken, failed);
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default) =>
            InvokeAsync(planner, context, request, observation, cancellationToken, failed);
        public void Dispose() { if (ownsSession && planner is IDisposable disposable) disposable.Dispose(); }
    }
}

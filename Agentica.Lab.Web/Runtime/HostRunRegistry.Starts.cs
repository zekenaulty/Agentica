using System.Text.Json;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

public sealed partial class HostRunRegistry
{
    public (HostRun Run, HostStartEntry Admission) CreateDurable(HostStartSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ProtocolValidation.Identifier(submission.AttemptId);
        ProtocolValidation.Validate(submission.Request);
        var original = CopyRequest(submission.Request);
        lock (_gate)
        {
            var store = RequiredStarts();
            var prior = store.Find(new HostStartKey(original.HostId, original.SessionId, submission.AttemptId));
            if (prior is not null)
            {
                if (prior.OriginalRequestHash != ProtocolValidation.Digest(original))
                    throw new HostProtocolException("start.changed", "This attempt is already bound to a different original request.");
                if (prior.StopRequested)
                    throw new HostProtocolException("start.stopped", "This admission was stopped. Read and reconcile its original evidence.");
                return _runs.TryGetValue(prior.RunId, out var retained) ? (retained, prior) :
                    throw new HostProtocolException("start.interrupted", "This start was already admitted by an earlier execution window. Read and stop its original admission; do not replay execution.");
            }
            CheckNewObjective(original);
            var removable = FindCapacityCandidate();
            var accepted = FreezeProvider(original) with { Limits = original.Limits ?? new HostRunLimits() };
            ProtocolValidation.Validate(accepted);
            var runId = "labrun_" + Guid.NewGuid().ToString("N");
            // Admission survives even if context opening, connection attachment or the reply fails.
            var admission = store.Admit(submission.AttemptId, original, accepted, runId);
            var request = CopyRequest(admission.AcceptedRequest);
            var run = new HostRun(request, contexts.Open(request), planners, custody, operations, runId: runId);
            ReleaseRunCapacity(removable);
            _runs.Add(runId, run);
            return (run, admission);
        }
    }

    public HostStartEntry GetStart(HostStartKey key) => RequiredStarts().Find(key) ??
        throw new HostProtocolException("start.not_found", "No retained admission was found. This is not proof that a start was never attempted.");

    public bool IsStartReplay(HostStartSubmission submission, string runId)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ProtocolValidation.Validate(submission.Request);
        var entry = RequiredStarts().Find(new HostStartKey(submission.Request.HostId, submission.Request.SessionId, submission.AttemptId));
        return entry is not null && entry.RunId == runId && !entry.StopRequested &&
            entry.OriginalRequestHash == ProtocolValidation.Digest(submission.Request);
    }

    public HostStartEntry StopStart(HostStartReference reference)
    {
        lock (_gate)
        {
            var store = RequiredStarts();
            var entry = store.RequestStop(reference);
            if (entry.Terminal is not null) return entry;
            var sessionRuns = _runs.Values.Where(run => run.Request.HostId == reference.HostId &&
                run.Request.SessionId == reference.SessionId).ToArray();
            foreach (var run in sessionRuns.Where(run => run.Request.ObjectiveId == entry.AcceptedRequest.ObjectiveId && !run.Terminal))
                run.Cancel();
            if (sessionRuns.Any(run => !run.Terminal || run.HasUnresolvedActions) ||
                custody.HasUnresolved(reference.HostId, reference.SessionId) ||
                operations?.HasOpen(reference.HostId, reference.SessionId) == true)
                return entry;
            var retained = sessionRuns.FirstOrDefault(run => run.RunId == entry.RunId);
            // A vanished process or a continuation can have usage not accounted for by this window.
            var usageUnknown = retained is null || operations?.List(reference.HostId, reference.SessionId)
                .Any(operation => operation.Request.ObjectiveId == entry.AcceptedRequest.ObjectiveId && operation.Admission is not null) == true;
            return store.SettleStopped(reference, usageUnknown ? "interrupted" : "stopped",
                usageUnknown ? null : retained!.Usage, usageUnknown);
        }
    }

    private HostStartStore RequiredStarts() => starts ??
        throw new HostProtocolException("start.unsupported", "Durable start admission storage is unavailable.");

    private static HostRunRequest CopyRequest(HostRunRequest request) =>
        HostProtocol.Element(request).Deserialize<HostRunRequest>(HostProtocol.Json)!;

    private void CheckNewObjective(HostRunRequest request)
    {
        if (starts?.HasOpen(request.HostId, request.SessionId) == true)
            throw new HostProtocolException("session.start_retained", "Reconcile and stop the original durable start before beginning a new objective.");
        if (starts?.HasRetainedObjective(request.HostId, request.SessionId, request.ObjectiveId) == true)
            throw new HostProtocolException("objective.retained", "This objective already has durable initial-start lineage.");
        if (operations?.HasOpen(request.HostId, request.SessionId) == true)
            throw new HostProtocolException("session.parked", "Continue or cancel the retained host operation before starting a new objective.");
        if (operations?.List(request.HostId, request.SessionId).Any(entry => entry.Admission is not null &&
            entry.Request.ObjectiveId == request.ObjectiveId) == true)
            throw new HostProtocolException("objective.retained", "This objective already has durable lineage. Use its operation handoff or a new objective identity.");
        if (custody.HasUnresolved(request.HostId, request.SessionId))
            throw new HostProtocolException("session.unresolved", "Reconcile the original dispatched actions before starting another run for this session.");
        if (_runs.Values.Any(run => run.Request.HostId == request.HostId && run.Request.SessionId == request.SessionId &&
            (!run.Terminal || run.HasUnresolvedActions)))
            throw new HostProtocolException("session.busy", "Finish or reconcile the existing run for this host session first.");
    }

    private void MakeRunCapacity() => ReleaseRunCapacity(FindCapacityCandidate());

    private HostRun? FindCapacityCandidate()
    {
        if (_runs.Count < 64) return null;
        return _runs.Values.Where(run => run.Terminal && !run.HasUnresolvedActions).OrderBy(run => run.CreatedAt).FirstOrDefault() ??
            throw new HostProtocolException("runs.limit", "Run capacity reached; reconcile outstanding runs.");
    }

    private void ReleaseRunCapacity(HostRun? removable)
    {
        if (removable is null) return;
        _runs.Remove(removable.RunId);
        removable.Dispose();
    }
}

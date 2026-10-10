using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;

namespace Agentica.Lab.Web.Runtime;

public sealed class HostRunRegistry(HostContextStore contexts, ILabPlannerFactory planners, ActionCustodyStore custody,
    HostOperationStore? operations = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostRun> _runs = new(StringComparer.Ordinal);

    public HostRun Create(HostRunRequest request)
    {
        ProtocolValidation.Validate(request);
        lock (_gate)
        {
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
            if (_runs.Count >= 64)
            {
                var removable = _runs.Values.Where(r => r.Terminal && !r.HasUnresolvedActions).OrderBy(r => r.CreatedAt).FirstOrDefault();
                if (removable is null) throw new HostProtocolException("runs.limit", "Run capacity reached; reconcile outstanding runs.");
                _runs.Remove(removable.RunId);
                removable.Dispose();
            }
            request = FreezeProvider(request);
            var run = new HostRun(request, contexts.Open(request), planners, custody, operations);
            _runs.Add(run.RunId, run);
            return run;
        }
    }

    public IReadOnlyList<HostOperationEntry> ListOperations(string hostId, string sessionId) =>
        RequiredOperations().List(hostId, sessionId);

    public HostOperationEntry ApplyOperationEvent(HostOperationEvent item)
    {
        if (item.Kind == "decision")
            throw new HostProtocolException("operation.wake_transport", "Use operation.wake on a host connection for decision events.");
        lock (_gate)
        {
            var entry = RequiredOperations().Get(item.HostId, item.SessionId, item.ActionId);
            CheckAdmission(entry);
            if (entry.State == "waking" && _runs.TryGetValue(entry.WakeRunId!, out var active) && !active.Terminal)
                throw new HostProtocolException("session.busy", "Stop the retained execution window before cancelling its objective.");
            if (item.Sequence > (entry.LatestEvent?.Sequence ?? 0)) ValidateOperationObservation(entry, item);
            var accepted = RequiredOperations().Apply(item);
            // Progress observations retain their actual as-of time. A transfer opens a new scoped
            // context while the original admission and historical sources remain in the old epoch.
            contexts.Open(entry.Request with { SessionEpoch = accepted.ActiveEpoch, Observation = accepted.LatestEvent!.Observation });
            return accepted;
        }
    }

    public HostRun Wake(HostOperationEvent item)
    {
        if (item.Kind != "decision") throw new HostProtocolException("operation.wake_kind", "Only a decision event activates cognition.");
        lock (_gate)
        {
            var store = RequiredOperations();
            var entry = store.Get(item.HostId, item.SessionId, item.ActionId);
            if (entry.LatestEvent?.EventId == item.EventId && entry.WakeRunId is not null)
            {
                store.Apply(item, entry.WakeRunId); // Exact replay still validates the entire immutable signal.
                return _runs.TryGetValue(entry.WakeRunId, out var retained) ? retained :
                    throw new HostProtocolException("operation.wake_interrupted", "This wake was already claimed by a prior process. Reconcile its original custody; it is not automatically replayed.");
            }
            CheckAdmission(entry);
            if (_runs.Values.Any(run => run.Request.HostId == item.HostId && run.Request.SessionId == item.SessionId && !run.Terminal))
                throw new HostProtocolException("session.busy", "Wait for the existing bounded execution window to settle.");
            var limits = entry.Request.Limits ?? new HostRunLimits();
            if (entry.Usage.Steps >= limits.MaxSteps || entry.Usage.Refinements > limits.MaxRefinements ||
                entry.Usage.Continuations >= limits.MaxPlanContinuations)
                throw new HostProtocolException("operation.budget", "The objective's cumulative execution allowance is exhausted. Progress and terminal evidence remain admissible.");
            if (_runs.Count >= 64)
            {
                var removable = _runs.Values.Where(run => run.Terminal && !run.HasUnresolvedActions).OrderBy(run => run.CreatedAt).FirstOrDefault();
                if (removable is null) throw new HostProtocolException("runs.limit", "Run capacity reached; reconcile outstanding runs.");
                _runs.Remove(removable.RunId);
                removable.Dispose();
            }
            var runId = "labrun_" + Guid.NewGuid().ToString("N");
            ValidateOperationObservation(entry, item);
            var claimed = store.Apply(item, runId); // Persist the activation identity before creating a planner.
            var request = entry.Request with { SessionEpoch = claimed.ActiveEpoch, Observation = item.Observation };
            var usage = entry.Usage with { Continuations = checked(entry.Usage.Continuations + 1) };
            var run = new HostRun(request, contexts.Open(request), planners, custody, store, usage, runId, claimed);
            _runs.Add(run.RunId, run);
            return run;
        }
    }

    private void CheckAdmission(HostOperationEntry entry)
    {
        if (entry.Admission is null) throw new HostProtocolException("operation.not_admitted", "Reconcile the original admission first.");
        if (custody.HasUnresolved(entry.Request.HostId, entry.Request.SessionId))
            throw new HostProtocolException("session.unresolved", "Reconcile original dispatched actions before an operation update or wake.");
        var original = custody.Get(entry.Request.HostId, entry.Request.SessionId, entry.Action.ActionId);
        if (original.ResultHash != ProtocolValidation.Digest(entry.Admission))
            throw new HostProtocolException("operation.admission_unsettled", "Original admission custody does not match the retained handoff.");
    }

    public bool IsWakeReplay(HostOperationEvent item, string runId)
    {
        lock (_gate)
        {
            var entry = RequiredOperations().Get(item.HostId, item.SessionId, item.ActionId);
            return entry.WakeRunId == runId && entry.LatestEvent?.Kind == "decision" &&
                ProtocolValidation.Digest(entry.LatestEvent) == ProtocolValidation.Digest(item);
        }
    }

    private HostOperationStore RequiredOperations() => operations ??
        throw new HostProtocolException("operation.unsupported", "Durable operation storage is unavailable.");

    private HostRunRequest FreezeProvider(HostRunRequest request)
    {
        var settings = request.Provider ?? new ProviderSettings();
        settings = settings with
        {
            Provider = settings.Provider?.Trim().ToLowerInvariant() switch
            {
                "google" => "gemini",
                "claude" => "anthropic",
                "xai" => "grok",
                { } name => name,
                _ => "gemini"
            }
        };
        var metadata = planners.GetProviders().FirstOrDefault(item => item.Provider == settings.Provider);
        if (metadata is null) return request with { Provider = settings };
        var model = settings.Model ?? metadata.DefaultModel;
        return request with
        {
            Provider = settings with
            {
                Model = model,
                ThinkingEffort = settings.ThinkingEffortResolved ? settings.ThinkingEffort :
                settings.ThinkingEffort ?? (model == metadata.DefaultModel ? metadata.DefaultThinkingEffort : null),
                ThinkingEffortResolved = true
            }
        };
    }

    private void ValidateOperationObservation(HostOperationEntry entry, HostOperationEvent item)
    {
        try
        {
            contexts.ValidateObservation(entry.Request with
            {
                SessionEpoch = item.Kind == "transfer" ? item.NextSessionEpoch! : item.SessionEpoch,
                Observation = item.Observation
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new HostProtocolException("operation.observation", "Operation evidence does not satisfy the scoped context contract.");
        }
    }

    public HostRun Get(string runId)
    {
        lock (_gate) return _runs.TryGetValue(runId, out var run) ? run
            : throw new HostProtocolException("run.not_found", "Run is not retained by this service. Do not replay unresolved effects.");
    }

    public object[] List()
    {
        lock (_gate) return _runs.Values.OrderByDescending(r => r.CreatedAt).Select(r => r.Snapshot(false)).ToArray();
    }

    public bool Recover(string hostId, string sessionId, HostActionResult result)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
        if (result is null) throw new HostProtocolException("recovery.result", "An original action result is required.");
        ProtocolValidation.Identifier(result.ActionId);
        lock (_gate)
        {
            var entry = custody.Get(hostId, sessionId, result.ActionId);
            if (_runs.ContainsKey(entry.RunId))
                throw new HostProtocolException("recovery.live_run", "Resume the retained run and send action.result through its host connection.");
            if (result.Operation is not null)
            {
                custody.PrevalidateResolution(hostId, sessionId, result.ActionId, result);
                var retained = RequiredOperations().Admit(hostId, sessionId, result);
                contexts.Open(retained.Request with { Observation = result.Observation! }).RecordActionResult(retained.Action, result);
            }
            return custody.Resolve(hostId, sessionId, result.ActionId, result);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values) run.Dispose();
            _runs.Clear();
        }
    }
}

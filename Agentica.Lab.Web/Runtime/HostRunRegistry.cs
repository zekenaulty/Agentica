using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;

namespace Agentica.Lab.Web.Runtime;

public sealed class HostRunRegistry(HostContextStore contexts, ILabPlannerFactory planners, ActionCustodyStore custody) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostRun> _runs = new(StringComparer.Ordinal);

    public HostRun Create(HostRunRequest request)
    {
        ProtocolValidation.Validate(request);
        lock (_gate)
        {
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
            var run = new HostRun(request, contexts.Open(request), planners, custody);
            _runs.Add(run.RunId, run);
            return run;
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

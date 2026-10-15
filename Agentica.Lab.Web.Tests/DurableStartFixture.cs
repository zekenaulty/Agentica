using System.Threading.Channels;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

internal sealed class DurableStartFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "agentica-start-registry", Guid.NewGuid().ToString("N"));
    public CountingFactory Planners { get; } = new();
    public HostRunRegistry Registry { get; private set; } = null!;
    public HostStartStore Starts { get; private set; } = null!;
    public ActionCustodyStore Custody { get; private set; } = null!;
    public HostOperationStore Operations { get; private set; } = null!;
    private readonly List<HostRun> _runs = [];
    public DurableStartFixture() => Restart();

    public void Restart()
    {
        Registry?.Dispose();
        Starts = new(Path.Combine(Root, "starts"));
        Custody = new(Path.Combine(Root, "custody"));
        Operations = new(Path.Combine(Root, "operations"));
        Registry = new(new HostContextStore(Path.Combine(Root, "context")), Planners, Custody, Operations, Starts);
    }
    public (HostRun Run, HostStartEntry Admission) Admit(HostStartSubmission? submission = null)
    {
        var result = Registry.CreateDurable(submission ?? Submission());
        _runs.Add(result.Run);
        return result;
    }
    public static HostStartSubmission Submission() => new("attempt-one", Request());
    public static HostRunRequest Request() => new(1, "host", "session", "epoch", "scope", "actor", "objective",
        "Do one explicitly bound host action.", Observation("initial", 0),
        [new("host.operate", "Operate", "Bounded host work.", ToolKind.Action, ToolEffect.WritesLocalState,
            ToolInputSchema.Create(), DurableHandoff: true)], new("fixture"));
    public static HostObservation Observation(string id, long revision) => new(id, revision,
        new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero).AddSeconds(revision), HostProtocol.Element(new { revision }));
    public static HostStartKey Key(HostStartEntry entry) => new(entry.OriginalRequest.HostId, entry.OriginalRequest.SessionId, entry.AttemptId);
    public static HostStartReference Reference(HostStartEntry entry) => new(entry.OriginalRequest.HostId, entry.OriginalRequest.SessionId,
        entry.AttemptId, entry.RunId, entry.AcceptedRequestHash);
    public static HostActionResult Result(HostActionRequest action, bool operation = false) => new(action.ActionId, action.SessionId,
        action.SessionEpoch, "applied", action.ExpectedRevision, action.ExpectedRevision + 1, "receipt-" + action.ActionId,
        "Host evidence.", Observation("observed-" + action.ActionId, action.ExpectedRevision + 1),
        Completion: operation ? null : new("objective", "completion", "The host proved the objective."),
        Operation: operation ? new("operation-" + action.ActionId, "Host operation admitted.", HostProtocol.Element(new { ticks = 0 })) : null);
    public static HostOperationEvent Event(HostActionRequest action, string kind) => new("host", "session", "epoch", action.ActionId,
        "operation-" + action.ActionId, 1, "event-" + action.ActionId, kind, Observation("event-observation", 2),
        "Host event.", HostProtocol.Element(new { ticks = 1 }));

    public async ValueTask DisposeAsync()
    {
        foreach (var run in _runs.Distinct()) if (!run.Terminal) run.Cancel();
        await Task.WhenAll(_runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Registry.Dispose();
        Directory.Delete(Root, true);
    }

    internal sealed class CountingFactory : ILabPlannerFactory
    {
        public int Created;
        public Action? OnCreate;
        public string Model = "offline-model";
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref Created);
            OnCreate?.Invoke();
            return new OneActionPlanner();
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [new("fixture", Model, true, null, "offline", DefaultThinkingEffort: "low")];
    }
    private sealed class OneActionPlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("plan", 1,
                [new PlanStep("step", "host.operate", ToolKind.Action, ToolEffect.WritesLocalState, new Dictionary<string, object?>())], "One host action."));
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No model refinement belongs in this fixture.");
    }
    internal sealed class Connection : IHostConnection
    {
        private readonly Channel<HostActionRequest> _actions = Channel.CreateUnbounded<HostActionRequest>();
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Type is "action.request" or "action.reconcile") _actions.Writer.TryWrite((HostActionRequest)message.Payload);
            return Task.CompletedTask;
        }
        public async Task<HostActionRequest> NextAction() => await _actions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
}

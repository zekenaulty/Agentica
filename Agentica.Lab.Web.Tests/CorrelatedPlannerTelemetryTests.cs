using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Lab.Web.Tests;

public sealed class CorrelatedPlannerTelemetryTests
{
    [Fact]
    public async Task Repair_calls_and_refinement_keep_their_own_invocation_after_delayed_consumption()
    {
        using var telemetry = new CorrelatedPlannerTelemetry("service-run", "objective-1", "fixture");
        var planner = telemetry.Wrap(new StreamingPlanner(telemetry.Report, createCalls: 2));
        await planner.CreatePlanAsync(Request("frame-create"));
        await planner.RefinePlanAsync(Request("frame-refine", "plan-1", 1), Observation());
        telemetry.Complete();

        var calls = (await DrainAsync(telemetry)).GroupBy(item => item.Record.CallId).ToArray();
        Assert.Equal(3, calls.Length);
        var initial = Assert.IsType<PlannerTelemetryContext>(calls[0].First().Context);
        Assert.Equal("service-run", initial.RunId);
        Assert.Equal("runner-run", initial.RunnerRunId);
        Assert.Equal("objective-1", initial.ObjectiveId);
        Assert.Equal("create", initial.PlanningOperation);
        Assert.Null(initial.CurrentPlanId);
        Assert.Equal(0, initial.PlanVersionCount);
        Assert.Null(initial.AfterStepId);
        Assert.Equal(new PlannerTelemetryFrame("frame-create", "host", "v1", "surface-1"), Assert.Single(initial.Frames));
        Assert.All(calls.Take(2).SelectMany(call => call), item => Assert.Same(initial, item.Context));

        var refined = Assert.IsType<PlannerTelemetryContext>(calls[2].First().Context);
        Assert.Equal("refine", refined.PlanningOperation);
        Assert.Equal("plan-1", refined.CurrentPlanId);
        Assert.Equal(1, refined.PlanVersionCount);
        Assert.Equal("step-1", refined.AfterStepId);
        Assert.Equal("frame-refine", Assert.Single(refined.Frames).FrameId);
        Assert.All(calls[2], item => Assert.Same(refined, item.Context));
    }

    [Fact]
    public async Task Wrapping_preserves_external_markers_and_runner_owned_session_lifetime()
    {
        using var telemetry = new CorrelatedPlannerTelemetry("service-run", "objective-1", "fixture");
        var factory = new ExternalSessionFactory(telemetry.Report);
        var planner = telemetry.Wrap(factory);
        Assert.IsAssignableFrom<IExternalWorkflowPlanner>(planner);
        var wrappedFactory = Assert.IsAssignableFrom<IWorkflowPlannerSessionFactory>(planner);
        var context = new PlanningSessionContext("runner-run", "Inspect.", RequestOrigin.User);
        using var session = wrappedFactory.BeginSession(context);
        Assert.Same(context, factory.Context);
        Assert.IsAssignableFrom<IExternalWorkflowPlanner>(session);
        var request = Request("frame-1");
        await session.CreatePlanAsync(request);
        Assert.Same(request, factory.Session.LastRequest);

        telemetry.Dispose();
        Assert.Equal(0, factory.Session.DisposeCount);
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, factory.Session.DisposeCount);

        var localFactory = telemetry.Wrap(new SessionFactory(_ => { }));
        Assert.False(localFactory is IExternalWorkflowPlanner);
        using var externalSession = Assert.IsAssignableFrom<IWorkflowPlannerSessionFactory>(localFactory).BeginSession(context);
        Assert.IsAssignableFrom<IExternalWorkflowPlanner>(externalSession);
        var localPlanner = telemetry.Wrap(new StreamingPlanner(_ => { }));
        Assert.False(localPlanner is IExternalWorkflowPlanner);
        Assert.False(localPlanner is IWorkflowPlannerSessionFactory);
    }

    [Fact]
    public async Task Serialized_correlation_contains_references_without_private_frame_or_provider_content()
    {
        using var telemetry = new CorrelatedPlannerTelemetry("service-run", "objective-1", "fixture");
        var planner = telemetry.Wrap(new StreamingPlanner(telemetry.Report));
        await planner.RefinePlanAsync(Request("frame-1", "plan-1", 1), Observation());
        telemetry.Complete();

        var deliveries = await DrainAsync(telemetry);
        var json = JsonSerializer.Serialize(deliveries, HostProtocol.Json);
        Assert.Contains("\"runId\":\"service-run\"", json, StringComparison.Ordinal);
        Assert.Contains("\"runnerRunId\":\"runner-run\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frameId\":\"frame-1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nativeContinuation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("structuredJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(deliveries, item => item.Record.OutputCharacters > 0);
    }

    [Fact]
    public async Task Failure_restores_scope_and_unbound_calls_serialize_an_explicit_null_context()
    {
        using var telemetry = new CorrelatedPlannerTelemetry("service-run", "objective-1", "fixture");
        var planner = telemetry.Wrap(new FailingPlanner(telemetry.Report));
        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.CreatePlanAsync(Request("frame-1")));
        telemetry.Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        telemetry.Report(new LlmStreamEvent(LlmStreamEventKind.Completed));
        telemetry.Complete();

        var calls = (await DrainAsync(telemetry)).GroupBy(item => item.Record.CallId).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls[0], item => Assert.NotNull(item.Context));
        Assert.All(calls[1], item => Assert.Null(item.Context));
        Assert.Contains("\"context\":null", JsonSerializer.Serialize(calls[1].First(), HostProtocol.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_reader_never_borrows_another_invocations_frame()
    {
        using var telemetry = new CorrelatedPlannerTelemetry("service-run", "objective-1", "fixture");
        var planner = telemetry.Wrap(new StreamingPlanner(telemetry.Report));
        var reading = DrainAsync(telemetry);
        for (var index = 0; index < 600; index++)
            await planner.CreatePlanAsync(Request($"frame-{index}"));
        telemetry.Complete();

        var activity = (await reading).Where(item => item.Record.Kind == "activity").ToArray();
        Assert.NotEmpty(activity);
        Assert.All(activity, item =>
        {
            // A lagging reader may see an explicitly unknown association after pruning.
            if (item.Context is { } context)
                Assert.Equal(item.Record.Activity, Assert.Single(context.Frames).FrameId);
        });
        Assert.Equal("frame-599", Assert.Single(Assert.IsType<PlannerTelemetryContext>(activity[^1].Context).Frames).FrameId);
    }

    [Fact]
    public async Task Missing_runner_session_identity_remains_unknown()
    {
        using var telemetry = new CorrelatedPlannerTelemetry("service-run", "objective-1", "fixture");
        var planner = telemetry.Wrap(new StreamingPlanner(telemetry.Report));
        await planner.CreatePlanAsync(Request("frame-1") with { SessionContext = null });
        telemetry.Complete();
        Assert.All(await DrainAsync(telemetry), item =>
            Assert.Null(Assert.IsType<PlannerTelemetryContext>(item.Context).RunnerRunId));
    }

    private static PlanningRequest Request(string frameId, string? planId = null, int planVersionCount = 0) =>
        new(new RunRequest("secret-objective", Context: new Dictionary<string, object?> { ["value"] = "secret-request" }), [], [], [])
        {
            SessionContext = new PlanningSessionContext("runner-run", "secret-session-objective", RequestOrigin.User),
            ExecutionContext = new PlanningExecutionContext([], [], planId, planVersionCount),
            ContextFrames = [new PlanningFrame(frameId, "host", "v1", DateTimeOffset.UtcNow,
                new Dictionary<string, object?> { ["value"] = "secret-frame" }, []) { ToolSurfaceId = "surface-1" }]
        };

    private static Observation Observation() => new("observation-1", "step-1", ObservationKind.ToolResult,
        "secret-observation", new Dictionary<string, object?> { ["value"] = "secret-observation-data" }, []);

    private static async Task<List<CorrelatedPlannerTelemetryDelivery>> DrainAsync(CorrelatedPlannerTelemetry telemetry)
    {
        var result = new List<CorrelatedPlannerTelemetryDelivery>();
        await foreach (var item in telemetry.ReadAllAsync()) result.Add(item);
        return result;
    }

    private class StreamingPlanner(Action<LlmStreamEvent> report, int createCalls = 1) : IWorkflowPlanner
    {
        public PlanningRequest? LastRequest { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            EmitAsync(request, createCalls, cancellationToken);

        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) => EmitAsync(request, 1, cancellationToken);

        private async Task<WorkflowPlan> EmitAsync(PlanningRequest request, int calls, CancellationToken cancellationToken)
        {
            LastRequest = request;
            for (var index = 0; index < calls; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report(new LlmStreamEvent(LlmStreamEventKind.Started));
                await Task.Yield();
                report(new LlmStreamEvent(LlmStreamEventKind.Activity, request.ContextFrames[0].FrameId));
                report(new LlmStreamEvent(LlmStreamEventKind.TextDelta, "secret-provider-text"));
                report(new LlmStreamEvent(LlmStreamEventKind.ThoughtSummaryDelta, "secret-disabled-summary"));
                report(new LlmStreamEvent(LlmStreamEventKind.Completed, Response: new LlmResponse(
                    "fixture", "fixture-model", "secret-provider-response", StructuredJson: "secret-provider-json",
                    Metadata: new Dictionary<string, string> { ["value"] = "secret-provider-metadata" })));
            }
            return new WorkflowPlan("plan-1", 1, [], "fixture");
        }
    }

    private class SessionFactory(Action<LlmStreamEvent> report) : IWorkflowPlanner, IWorkflowPlannerSessionFactory
    {
        public PlanningSessionContext? Context { get; private set; }
        public NativeSession Session { get; } = new(report);

        public IWorkflowPlannerSession BeginSession(PlanningSessionContext context)
        {
            Context = context;
            return Session;
        }

        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The runner must use the actual session.");

        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The runner must use the actual session.");
    }

    private sealed class ExternalSessionFactory(Action<LlmStreamEvent> report) : SessionFactory(report), IExternalWorkflowPlanner
    {
    }

    private sealed class NativeSession(Action<LlmStreamEvent> report)
        : StreamingPlanner(report), IWorkflowPlannerSession, IExternalWorkflowPlanner
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class FailingPlanner(Action<LlmStreamEvent> report) : IWorkflowPlanner
    {
        public async Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            report(new LlmStreamEvent(LlmStreamEventKind.Started));
            await Task.Yield();
            report(new LlmStreamEvent(LlmStreamEventKind.Failed, "fixture.failure"));
            throw new InvalidOperationException("Fixture failure.");
        }

        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) => CreatePlanAsync(request, cancellationToken);
    }
}

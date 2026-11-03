using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStartRegistryTests
{
    [Fact]
    public async Task Admission_precedes_planning_and_freezes_full_request_across_exact_retry()
    {
        await using var fixture = new DurableStartFixture();
        var (run, entry) = fixture.Admit();
        Assert.Equal(0, fixture.Planners.Created);
        Assert.Equal("offline-model", entry.AcceptedRequest.Provider!.Model);
        Assert.Equal(new HostRunLimits(), entry.AcceptedRequest.Limits);
        Assert.Null(entry.OriginalRequest.Limits);
        fixture.Planners.Model = "changed-default";
        Assert.Same(run, fixture.Registry.CreateDurable(Submission()).Run);
        Assert.Equal(ProtocolValidation.Digest(entry), ProtocolValidation.Digest(fixture.Registry.CreateDurable(Submission()).Admission));
        fixture.Planners.OnCreate = () => Assert.Equal(entry.AcceptedRequestHash, fixture.Starts.Find(Key(entry))!.AcceptedRequestHash);
        var connection = new Connection();
        run.Attach(connection);
        run.Start();
        var action = await connection.NextAction();
        Assert.Equal(1, fixture.Planners.Created);
        run.AcceptResult(Result(action));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = fixture.Registry.StopStart(Reference(entry));
        Assert.Equal("stopped", stopped.Terminal!.Reason);
        Assert.False(stopped.Terminal.UsageUnknown);
        Assert.Equal(1, stopped.Terminal.Usage!.Steps);
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(fixture.Registry.StopStart(Reference(entry))));
    }

    [Fact]
    public async Task Stop_before_start_cannot_be_undone_by_the_socket_finally_block()
    {
        await using var fixture = new DurableStartFixture();
        var (run, entry) = fixture.Admit();
        var stopped = fixture.Registry.StopStart(Reference(entry));
        run.Start();
        await run.Completion;
        Assert.True(run.Terminal);
        Assert.Equal(0, fixture.Planners.Created);
        Assert.Equal(new HostRunUsage(), stopped.Terminal!.Usage);
        Assert.Equal("start.stopped", Assert.Throws<HostProtocolException>(() => fixture.Registry.CreateDurable(Submission())).Code);
    }

    [Fact]
    public async Task Restart_read_never_reexecutes_and_stop_retains_unknown_usage_and_objective_tombstone()
    {
        await using var fixture = new DurableStartFixture();
        var (run, entry) = fixture.Admit();
        run.Cancel();
        fixture.Restart();
        Assert.Equal(ProtocolValidation.Digest(entry), ProtocolValidation.Digest(fixture.Registry.GetStart(Key(entry))));
        Assert.Equal("start.interrupted", Assert.Throws<HostProtocolException>(() => fixture.Registry.CreateDurable(Submission())).Code);
        Assert.Equal("session.start_retained", Assert.Throws<HostProtocolException>(() => fixture.Registry.Create(Request() with { ObjectiveId = "replacement" })).Code);
        var stopped = fixture.Registry.StopStart(Reference(entry));
        Assert.Equal("interrupted", stopped.Terminal!.Reason);
        Assert.Null(stopped.Terminal.Usage);
        Assert.True(stopped.Terminal.UsageUnknown);
        fixture.Restart();
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(fixture.Registry.StopStart(Reference(entry))));
        Assert.Equal("objective.retained", Assert.Throws<HostProtocolException>(() => fixture.Registry.Create(Request())).Code);
        var next = fixture.Admit(new("next-attempt", Request() with { ObjectiveId = "next-objective" }));
        Assert.NotEqual(entry.RunId, next.Run.RunId);
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(fixture.Registry.StopStart(Reference(entry))));
        Assert.False(next.Run.Terminal);
        Assert.Equal(0, fixture.Planners.Created);
    }

    [Fact]
    public async Task Changed_attempt_and_wrong_stop_binding_cannot_modify_admission()
    {
        await using var fixture = new DurableStartFixture();
        var (_, entry) = fixture.Admit();
        Assert.Equal("start.changed", Assert.Throws<HostProtocolException>(() => fixture.Registry.CreateDurable(
            Submission() with { Request = Request() with { Objective = "Changed intention." } })).Code);
        Assert.Equal("start.reference", Assert.Throws<HostProtocolException>(() => fixture.Registry.StopStart(
            Reference(entry) with { RunId = "wrong-run" })).Code);
        Assert.Equal(ProtocolValidation.Digest(entry), ProtocolValidation.Digest(fixture.Registry.GetStart(Key(entry))));
        Assert.Equal("start.not_found", Assert.Throws<HostProtocolException>(() => fixture.Registry.GetStart(Key(entry) with { AttemptId = "absent" })).Code);
        Assert.Equal(0, fixture.Planners.Created);
    }

    [Fact]
    public async Task Failed_context_open_leaves_an_admission_not_a_retriable_new_execution()
    {
        await using var fixture = new DurableStartFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "context"), "Deliberately block the context directory.");
        Assert.ThrowsAny<IOException>(() => fixture.Admit());
        var entry = fixture.Registry.GetStart(new("host", "session", "attempt-one"));
        Assert.Empty(fixture.Registry.List());
        Assert.Equal("start.interrupted", Assert.Throws<HostProtocolException>(() => fixture.Registry.CreateDurable(Submission())).Code);
        Assert.Equal("interrupted", fixture.Registry.StopStart(Reference(entry)).Terminal!.Reason);
        Assert.Equal(0, fixture.Planners.Created);
    }

    [Fact]
    public async Task Stop_requires_original_dispatched_action_reconciliation_even_after_restart()
    {
        await using var fixture = new DurableStartFixture();
        var (run, entry) = fixture.Admit();
        var connection = new Connection();
        run.Attach(connection);
        run.Start();
        var action = await connection.NextAction();
        Assert.Null(fixture.Registry.StopStart(Reference(entry)).Terminal);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Restart();
        Assert.Null(fixture.Registry.StopStart(Reference(entry)).Terminal);
        fixture.Registry.Recover("host", "session", Result(action));
        var stopped = fixture.Registry.StopStart(Reference(entry));
        Assert.Equal("interrupted", stopped.Terminal!.Reason);
        Assert.Equal(1, fixture.Planners.Created);
    }

    [Fact]
    public async Task Stop_fences_cognition_but_allows_original_operation_to_reconcile_before_settlement()
    {
        await using var fixture = new DurableStartFixture();
        var (run, entry) = fixture.Admit();
        var connection = new Connection();
        run.Attach(connection);
        run.Start();
        var action = await connection.NextAction();
        run.AcceptResult(Result(action, operation: true));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = fixture.Registry.StopStart(Reference(entry));
        Assert.True(pending.StopRequested);
        Assert.Null(pending.Terminal);
        Assert.Equal("start.stopped", Assert.Throws<HostProtocolException>(() => fixture.Registry.Wake(Event(action, "decision"))).Code);
        fixture.Registry.ApplyOperationEvent(Event(action, "cancelled"));
        var stopped = fixture.Registry.StopStart(Reference(entry));
        Assert.NotNull(stopped.Terminal);
        Assert.True(stopped.Terminal.UsageUnknown);
        Assert.Equal(1, fixture.Planners.Created);
    }
}

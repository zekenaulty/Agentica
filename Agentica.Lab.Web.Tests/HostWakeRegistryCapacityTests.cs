using System.Security.Cryptography;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed class HostWakeRegistryCapacityTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory]
    [InlineData("observation", "operation.observation")]
    [InlineData("decision", "operation.sequence")]
    [InlineData("adoption", "operation.epoch_reused")]
    public async Task Rejected_wake_preserves_all_retained_windows_and_original_evidence(string invalid, string code)
    {
        await using var fixture = new DurableStartFixture();
        var (action, _) = SeedOperation(fixture);
        var retained = FillCapacity(fixture);
        var decision = Event(action, "decision");
        var before = StorageSnapshot(fixture);
        var candidate = invalid switch
        {
            "observation" => decision with { Observation = Observation("rewound", 0) },
            "decision" => decision with { Sequence = 2 },
            _ => decision
        };
        var adoption = invalid == "adoption"
            ? new HostDecisionAdoption("invalid-adoption", decision.SessionEpoch, Observation("adopted", 3))
            : null;

        Assert.Equal(code, Assert.Throws<HostProtocolException>(() => fixture.Registry.Wake(candidate, adoption)).Code);
        AssertCapacityPreserved(fixture, retained, before);
        Assert.Null(fixture.Operations.Get("host", "session", action.ActionId).WakeRunId);
        Assert.Equal(0, fixture.Planners.Created);

        AssertSuccessfulClaim(fixture, retained, action, decision);
    }

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public async Task Failed_claim_storage_preserves_capacity_and_exact_original_retry()
    {
        await using var fixture = new DurableStartFixture();
        var (action, _) = SeedOperation(fixture);
        var retained = FillCapacity(fixture);
        var before = StorageSnapshot(fixture);
        var decision = Event(action, "decision");
        var path = Path.Combine(fixture.Root, "operations", HostOperationStore.LedgerFileName);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => fixture.Registry.Wake(decision)) is IOException or UnauthorizedAccessException);
        AssertCapacityPreserved(fixture, retained, before);
        Assert.Null(fixture.Operations.Get("host", "session", action.ActionId).WakeRunId);
        Assert.Equal(0, fixture.Planners.Created);

        AssertSuccessfulClaim(fixture, retained, action, decision);
    }

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public async Task Failure_after_claim_preserves_windows_and_durable_interrupted_identity()
    {
        await using var fixture = new DurableStartFixture();
        var (action, contextPath) = SeedOperation(fixture);
        var retained = FillCapacity(fixture);
        var decision = Event(action, "decision");
        var admission = fixture.Operations.Get("host", "session", action.ActionId);
        using (var held = new FileStream(contextPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => fixture.Registry.Wake(decision)) is IOException or UnauthorizedAccessException);

        Assert.All(retained, run => Assert.Same(run, fixture.Registry.Get(run.RunId)));
        Assert.Equal(64, fixture.Registry.List().Length);
        var claimed = fixture.Operations.Get("host", "session", action.ActionId);
        Assert.NotNull(claimed.WakeRunId);
        Assert.Equal("waking", claimed.State);
        Assert.Equal(ProtocolValidation.Digest(decision), ProtocolValidation.Digest(claimed.LatestEvent!));
        Assert.Equal(admission.Usage, claimed.Usage);
        var beforeRetry = StorageSnapshot(fixture);
        Assert.Equal("operation.wake_interrupted", Assert.Throws<HostProtocolException>(() => fixture.Registry.Wake(decision)).Code);
        AssertCapacityPreserved(fixture, retained, beforeRetry);
        Assert.Equal(0, fixture.Planners.Created);
    }

    private static HostRun[] FillCapacity(DurableStartFixture fixture) => Enumerable.Range(0, 64).Select(index =>
    {
        var run = fixture.Registry.Create(Request() with { SessionId = "retained-" + index });
        run.Cancel();
        return run;
    }).ToArray();

    private static (HostActionRequest Action, string ContextPath) SeedOperation(DurableStartFixture fixture)
    {
        var request = Request();
        var contextRoot = Path.Combine(fixture.Root, "context");
        var context = new HostContextStore(contextRoot).Open(request);
        var action = new HostActionRequest("original-action", "original-run", "original-runner", "original-step",
            request.SessionId, request.SessionEpoch, request.Capabilities[0].Id, ProtocolValidation.Digest(request.Capabilities),
            HostProtocol.Element(new { }), 0, DateTimeOffset.UtcNow.AddMinutes(1));
        context.RecordActionDispatch(action);
        fixture.Operations.Reserve(request, action, new HostRunUsage(Steps: 1, Continuations: 1, ProviderCalls: 3));
        fixture.Custody.Reserve(request.HostId, request.SessionId, request.SessionEpoch, action.RunId,
            action, request.Capabilities[0], request.ObjectiveId);
        var result = Result(action, operation: true);
        fixture.Operations.Admit(request.HostId, request.SessionId, result);
        context.RecordActionResult(action, result);
        fixture.Custody.Resolve(request.HostId, request.SessionId, action.ActionId, result);
        return (action, Assert.Single(Directory.GetFiles(contextRoot, "*.json")));
    }

    private static void AssertSuccessfulClaim(DurableStartFixture fixture, HostRun[] retained,
        HostActionRequest action, HostOperationEvent decision)
    {
        var original = fixture.Operations.Get("host", "session", action.ActionId);
        var successor = fixture.Registry.Wake(decision);
        try
        {
            Assert.Equal(64, fixture.Registry.List().Length);
            Assert.Equal("run.not_found", Assert.Throws<HostProtocolException>(() => fixture.Registry.Get(retained[0].RunId)).Code);
            Assert.All(retained.Skip(1), run => Assert.Same(run, fixture.Registry.Get(run.RunId)));
            Assert.Same(successor, fixture.Registry.Wake(decision));
            Assert.Equal(64, fixture.Registry.List().Length);
            Assert.Equal(original.Usage with { Continuations = original.Usage.Continuations + 1 }, successor.Usage);
            Assert.Equal(ProtocolValidation.Digest(original.Request), ProtocolValidation.Digest(successor.Request with { Observation = original.Request.Observation }));
            Assert.Equal(0, fixture.Planners.Created);
        }
        finally { successor.Cancel(); }
    }

    private static void AssertCapacityPreserved(DurableStartFixture fixture, HostRun[] retained, string[] before)
    {
        Assert.All(retained, run => Assert.Same(run, fixture.Registry.Get(run.RunId)));
        Assert.Equal(64, fixture.Registry.List().Length);
        Assert.Equal(before, StorageSnapshot(fixture));
    }

    private static string[] StorageSnapshot(DurableStartFixture fixture) => Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture.Root, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}

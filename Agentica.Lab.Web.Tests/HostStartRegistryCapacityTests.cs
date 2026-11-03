using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStartRegistryCapacityTests
{
    [Fact]
    public async Task Rejected_admission_does_not_evict_an_unrelated_retained_run()
    {
        await using var fixture = new DurableStartFixture();
        var retained = Enumerable.Range(0, 64).Select(index =>
        {
            var run = fixture.Registry.Create(Request() with { SessionId = "retained-" + index });
            run.Cancel();
            return run;
        }).ToArray();
        var request = Request() with { SessionId = "entry-0" };
        var entry = fixture.Starts.Admit("seed", request, request, "seed-run");
        var path = Path.Combine(fixture.Root, "starts", HostStartStore.LedgerFileName);
        using var current = JsonDocument.Parse(File.ReadAllBytes(path));
        var storeId = current.RootElement.GetProperty("payload").GetProperty("storeId").GetString();
        var entries = Enumerable.Range(0, HostStartStore.MaximumEntries).Select(index =>
        {
            var bound = request with { SessionId = "entry-" + index };
            return entry with
            {
                OriginalRequest = bound,
                AcceptedRequest = bound,
                RunId = "entry-run-" + index,
                OriginalRequestHash = ProtocolValidation.Digest(bound),
                AcceptedRequestHash = ProtocolValidation.Digest(bound)
            };
        }).ToArray();
        var payload = new { storeId, entries };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            payload,
            contentHash = ProtocolValidation.Digest(payload)
        }, HostProtocol.Json));

        Assert.Equal("start.capacity", Assert.Throws<HostProtocolException>(() => fixture.Registry.CreateDurable(Submission())).Code);

        Assert.All(retained, run => Assert.Same(run, fixture.Registry.Get(run.RunId)));
        Assert.Equal(64, fixture.Registry.List().Length);
        Assert.Null(fixture.Starts.Find(new("host", "session", "attempt-one")));
        Assert.Equal(0, fixture.Planners.Created);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    private async Task<HostRunRecoveryPage> ReadRunPage(Service service, string hostId = "host", string sessionId = "session",
        string sessionEpoch = "epoch", string runId = "run", string? afterActionId = null)
    {
        var before = StorageSnapshot();
        var planners = service.Planners.Created;
        var runs = service.Registry.List().Length;
        var pruned = service.Custody.PrunedCompletedCount;
        var query = RunQuery(hostId, sessionId, sessionEpoch, runId, afterActionId);
        using var response = await service.Http.GetAsync("api/recovery/run?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(HostProtocol.Json);
        Assert.Equal(new[] { "hostId", "sessionId", "sessionEpoch", "runId", "items", "nextAfterActionId" },
            json.EnumerateObject().Select(property => property.Name));
        foreach (var item in json.GetProperty("items").EnumerateArray())
            Assert.Equal(new[] { "actionId", "requestHash", "capabilityHash", "disposition", "recoveryRetention" },
                item.EnumerateObject().Select(property => property.Name));
        var page = json.Deserialize<HostRunRecoveryPage>(HostProtocol.Json)!;
        Assert.Equal((hostId, sessionId, sessionEpoch, runId), (page.HostId, page.SessionId, page.SessionEpoch, page.RunId));
        Assert.InRange(page.Items.Count, 0, 16);
        Assert.Equal(before, StorageSnapshot());
        Assert.Equal(planners, service.Planners.Created);
        Assert.Equal(runs, service.Registry.List().Length);
        Assert.Equal(pruned, service.Custody.PrunedCompletedCount);
        return page;
    }

    private static string RunQuery(string hostId, string sessionId, string sessionEpoch, string runId, string? afterActionId = null)
    {
        var fields = new Dictionary<string, string> { ["hostId"] = hostId, ["sessionId"] = sessionId,
            ["sessionEpoch"] = sessionEpoch, ["runId"] = runId };
        if (afterActionId is not null) fields["afterActionId"] = afterActionId;
        return string.Join("&", fields.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
    }

    private static ActionCustodyEntry ReserveRunAction(Service service, string actionId, string hostId = "host",
        string sessionId = "session", string sessionEpoch = "epoch", string runId = "run")
    {
        var request = Request();
        var action = new HostActionRequest(actionId, runId, "runner", "step", sessionId, sessionEpoch,
            request.Capabilities[0].Id, ProtocolValidation.Digest(request.Capabilities),
            HostProtocol.Element(new { value = "private original arguments" }), request.Observation.Revision, DateTimeOffset.UtcNow.AddMinutes(1));
        return service.Custody.Reserve(hostId, sessionId, sessionEpoch, runId, action, request.Capabilities[0], request.ObjectiveId);
    }

    private static void AssertRunItem(ActionCustodyEntry expected, HostRunRecoveryItem actual) =>
        Assert.Equal(new(expected.Request.ActionId, expected.RequestHash, expected.CapabilityHash,
            expected.Result?.Disposition, expected.RecoveryRetention), actual);
}

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Outcomes;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostRuntimeTests
{
    private const int DisplayFrameLimit = 64 * 1024;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8000, 3)]
    public async Task Terminal_and_reconnect_use_compact_frames_without_changing_full_http_evidence(int observationSize, int stepCount)
    {
        await using var service = await SocketService.StartAsync();
        var socket = await service.ConnectAsync();
        var request = Request() with { Provider = new ProviderSettings("fixture", "large-outcome") };
        await SendAsync(socket, "start.durable", new HostStartSubmission("compact-outcome", request));
        var started = await ReceiveUntilAsync(socket, "started");
        AssertDisplayBound(started);
        var runId = started.GetProperty("runId").GetString()!;
        var run = service.Track(runId);
        for (var revision = 1; revision <= stepCount; revision++)
        {
            var action = (await ReceiveUntilAsync(socket, "action.request")).GetProperty("payload")
                .Deserialize<HostActionRequest>(HostProtocol.Json)!;
            var result = Applied(action, revision, complete: revision == stepCount);
            await SendAsync(socket, "action.result", result with
            {
                Observation = result.Observation! with
                {
                    Data = HostProtocol.Element(new { revision, detail = new string((char)('a' + revision), observationSize) })
                }
            }, runId);
        }

        var terminal = await ReceiveUntilAsync(socket, "outcome");
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        using var http = new HttpClient { BaseAddress = service.BaseAddress };
        var fullBefore = await http.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}", HostProtocol.Json);
        var fullOutcome = fullBefore.GetProperty("outcome");
        var fullBytes = Encoding.UTF8.GetByteCount(fullOutcome.GetRawText());
        // Exercise both small outcomes and the old 64-128 KiB projection gap.
        Assert.InRange(fullBytes, observationSize == 0 ? 1 : DisplayFrameLimit + 1,
            observationSize == 0 ? DisplayFrameLimit : HostProtocol.MaxMessageBytes / 2);
        var summary = AssertCompactOutcome(terminal, fullOutcome);
        var observations = fullOutcome.GetProperty("details").GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(stepCount, observations.Length);
        for (var i = 0; i < observations.Length; i++)
        {
            var data = observations[i].GetProperty("data").GetProperty("observation").GetProperty("data");
            Assert.Equal(i + 1, data.GetProperty("revision").GetInt32());
            Assert.Equal(new string((char)('b' + i), observationSize), data.GetProperty("detail").GetString());
        }
        Assert.NotEmpty(fullOutcome.GetProperty("outcome").GetProperty("completionEvidence").EnumerateArray());
        Assert.Equal(stepCount, fullOutcome.GetProperty("details").GetProperty("toolSurfaces").GetArrayLength());
        Assert.NotEmpty(fullOutcome.GetProperty("details").GetProperty("planningFrames").EnumerateArray());
        Assert.NotEmpty(fullOutcome.GetProperty("details").GetProperty("events").EnumerateArray());
        var digest = ProtocolValidation.Digest(fullOutcome);
        Assert.Equal(digest, ProtocolValidation.Digest(Snapshot(run).GetProperty("outcome")));

        socket.Abort();
        await WaitUntilAsync(() => !Snapshot(run).GetProperty("connected").GetBoolean());
        var reconnected = await service.ConnectAsync();
        await SendAsync(reconnected, "resume", new ResumeRequest(runId, request.SessionId, request.SessionEpoch), runId);
        var resumedCount = 0;
        while (true)
        {
            var message = await ReceiveAsync(reconnected);
            AssertDisplayBound(message);
            Assert.Equal(runId, message.GetProperty("runId").GetString());
            if (Type(message) == "outcome")
            {
                Assert.Equal(ProtocolValidation.Digest(summary), ProtocolValidation.Digest(AssertCompactOutcome(message, fullOutcome)));
                break;
            }
            if (Type(message) == "resumed")
            {
                resumedCount++;
                Assert.Equal(ProtocolValidation.Digest(summary),
                    ProtocolValidation.Digest(message.GetProperty("payload").GetProperty("outcome")));
                Assert.Equal(JsonValueKind.Null, message.GetProperty("payload").GetProperty("context").ValueKind);
            }
            else Assert.Equal("connection", Type(message));
        }
        // Both the correlated resume response and ReplayPendingAsync's snapshot are bounded.
        Assert.Equal(2, resumedCount);
        var fullAfter = await http.GetFromJsonAsync<JsonElement>(summary.GetProperty("snapshotUrl").GetString(), HostProtocol.Json);
        Assert.Equal(digest, ProtocolValidation.Digest(fullAfter.GetProperty("outcome")));
        Assert.Equal(ProtocolValidation.Digest(fullBefore.GetProperty("context")),
            ProtocolValidation.Digest(fullAfter.GetProperty("context")));
    }

    private static void AssertDisplayBound(JsonElement message) =>
        Assert.InRange(Encoding.UTF8.GetByteCount(message.GetRawText()), 1, DisplayFrameLimit);

    private static JsonElement AssertCompactOutcome(JsonElement terminal, JsonElement fullOutcome)
    {
        AssertDisplayBound(terminal);
        var summary = terminal.GetProperty("payload");
        Assert.Equal(["observationCount", "outcome", "receiptCount", "snapshotUrl", "truncated"],
            summary.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(summary.GetProperty("truncated").GetBoolean());
        Assert.Equal($"/api/runs/{terminal.GetProperty("runId").GetString()}", summary.GetProperty("snapshotUrl").GetString());
        Assert.Equal(3, summary.GetProperty("outcome").EnumerateObject().Count());
        foreach (var name in new[] { "runId", "status", "stopReason" })
            Assert.Equal(fullOutcome.GetProperty("outcome").GetProperty(name).GetRawText(), summary.GetProperty("outcome").GetProperty(name).GetRawText());
        Assert.Equal(fullOutcome.GetProperty("receipts").GetProperty("items").GetArrayLength(), summary.GetProperty("receiptCount").GetInt32());
        Assert.Equal(fullOutcome.GetProperty("details").GetProperty("observations").GetArrayLength(), summary.GetProperty("observationCount").GetInt32());
        return summary;
    }

    private static async Task<OutcomeEnvelope> ReadFullOutcomeAsync(SocketService service, JsonElement terminal)
    {
        using var http = new HttpClient { BaseAddress = service.BaseAddress };
        var snapshot = await http.GetFromJsonAsync<JsonElement>($"/api/runs/{terminal.GetProperty("runId").GetString()}", HostProtocol.Json);
        var fullOutcome = snapshot.GetProperty("outcome");
        AssertCompactOutcome(terminal, fullOutcome);
        return fullOutcome.Deserialize<OutcomeEnvelope>(HostProtocol.Json)!;
    }
}

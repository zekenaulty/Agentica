using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Mcp;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentica.Tests;

public sealed class McpRuntimeAcceptanceTests
{
    private const string ToolId = "mcp.fixture.lookup";
    private const string Schema =
        "{\"type\":\"object\",\"properties\":{\"recordId\":{\"type\":\"string\"}},\"required\":[\"recordId\"],\"additionalProperties\":false}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runner_accepts_only_the_expected_record_from_a_bound_sdk_tool(bool recordFound)
    {
        await using var server = new SdkFixtureServer(recordFound);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var transport = await SdkMcpToolTransport.ConnectHttpAsync(
            "fixture", server.Endpoint, timeout.Token, SdkFixtureServer.BearerToken);
        var binding = McpReadOnlyBindingFactory.Create("fixture", "lookup",
            McpSchemaFingerprint.Sha256(Schema), ToolId, "Look up a fixture record",
            "Read one immutable record from the installed fixture service.", Schema);
        var catalog = ToolCatalog.Create((await McpToolCatalogAdapter.BindAsync(
            transport, [binding], timeout.Token)).ToArray());
        var planner = new RecordPlanner();
        var runner = new AgenticaRunner(planner, catalog, new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 2, MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly, MaxBlockedRetries: 0),
            new RecordCompletionEvaluator());

        var envelope = await runner.RunAsync(new RunRequest(
            "Read record-42 and verify its expected title and version.",
            AuthorizationScopeId: "fixture-read-scope"), timeout.Token);

        Assert.Equal(recordFound ? RunOutcomeStatus.Succeeded : RunOutcomeStatus.Failed,
            envelope.Outcome.Status);
        Assert.Equal(recordFound ? StopReason.Complete : StopReason.CompletionNotSatisfied,
            envelope.Outcome.StopReason);
        Assert.Equal(1, server.CallCount);
        Assert.Equal("record-42", server.RequestedRecordId);
        Assert.Equal(0, server.UnboundCallCount);
        Assert.True(server.ListCount >= 2);
        Assert.Equal([ToolId], planner.VisibleToolIds);
        Assert.Null(catalog.Resolve("unbound"));
        Assert.Empty(envelope.Details.GrantConsumptions);
        Assert.Empty(envelope.Details.ValidationIssues);

        // Both remote calls succeed at the protocol level. Only the host's record predicate
        // determines objective completion, including when the text claims success.
        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal(ReceiptStatus.Succeeded, receipt.Status);
        Assert.Equal(ToolId, receipt.ToolId);
        Assert.Equal("lookup-step", receipt.StepId);
        Assert.Equal("fixture", receipt.Data["mcp.serverId"]);
        Assert.Equal("lookup", receipt.Data["mcp.remoteName"]);
        Assert.Equal(binding.ExpectedInputSchemaSha256, receipt.Data["mcp.schemaSha256"]);
        Assert.NotNull(receipt.Data["mcp.contentSha256"]);
        var observation = Assert.Single(envelope.Details.Observations);
        Assert.Equal(receipt.StepId, observation.StepId);
        Assert.Contains(new EvidenceRef("receipt", receipt.ReceiptId), observation.Evidence);
        if (recordFound)
        {
            Assert.Equal([new EvidenceRef("receipt", receipt.ReceiptId),
                new EvidenceRef("observation", observation.ObservationId)],
                envelope.Outcome.CompletionEvidence);
        }
        else
        {
            Assert.Empty(envelope.Outcome.CompletionEvidence);
        }
        Assert.DoesNotContain(SdkFixtureServer.BearerToken, JsonSerializer.Serialize(envelope),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lab_inspect_uses_the_host_bearer_token_without_exposing_it()
    {
        await using var server = new SdkFixtureServer(recordFound: true);
        var result = await RunLabAsync(new Dictionary<string, string>
        {
            ["AGENTICA_MCP_BEARER_TOKEN"] = SdkFixtureServer.BearerToken
        }, "mcp-inspect", server.Endpoint.AbsoluteUri, "fixture");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Name\":\"lookup\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(McpSchemaFingerprint.Sha256(Schema), result.StandardOutput, StringComparison.Ordinal);
        Assert.True(server.ListCount > 0);
        Assert.Equal(0, server.CallCount);
        Assert.DoesNotContain(SdkFixtureServer.BearerToken,
            result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AGENTICA_MCP_ENDPOINT")]
    [InlineData("AGENTICA_MCP_BINDINGS_FILE")]
    public async Task Lab_deterministic_run_rejects_mcp_before_connecting(string setting)
    {
        await using var server = new SdkFixtureServer(recordFound: true);
        var result = await RunLabAsync(new Dictionary<string, string>
        {
            [setting] = setting == "AGENTICA_MCP_ENDPOINT"
                ? server.Endpoint.AbsoluteUri : "missing-fixture-manifest.json"
        }, "run", "Read the installed record", "--planner", "deterministic");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("deterministic run planner supports demo tools only",
            result.StandardError, StringComparison.Ordinal);
        Assert.Contains("host-authored planner", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, server.RequestCount);
    }

    private sealed class RecordPlanner : IWorkflowPlanner
    {
        public string[] VisibleToolIds { get; private set; } = [];

        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            VisibleToolIds = request.ToolDescriptors.Select(tool => tool.ToolId).ToArray();
            return Task.FromResult(new WorkflowPlan("lookup-plan", 1,
                [new PlanStep("lookup-step", ToolId, ToolKind.Query, ToolEffect.ReadOnly,
                    new Dictionary<string, object?> { ["recordId"] = "record-42" })],
                "Read the record needed by the host acceptance predicate."));
        }

        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This bounded read requires no refinement.");
    }

    private sealed class RecordCompletionEvaluator : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context)
        {
            var receipt = context.Receipts.SingleOrDefault(item =>
                item.ToolId == ToolId && item.Status == ReceiptStatus.Succeeded);
            var observation = context.Observations.SingleOrDefault(item =>
                receipt is not null && item.StepId == receipt.StepId &&
                item.Evidence.Contains(new EvidenceRef("receipt", receipt.ReceiptId)));
            if (receipt is not null && observation?.Data["structuredJson"] is string json)
            {
                using var document = JsonDocument.Parse(json);
                var record = document.RootElement;
                if (record.GetProperty("found").GetBoolean() &&
                    record.GetProperty("id").GetString() == "record-42" &&
                    record.GetProperty("title").GetString() == "Bounded runtime" &&
                    record.GetProperty("version").GetInt32() == 7)
                {
                    return CompletionEvaluation.Complete(
                        new EvidenceRef("receipt", receipt.ReceiptId),
                        new EvidenceRef("observation", observation.ObservationId));
                }
            }
            return CompletionEvaluation.Failed(StopReason.CompletionNotSatisfied,
                "The expected record was not verified by receipt-linked structured data.");
        }
    }

    // Owns only its ephemeral listener and SDK session. HTTP authentication and routing live
    // here; initialization, tools/list, tools/call, and SSE responses are handled by the SDK.
    private sealed class SdkFixtureServer : IAsyncDisposable
    {
        internal const string BearerToken = "agentica-fixture-only-token";
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly StreamableHttpServerTransport _transport = new();
        private readonly McpServer _server;
        private readonly Task _runServer;
        private readonly Task _serveHttp;

        public SdkFixtureServer(bool recordFound)
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            var origin = $"http://127.0.0.1:{port}/";
            Endpoint = new Uri(origin + "mcp");
            _listener.Prefixes.Add(origin);
            _server = McpServer.Create(_transport, new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "agentica-sdk-fixture", Version = "1.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) =>
                    {
                        ListCount++;
                        return ValueTask.FromResult(new ListToolsResult
                        {
                            Tools = [
                                new ModelContextProtocol.Protocol.Tool { Name = "lookup",
                                    InputSchema = JsonSerializer.Deserialize<JsonElement>(Schema) },
                                new ModelContextProtocol.Protocol.Tool { Name = "unbound",
                                    InputSchema = JsonSerializer.Deserialize<JsonElement>(Schema) }
                            ]
                        });
                    },
                    CallToolHandler = (request, _) =>
                    {
                        if (request.Params?.Name != "lookup")
                        {
                            UnboundCallCount++;
                            throw new InvalidOperationException("An unbound tool was invoked.");
                        }
                        CallCount++;
                        RequestedRecordId = request.Params.Arguments?["recordId"].GetString();
                        return ValueTask.FromResult(new CallToolResult
                        {
                            IsError = false,
                            Content = [new TextContentBlock { Text = "Lookup completed successfully." }],
                            StructuredContent = JsonSerializer.SerializeToElement(new
                            {
                                found = recordFound && RequestedRecordId == "record-42",
                                id = "record-42",
                                title = "Bounded runtime",
                                version = 7
                            })
                        });
                    }
                }
            });
            _listener.Start();
            _runServer = _server.RunAsync(_stop.Token);
            _serveHttp = ServeHttpAsync();
        }

        public Uri Endpoint { get; }
        public int RequestCount { get; private set; }
        public int ListCount { get; private set; }
        public int CallCount { get; private set; }
        public int UnboundCallCount { get; private set; }
        public string? RequestedRecordId { get; private set; }

        private async Task ServeHttpAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    RequestCount++;
                    try
                    {
                        if (context.Request.Headers["Authorization"] != "Bearer " + BearerToken)
                        {
                            context.Response.StatusCode = 401;
                            continue;
                        }
                        if (context.Request.HttpMethod != "POST")
                        {
                            context.Response.StatusCode = 405;
                            continue;
                        }
                        var message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(
                            context.Request.InputStream, McpJsonUtilities.DefaultOptions, _stop.Token)
                            ?? throw new InvalidOperationException("Missing JSON-RPC message.");
                        context.Response.ContentType = "text/event-stream";
                        context.Response.SendChunked = true;
                        if (!await _transport.HandlePostRequestAsync(message,
                            context.Response.OutputStream, _stop.Token))
                        {
                            context.Response.StatusCode = 202;
                        }
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _serveHttp;
            try { await _runServer; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            await _server.DisposeAsync();
            _stop.Dispose();
        }
    }

    private static async Task<ProcessResult> RunLabAsync(
        IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"agentica-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Agentica.Lab.dll"));
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var name in start.Environment.Keys.Where(name =>
                name.StartsWith("AGENTICA_MCP_", StringComparison.Ordinal)).ToArray())
                start.Environment.Remove(name);
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the Lab process.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                return new ProcessResult(process.ExitCode, await output, await error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

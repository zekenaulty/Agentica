using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Mcp;
using Agentica.NyxLearningHost.Mcp;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Projection;
using Agentica.Requests;
using Agentica.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentica.Tests;

public sealed class NyxFacultyMcpConformanceTests
{
    [Theory]
    [InlineData("available", true, ReceiptStatus.Succeeded, ObligationResolutionState.Ready)]
    [InlineData("partial", false, ReceiptStatus.Succeeded, ObligationResolutionState.Ready)]
    [InlineData("unavailable", false, ReceiptStatus.Succeeded, ObligationResolutionState.Unavailable)]
    [InlineData("error", false, ReceiptStatus.Failed, ObligationResolutionState.Indeterminate)]
    [InlineData("changed", false, ReceiptStatus.Failed, ObligationResolutionState.Indeterminate)]
    public async Task In_process_and_real_sdk_calls_preserve_exact_cut_and_host_acceptance(
        string condition, bool accepted, ReceiptStatus receiptStatus, ObligationResolutionState resolution)
    {
        using var timeout = Timeout();
        var manifest = Manifest(condition == "partial" ? ObligationCoverageStatus.Partial : ObligationCoverageStatus.Complete);
        var native = Faculty(manifest, condition);
        var remote = Faculty(manifest, condition);
        await using var server = new SdkFacultyServer(remote);
        await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, server.Endpoint, timeout.Token);
        var localInspection = await native.InspectAsync(native.InProcessTransport(), timeout.Token);
        var remoteInspection = await remote.InspectAsync(sdk, timeout.Token);
        var localRun = await RunAsync(native, localInspection, timeout.Token);
        var remoteRun = await RunAsync(remote, remoteInspection, timeout.Token);

        Assert.Equal(accepted, localRun.Outcome.Status == RunOutcomeStatus.Succeeded);
        Assert.Equal(accepted, remoteRun.Outcome.Status == RunOutcomeStatus.Succeeded);
        Assert.Equal(receiptStatus, Assert.Single(localRun.Receipts.Items).Status);
        Assert.Equal(receiptStatus, Assert.Single(remoteRun.Receipts.Items).Status);
        var localJson = Structured(localRun);
        var remoteJson = Structured(remoteRun);
        var result = HistoricalEvidenceContract.Deserialize(remoteJson);
        Assert.Equal(HistoricalEvidenceContract.Deserialize(localJson), result);
        Assert.Equal(resolution, result.Resolution);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(HistoricalEvidenceContract.OutputRevision, result.OutputRevision);
        Assert.Equal(manifest.ContentIdentity, result.ManifestContentIdentity);
        Assert.Equal(remote.InstalledRequest, result.Request);
        if (resolution == ObligationResolutionState.Ready)
        {
            Assert.Equal(manifest.PresentedEvidence[0], result.Evidence);
            Assert.Equal(manifest.Coverage, result.Coverage);
            Assert.Equal("At turn 7, speaker alpha said: “pending 🧭”.\nThe turn-9 correction is a separate event.", result.Evidence!.Text);
            Assert.Equal(19, result.Evidence.StartUtf16);
        }
        else
        {
            Assert.Null(result.Evidence);
            Assert.Equal(ObligationCoverageStatus.Unknown, result.Coverage.Status);
            Assert.Empty(remoteRun.Outcome.CompletionEvidence);
        }
        Assert.Equal(1, native.SourceReads);
        Assert.Equal(1, remote.SourceReads);
        Assert.Equal(1, server.CallCount);
        Assert.Equal(0, server.UnboundCalls);
        Assert.True(server.ListCount >= 2);
        Assert.Equal(HistoricalEvidenceContract.ProtocolVersion, server.NegotiatedProtocol);
        Assert.Same(remote.Binding, Assert.Single(remoteInspection.Statuses).Binding);
        Assert.Equal(HistoricalEvidenceContract.InputSchemaSha256,
            Assert.Single(remoteRun.Receipts.Items).Data["mcp.schemaSha256"]);
        Assert.DoesNotContain("fixture private connector diagnostic", remoteJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installed_source_scope_revision_and_exact_range_are_checked_before_remote_dispatch()
    {
        using var timeout = Timeout();
        var faculty = Faculty(Manifest());
        await using var server = new SdkFacultyServer(faculty);
        await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, server.Endpoint, timeout.Token);
        var registration = Assert.Single((await faculty.InspectAsync(sdk, timeout.Token)).ReadyRegistrations);
        var installed = faculty.InstalledRequest;
        HistoricalEvidenceRequest[] refused =
        [
            installed with { SourceId = "another-conversation" },
            installed with { SourceRevision = "newer-unpresented-revision" },
            installed with { ScopeId = "other-scope" },
            installed with { PerspectiveId = "current-belief" },
            installed with { StartUtf16 = 20 },
            installed with { LengthUtf16 = installed.LengthUtf16 - 1 }
        ];
        foreach (var request in refused)
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(
                Invocation(faculty, request), timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(
            Invocation(faculty) with { AuthorizationScopeId = "different-host-scope" }, timeout.Token));
        var malformed = HistoricalEvidenceContract.Arguments(installed).ToDictionary(pair => pair.Key,
            pair => pair.Value, StringComparer.Ordinal);
        malformed["lengthUtf16"] = "not-an-integer";
        await Assert.ThrowsAsync<ArgumentException>(() => registration.Tool.ExecuteAsync(
            Invocation(faculty) with { Input = malformed }, timeout.Token));
        Assert.Equal(0, server.CallCount);
        Assert.Equal(0, faculty.SourceReads);

        // The server's native boundary checks the same cut even if a caller bypasses the host wrapper.
        await Assert.ThrowsAsync<InvalidOperationException>(() => faculty.CallAsync(
            HistoricalEvidenceContract.RemoteName, HistoricalEvidenceContract.Arguments(refused[0]), timeout.Token));
        Assert.Equal(0, faculty.SourceReads);
    }

    [Fact]
    public async Task Advertised_unbound_tool_never_enters_the_runner_catalog_or_dispatches()
    {
        using var timeout = Timeout();
        var faculty = Faculty(Manifest());
        await using var server = new SdkFacultyServer(faculty);
        await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, server.Endpoint, timeout.Token);
        var inspection = await faculty.InspectAsync(sdk, timeout.Token);
        var catalog = ToolCatalog.Create(inspection.ReadyRegistrations.ToArray());
        Assert.Null(catalog.Resolve("history.unbound"));
        var run = await RunAsync(faculty, inspection, timeout.Token, "history.unbound");
        Assert.NotEqual(RunOutcomeStatus.Succeeded, run.Outcome.Status);
        Assert.NotEmpty(run.Details.ValidationIssues);
        Assert.Equal(0, server.CallCount);
        Assert.Equal(0, server.UnboundCalls);
        Assert.Equal(0, faculty.SourceReads);
    }

    [Fact]
    public async Task Real_sdk_drift_and_changed_host_server_identity_cannot_use_a_retained_registration()
    {
        using var timeout = Timeout();
        var faculty = Faculty(Manifest());
        await using var server = new SdkFacultyServer(faculty);
        await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, server.Endpoint, timeout.Token);
        var labelled = new RelabelledTransport(sdk);
        var ready = await faculty.InspectAsync(labelled, timeout.Token);
        var registration = Assert.Single(ready.ReadyRegistrations);
        server.InputSchemaJson = "{\"type\":\"object\"}";
        var drifted = await faculty.InspectAsync(labelled, timeout.Token);
        Assert.Same(faculty.Binding, Assert.Single(drifted.Statuses).Binding);
        Assert.Equal("mcp.schema_mismatch", drifted.Statuses[0].ReasonCode);
        Assert.Empty(drifted.ReadyRegistrations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(Invocation(faculty), timeout.Token));
        server.InputSchemaJson = HistoricalEvidenceContract.InputSchemaJson;
        labelled.ServerId = "different-server";
        Assert.Equal("mcp.server_mismatch", Assert.Single((await faculty.InspectAsync(labelled, timeout.Token)).Statuses).ReasonCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(Invocation(faculty), timeout.Token));
        labelled.ServerId = HistoricalEvidenceContract.ServerId;
        labelled.RelabelAfterDiscovery = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(Invocation(faculty), timeout.Token));
        Assert.Equal(0, server.CallCount);
        Assert.Equal(0, faculty.SourceReads);
        labelled.RelabelAfterDiscovery = false;
        labelled.ServerId = HistoricalEvidenceContract.ServerId;
        var recovered = await faculty.InspectAsync(labelled, timeout.Token);
        Assert.Equal(faculty.Binding.ExpectedInputSchemaSha256, Assert.Single(recovered.Statuses).Binding.ExpectedInputSchemaSha256);
        Assert.Equal(ReceiptStatus.Succeeded,
            (await registration.Tool.ExecuteAsync(Invocation(faculty), timeout.Token)).Receipt.Status);
        Assert.Equal(1, server.CallCount);
    }

    [Fact]
    public async Task Discovery_outage_and_real_server_restart_preserve_the_original_binding_and_cut()
    {
        using var timeout = Timeout();
        var faculty = Faculty(Manifest());
        var originalBinding = faculty.Binding;
        Uri endpoint;
        string before;
        await using (var server = new SdkFacultyServer(faculty))
        {
            endpoint = server.Endpoint;
            await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
                HistoricalEvidenceContract.ServerId, endpoint, timeout.Token);
            before = Structured(await RunAsync(faculty, await faculty.InspectAsync(sdk, timeout.Token), timeout.Token));
            server.DiscoveryUnavailable = true;
            var unavailable = await faculty.InspectAsync(sdk, timeout.Token);
            Assert.Same(originalBinding, Assert.Single(unavailable.Statuses).Binding);
            Assert.Equal("mcp.discovery_unavailable", unavailable.Statuses[0].ReasonCode);
            Assert.False(unavailable.Statuses[0].RuntimeReady);
            Assert.Empty(unavailable.ReadyRegistrations);
        }
        var outage = await faculty.InspectAsync(null, timeout.Token);
        Assert.Same(originalBinding, Assert.Single(outage.Statuses).Binding);
        Assert.Equal("mcp.connection_unavailable", outage.Statuses[0].ReasonCode);
        await using var restarted = new SdkFacultyServer(faculty, endpoint);
        await using var replacement = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, endpoint, timeout.Token);
        var ready = await faculty.InspectAsync(replacement, timeout.Token);
        Assert.Same(originalBinding, Assert.Single(ready.Statuses).Binding);
        Assert.True(ready.Statuses[0].RuntimeReady);
        var after = Structured(await RunAsync(faculty, ready, timeout.Token));
        Assert.Equal(HistoricalEvidenceContract.Deserialize(before), HistoricalEvidenceContract.Deserialize(after));
        Assert.Equal(HistoricalEvidenceContract.ProtocolVersion, restarted.NegotiatedProtocol);
        Assert.Equal(1, restarted.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_become_a_successful_read_or_trigger_an_implicit_retry()
    {
        using var timeout = Timeout();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifest = Manifest();
        var faculty = new HistoricalEvidenceFaculty(manifest, async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(HistoricalSourceReadState.Available, manifest.PresentedEvidence[0]);
        });
        await using var server = new SdkFacultyServer(faculty);
        await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, server.Endpoint, timeout.Token);
        var registration = Assert.Single((await faculty.InspectAsync(sdk, timeout.Token)).ReadyRegistrations);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var pending = registration.Tool.ExecuteAsync(Invocation(faculty), caller.Token);
        try
        {
            await started.Task.WaitAsync(timeout.Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(1, server.CallCount);
            Assert.Equal(1, faculty.SourceReads);
        }
        finally { release.TrySetResult(); }
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.Tool.ExecuteAsync(Invocation(faculty), stopped.Token));
        Assert.Equal(1, server.CallCount);
    }

    [Theory]
    [InlineData("outputRevision")]
    [InlineData("sourceKind")]
    [InlineData("segmentId")]
    [InlineData("attribution")]
    public async Task Output_revision_and_evidence_remain_host_acceptance_checks_after_protocol_success(string changed)
    {
        using var timeout = Timeout();
        var faculty = Faculty(Manifest());
        await using var server = new SdkFacultyServer(faculty)
        {
            RewriteResult = result => result with
            {
                StructuredContentJson = Mutate(result.StructuredContentJson!, changed)
            }
        };
        await using var sdk = await SdkMcpToolTransport.ConnectHttpAsync(
            HistoricalEvidenceContract.ServerId, server.Endpoint, timeout.Token);
        var run = await RunAsync(faculty, await faculty.InspectAsync(sdk, timeout.Token), timeout.Token);
        Assert.Equal(ReceiptStatus.Succeeded, Assert.Single(run.Receipts.Items).Status);
        Assert.NotEqual(RunOutcomeStatus.Succeeded, run.Outcome.Status);
        Assert.Empty(run.Outcome.CompletionEvidence);
        Assert.Equal(1, server.CallCount);

        static string Mutate(string json, string changed)
        {
            var node = JsonNode.Parse(json)!;
            switch (changed)
            {
                case "outputRevision": node["outputRevision"] = "history.read.output.future"; break;
                case "sourceKind": node["evidence"]!["source"]!["kind"] = "unadmitted-source-kind"; break;
                case "segmentId": node["evidence"]!["segmentId"] = "other-segment"; break;
                case "attribution": node["evidence"]!["attributionId"] = "speaker.other"; break;
                default: throw new ArgumentOutOfRangeException(nameof(changed));
            }
            return node.ToJsonString();
        }
    }

    [Fact]
    public void Installed_metadata_and_source_bounds_are_explicit_and_do_not_claim_obligation_fulfillment()
    {
        var faculty = Faculty(Manifest());
        Assert.Equal("2.2.0", HistoricalEvidenceContract.SdkPackageVersion);
        Assert.Equal(ToolEffect.ReadOnly, faculty.Binding.Descriptor.Effect);
        Assert.Equal(ToolApprovalRequirement.None, faculty.Binding.Security.ApprovalRequirement);
        Assert.NotEmpty(HistoricalEvidenceContract.Semantics.Requires);
        Assert.NotEmpty(HistoricalEvidenceContract.Semantics.Produces);
        Assert.NotEmpty(HistoricalEvidenceContract.Semantics.Applies);
        Assert.NotEmpty(HistoricalEvidenceContract.Semantics.Effects);
        Assert.Equal(64, HistoricalEvidenceContract.InputSchemaSha256.Length);
        Assert.Equal(64, HistoricalEvidenceContract.OutputSchemaSha256.Length);
        Assert.Contains("consumer acceptance", faculty.Binding.Descriptor.ContextHint!.NotEnoughWhen!, StringComparison.Ordinal);
        var empty = new ObligationInputManifest(Manifest().Obligation, "Read the exact historical cut.",
            "scope.history", "speaker.alpha.asof9", [], Manifest().Coverage,
            "fixture.compiler.v1", "history.read.input.v1", "history.read.output.v1", "no-generation.v1");
        Assert.Throws<ArgumentException>(() => new HistoricalEvidenceFaculty(empty));
    }

    private static ObligationInputManifest Manifest(ObligationCoverageStatus coverage = ObligationCoverageStatus.Complete) => new(
        new("history.review", "history.objective", "history.accept", "1", "conversation.synthetic", "turn9"),
        "Read the exact turn-7 statement through the turn-9 source horizon.", "scope.history", "speaker.alpha.asof9",
        [new("turn7.excerpt", new EvidenceRef("source", "conversation.synthetic.turn7"), "revision.3", 19,
            "At turn 7, speaker alpha said: “pending 🧭”.\nThe turn-9 correction is a separate event.", "speaker.alpha")],
        new(coverage, "Only the installed turn-7 excerpt, source revision 3, as of turn 9.", "synthetic.history.v1"),
        "fixture.compiler.v1", HistoricalEvidenceContract.InputRevision, HistoricalEvidenceContract.OutputRevision, "no-generation.v1");

    private static HistoricalEvidenceFaculty Faculty(ObligationInputManifest manifest, string condition = "available") =>
        new(manifest, _ => condition switch
        {
            "unavailable" => throw new IOException("fixture private connector diagnostic"),
            "error" => ValueTask.FromResult(new HistoricalSourceRead(HistoricalSourceReadState.Error)),
            "changed" => ValueTask.FromResult(new HistoricalSourceRead(HistoricalSourceReadState.Available,
                manifest.PresentedEvidence[0] with { AttributionId = "speaker.other" })),
            _ => ValueTask.FromResult(new HistoricalSourceRead(HistoricalSourceReadState.Available, manifest.PresentedEvidence[0]))
        });

    private static ToolInvocation Invocation(HistoricalEvidenceFaculty faculty, HistoricalEvidenceRequest? request = null) =>
        new("faculty-run", "faculty-read", HistoricalEvidenceContract.ToolId,
            HistoricalEvidenceContract.Arguments(request ?? faculty.InstalledRequest), faculty.InstalledRequest.ScopeId);

    private static async Task<OutcomeEnvelope> RunAsync(HistoricalEvidenceFaculty faculty,
        McpBindingInspection inspection, CancellationToken token, string toolId = HistoricalEvidenceContract.ToolId)
    {
        var catalog = ToolCatalog.Create(inspection.ReadyRegistrations.ToArray());
        var runner = new AgenticaRunner(new ReadPlanner(faculty, toolId), catalog, new InMemoryEventSink(),
            new DeterministicOutcomeReporter(), new ExecutionPolicy(MaxSteps: 2, MaxRefinements: 0,
                MaxBlockedRetries: 0, PlanningMode: PlanningMode.PlanOnly), new ExactCutCompletion(faculty));
        return await runner.RunAsync(new RunRequest("Read the installed historical source cut and verify its exact evidence.",
            AuthorizationScopeId: faculty.InstalledRequest.ScopeId), token);
    }

    private static string Structured(OutcomeEnvelope outcome) =>
        Assert.IsType<string>(Assert.Single(outcome.Details.Observations).Data["structuredJson"]);

    private static CancellationTokenSource Timeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }

    private sealed class ReadPlanner(HistoricalEvidenceFaculty faculty, string toolId) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan("faculty-plan", 1,
                [new("faculty-read", toolId, ToolKind.Query, ToolEffect.ReadOnly,
                    HistoricalEvidenceContract.Arguments(faculty.InstalledRequest))],
                "Read the installed exact cut; the host separately evaluates its evidence."));

        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("One exact read requires no refinement.");
    }

    private sealed class ExactCutCompletion(HistoricalEvidenceFaculty faculty) : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context)
        {
            var receipt = context.Receipts.SingleOrDefault(item => item.Status == ReceiptStatus.Succeeded &&
                item.ToolId == HistoricalEvidenceContract.ToolId);
            var observation = context.Observations.SingleOrDefault(item => receipt is not null &&
                item.StepId == receipt.StepId && item.Evidence.Contains(new EvidenceRef("receipt", receipt.ReceiptId)));
            if (observation?.Data["structuredJson"] is string json)
            {
                var result = HistoricalEvidenceContract.Deserialize(json);
                if (result.SchemaVersion == 1 && result.OutputRevision == HistoricalEvidenceContract.OutputRevision &&
                    result.Resolution == ObligationResolutionState.Ready && result.Request == faculty.InstalledRequest &&
                    result.Coverage.Status == ObligationCoverageStatus.Complete &&
                    result.Evidence == Manifest().PresentedEvidence[0] &&
                    result.ManifestContentIdentity == Manifest().ContentIdentity &&
                    result.Coverage == Manifest().Coverage)
                    return CompletionEvaluation.Complete(new EvidenceRef("receipt", receipt!.ReceiptId),
                        new EvidenceRef("observation", observation.ObservationId));
            }
            return CompletionEvaluation.Failed(StopReason.CompletionNotSatisfied,
                "The host did not verify the exact source, revision, perspective and complete declared coverage.");
        }
    }

    private sealed class RelabelledTransport(IMcpToolTransport inner) : IMcpToolTransport
    {
        public string ServerId { get; set; } = HistoricalEvidenceContract.ServerId;
        public bool RelabelAfterDiscovery { get; set; }

        public async Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            var tools = await inner.ListToolsAsync(cancellationToken);
            if (RelabelAfterDiscovery) ServerId = "changed-during-discovery";
            return tools;
        }

        public Task<McpCallResult> CallToolAsync(string remoteName, IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken) => inner.CallToolAsync(remoteName, input, cancellationToken);
    }

    // The HTTP wrapper only routes bounded fixture requests. The pinned SDK implements
    // server discovery, protocol metadata, tool dispatch, structured content and SSE.
    private sealed class SdkFacultyServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly StreamableHttpServerTransport _transport = new();
        private readonly McpServer _server;
        private readonly Task _runServer;
        private readonly Task _serveHttp;
        private readonly List<Task> _requests = [];
        private int _listCount;
        private int _callCount;
        private int _unboundCalls;

        public SdkFacultyServer(HistoricalEvidenceFaculty faculty, Uri? endpoint = null)
        {
            if (endpoint is null)
            {
                var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                socket.Stop();
                endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            }
            Endpoint = endpoint;
            _listener.Prefixes.Add(endpoint.GetLeftPart(UriPartial.Authority) + "/");
            _server = McpServer.Create(_transport, new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = HistoricalEvidenceContract.ImplementationName,
                    Version = HistoricalEvidenceContract.ImplementationRevision
                },
                ProtocolVersion = HistoricalEvidenceContract.ProtocolVersion,
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) =>
                    {
                        Interlocked.Increment(ref _listCount);
                        if (DiscoveryUnavailable) throw new McpException("Fixture discovery unavailable.");
                        return ValueTask.FromResult(new ListToolsResult
                        {
                            Tools =
                            [
                                new ModelContextProtocol.Protocol.Tool
                                {
                                    Name = HistoricalEvidenceContract.RemoteName,
                                    InputSchema = JsonSerializer.Deserialize<JsonElement>(InputSchemaJson),
                                    OutputSchema = JsonSerializer.Deserialize<JsonElement>(HistoricalEvidenceContract.OutputSchemaJson)
                                },
                                new ModelContextProtocol.Protocol.Tool
                                {
                                    Name = "unbound", InputSchema = JsonSerializer.Deserialize<JsonElement>(InputSchemaJson)
                                }
                            ]
                        });
                    },
                    CallToolHandler = async (request, token) =>
                    {
                        if (request.Params?.Name != HistoricalEvidenceContract.RemoteName)
                        {
                            Interlocked.Increment(ref _unboundCalls);
                            throw new InvalidOperationException("Unbound fixture capability was called.");
                        }
                        Interlocked.Increment(ref _callCount);
                        var arguments = request.Params.Arguments?.ToDictionary(pair => pair.Key,
                            pair => (object?)pair.Value, StringComparer.Ordinal) ?? [];
                        var result = await faculty.CallAsync(request.Params.Name, arguments, token);
                        result = RewriteResult?.Invoke(result) ?? result;
                        return new CallToolResult
                        {
                            IsError = result.IsError,
                            Content = result.TextBlocks.Select(text => (ContentBlock)new TextContentBlock { Text = text }).ToArray(),
                            StructuredContent = JsonSerializer.Deserialize<JsonElement>(result.StructuredContentJson!)
                        };
                    }
                }
            });
            _listener.Start();
            _runServer = _server.RunAsync(_stop.Token);
            _serveHttp = ServeHttpAsync();
        }

        public Uri Endpoint { get; }
        public string InputSchemaJson { get; set; } = HistoricalEvidenceContract.InputSchemaJson;
        public bool DiscoveryUnavailable { get; set; }
        public Func<McpCallResult, McpCallResult>? RewriteResult { get; init; }
        public string? NegotiatedProtocol => _server.NegotiatedProtocolVersion;
        public int ListCount => Volatile.Read(ref _listCount);
        public int CallCount => Volatile.Read(ref _callCount);
        public int UnboundCalls => Volatile.Read(ref _unboundCalls);

        private async Task ServeHttpAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    if (_requests.Count >= 128)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        continue;
                    }
                    // Cancellation notifications can arrive while a read response is pending.
                    _requests.Add(HandleAsync(context));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "POST")
                {
                    context.Response.StatusCode = 405;
                    return;
                }
                if (context.Request.ContentLength64 > 16_384)
                {
                    context.Response.StatusCode = 413;
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream);
                var characters = new char[16_385];
                var count = await reader.ReadBlockAsync(characters.AsMemory(), _stop.Token);
                if (count > 16_384)
                {
                    context.Response.StatusCode = 413;
                    return;
                }
                var message = JsonSerializer.Deserialize<JsonRpcMessage>(new string(characters, 0, count),
                    McpJsonUtilities.DefaultOptions) ?? throw new InvalidOperationException("Missing fixture message.");
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                if (!await _transport.HandlePostRequestAsync(message, context.Response.OutputStream, _stop.Token))
                    context.Response.StatusCode = 202;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            // Caller cancellation may close its response stream before the SDK finishes writing.
            catch (IOException) { }
            catch (HttpListenerException) { }
            finally { context.Response.Close(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _serveHttp;
            await Task.WhenAll(_requests);
            try { await _runServer; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            await _server.DisposeAsync();
            _stop.Dispose();
        }
    }
}

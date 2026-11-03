using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Mcp;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class McpSdkIntegrationTests
{
    private const string Schema =
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}";

    [Fact]
    public async Task Loopback_sdk_binding_discovers_calls_and_receipts_real_http_result()
    {
        await using var server = new LoopbackMcpServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var transport = await SdkMcpToolTransport.ConnectHttpAsync(
            "test-server", server.Endpoint, timeout.Token,
            bearerToken: "local-test-token");
        var binding = McpReadOnlyBindingFactory.Create(
            "test-server", "search", McpSchemaFingerprint.Sha256(Schema),
            "mcp.test.search", "Search", "Search a local fixture", Schema);
        var registration = Assert.Single(await McpToolCatalogAdapter.BindAsync(
            transport, [binding]));

        var result = await registration.Tool.ExecuteAsync(new ToolInvocation(
            "run-test", "step-test", "mcp.test.search",
            new Dictionary<string, object?> { ["query"] = "Agentica" }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("test-server", result.Receipt.Data["mcp.serverId"]);
        Assert.Equal("search", result.Receipt.Data["mcp.remoteName"]);
        Assert.Equal("remote data", result.Observation?.Data["text"]);
        Assert.Equal(1, server.CallCount);
        Assert.True(server.ListCount >= 2);
        Assert.Equal("Bearer local-test-token", server.CallAuthorization);
        Assert.Equal("Agentica", server.CallQuery);
    }

    [Fact]
    public async Task SDK_inspection_preserves_config_and_revalidates_the_same_pin_after_drift()
    {
        await using var server = new LoopbackMcpServer();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var binding = McpReadOnlyBindingFactory.Create("test-server", "search",
            McpSchemaFingerprint.Sha256(Schema), "mcp.test.search", "Search", "Host-owned fixture binding", Schema);
        var disconnected = await McpToolCatalogAdapter.InspectAsync(null, [binding], cancellationToken: timeout.Token);
        Assert.Same(binding, Assert.Single(disconnected.Statuses).Binding);
        Assert.Empty(disconnected.ReadyRegistrations); Assert.Equal(0, server.ListCount);
        await using var transport = await SdkMcpToolTransport.ConnectHttpAsync(
            "test-server", server.Endpoint, timeout.Token, bearerToken: "local-test-token");
        var initial = await McpToolCatalogAdapter.InspectAsync(transport, [binding], cancellationToken: timeout.Token);
        var registration = Assert.Single(initial.ReadyRegistrations);
        Assert.True(Assert.Single(initial.Statuses).RuntimeReady);
        server.InputSchemaJson = "{\"type\":\"object\"}";
        var drifted = await McpToolCatalogAdapter.InspectAsync(transport, [binding], cancellationToken: timeout.Token);
        Assert.Equal("mcp.schema_mismatch", Assert.Single(drifted.Statuses).ReasonCode);
        Assert.Same(binding, drifted.Statuses[0].Binding); Assert.Empty(drifted.ReadyRegistrations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(new(
            "run", "step", "mcp.test.search", new Dictionary<string, object?> { ["query"] = "fixture" }), timeout.Token));
        Assert.Equal(0, server.CallCount);
        server.InputSchemaJson = Schema;
        var recovered = await McpToolCatalogAdapter.InspectAsync(transport, [binding], cancellationToken: timeout.Token);
        Assert.True(Assert.Single(recovered.Statuses).RuntimeReady);
        Assert.Equal(binding.ExpectedInputSchemaSha256, recovered.Statuses[0].Binding.ExpectedInputSchemaSha256);
        var result = await Assert.Single(recovered.ReadyRegistrations).Tool.ExecuteAsync(new(
            "run", "step", "mcp.test.search", new Dictionary<string, object?> { ["query"] = "fixture" }), timeout.Token);
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status); Assert.Equal(1, server.CallCount);
        Assert.Equal("Bearer local-test-token", server.CallAuthorization);
        Assert.DoesNotContain("local-test-token", JsonSerializer.Serialize(recovered.Statuses), StringComparison.Ordinal);
    }

    private sealed class LoopbackMcpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        public LoopbackMcpServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            var origin = $"http://127.0.0.1:{port}/";
            Endpoint = new Uri(origin + "mcp");
            _listener.Prefixes.Add(origin);
            _listener.Start();
            _serve = ServeAsync();
        }

        public Uri Endpoint { get; }
        public int ListCount { get; private set; }
        public string InputSchemaJson { get; set; } = Schema;
        public int CallCount { get; private set; }
        public string? CallAuthorization { get; private set; }
        public string? CallQuery { get; private set; }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync()
                            .WaitAsync(_stop.Token);
                    }
                    catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
                    {
                        // Closing the listener can complete accept before cancellation wins.
                        break;
                    }
                    await HandleAsync(context);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (HttpListenerException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync(_stop.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (!root.TryGetProperty("id", out var id))
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }
            if (method == "server/discover")
            {
                await WriteJsonAsync(context.Response, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    jsonrpc = "2.0",
                    id = id.Clone(),
                    error = new { code = -32601, message = "Method not found" }
                }));
                return;
            }

            object result;
            switch (method)
            {
                case "initialize":
                    result = new
                    {
                        protocolVersion = root.GetProperty("params")
                            .GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "loopback-test", version = "1.0" }
                    };
                    break;
                case "tools/list":
                    ListCount++;
                    using (var schema = JsonDocument.Parse(InputSchemaJson))
                    {
                        result = new
                        {
                            tools = new[]
                            {
                                new { name = "search", description = "Remote hint",
                                    inputSchema = schema.RootElement.Clone() }
                            }
                        };
                    }
                    break;
                case "tools/call":
                    CallCount++;
                    CallAuthorization = context.Request.Headers["Authorization"];
                    CallQuery = root.GetProperty("params").GetProperty("arguments")
                        .GetProperty("query").GetString();
                    result = new
                    {
                        content = new[] { new { type = "text", text = "remote data" } },
                        isError = false,
                        structuredContent = new { count = 1 }
                    };
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected MCP method: {method}");
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result
            });
            await WriteJsonAsync(context.Response, bytes);
        }

        private async Task WriteJsonAsync(HttpListenerResponse response, byte[] bytes)
        {
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, _stop.Token);
            response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            // Close is the sole listener shutdown. Stop followed by Close repeats Unix
            // prefix removal, which can try to rebind the already released port.
            _listener.Close();
            await _serve;
            _stop.Dispose();
        }
    }
}

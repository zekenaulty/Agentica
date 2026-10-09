using System.Text.Json;
using System.Net.WebSockets;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Lab.Web.Transport;

namespace Agentica.Lab.Web;

public static class LabWebApplication
{
    public static WebApplication Create(string[] args, ILabPlannerFactory? plannerFactory = null, string? storageDirectory = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(LabWebApplication).Assembly.FullName
        });
        if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5078");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = HostProtocol.MaxMessageBytes);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = HostProtocol.Json.PropertyNamingPolicy;
            options.SerializerOptions.UnmappedMemberHandling = HostProtocol.Json.UnmappedMemberHandling;
            options.SerializerOptions.MaxDepth = HostProtocol.Json.MaxDepth;
            foreach (var converter in HostProtocol.Json.Converters) options.SerializerOptions.Converters.Add(converter);
        });
        if (plannerFactory is null) builder.Services.AddSingleton<ILabPlannerFactory, LabPlannerFactory>();
        else builder.Services.AddSingleton(plannerFactory);
        var storageRoot = storageDirectory ?? builder.Configuration["Agentica:StorageDirectory"] ??
            Path.Combine(builder.Environment.ContentRootPath, ".agentica", "lab-web");
        builder.Services.AddSingleton(new HostContextStore(Path.Combine(storageRoot, "context")));
        builder.Services.AddSingleton(new ActionCustodyStore(Path.Combine(storageRoot, "custody")));
        builder.Services.AddSingleton<HostRunRegistry>();
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context).ConfigureAwait(false); }
            catch (HostProtocolException exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = exception.Code == "run.not_found" ? 404 : 409;
                await context.Response.WriteAsJsonAsync(new { code = exception.Code, message = exception.Message }).ConfigureAwait(false);
            }
            catch (Exception exception) when (!context.Response.HasStarted && exception is InvalidDataException or IOException)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { code = "storage.unavailable", message = "Persistent evidence could not be read or written. Resolve storage before further dispatch." }).ConfigureAwait(false);
            }
        });
        app.UseCors();
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/health", () => new { status = "ready", protocolVersion = HostProtocol.Version });
        app.MapGet("/api/providers", (ILabPlannerFactory factory) =>
            factory.GetProviders().Prepend(new ProviderMetadata("demo", "scripted-inventory", true, null, "scripted-fixture", true)));
        app.MapGet("/api/runs", (HostRunRegistry registry) => registry.List());
        app.MapGet("/api/recovery", (string hostId, string sessionId, ActionCustodyStore custody) =>
        {
            ProtocolValidation.Identifier(hostId);
            ProtocolValidation.Identifier(sessionId);
            return custody.ListUnresolved(hostId, sessionId);
        });
        app.MapPost("/api/recovery", (HostRecoveryRequest request, HostRunRegistry registry) =>
        {
            var changed = registry.Recover(request.HostId, request.SessionId, request.Result);
            return Results.Ok(new { request.Result.ActionId, resolved = request.Result.Disposition != "unresolved", duplicate = !changed });
        });
        app.MapGet("/api/runs/{runId}", (string runId, HostRunRegistry registry) => registry.Get(runId).Snapshot());
        app.MapPost("/api/runs/{runId}/cancel", (string runId, HostRunRegistry registry) =>
        {
            registry.Get(runId).Cancel();
            return Results.Ok(new { runId, status = "cancel_requested" });
        });
        app.MapGet("/api/runs/{runId}/events", StreamEventsAsync);
        app.Map("/api/host", HandleHostAsync);
        return app;
    }

    private static async Task StreamEventsAsync(HttpContext context, string runId, HostRunRegistry registry)
    {
        var run = registry.Get(runId);
        var afterText = context.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? context.Request.Query["after"].FirstOrDefault();
        var after = long.TryParse(afterText, out var parsed) && parsed >= 0 ? parsed : 0;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            await foreach (var item in run.Events.ReadAsync(after, context.RequestAborted).ConfigureAwait(false))
            {
                if (item.Sequence > after + 1)
                    await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new ServiceMessage("telemetry.gap",
                        new { after, next = item.Sequence, snapshotUrl = $"/api/runs/{runId}" }, runId), HostProtocol.Json) + "\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync($"id: {item.Sequence}\ndata: " + JsonSerializer.Serialize(item.Message, HostProtocol.Json) + "\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                after = item.Sequence;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task HandleHostAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var registry = context.RequestServices.GetRequiredService<HostRunRegistry>();
        await using var connection = new HostSocket(await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false));
        connection.StartProgress();
        HostRun? attached = null;
        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                HostMessage? message = null;
                try
                {
                    message = await connection.ReceiveAsync(context.RequestAborted).ConfigureAwait(false);
                    if (message is null) break;
                    if (message.ProtocolVersion != HostProtocol.Version) ProtocolValidation.Fail("protocol.version", "Unsupported protocol version.");
                    switch (message.Type)
                    {
                        case "start":
                            if (attached is not null && (!attached.Terminal || attached.HasUnresolvedActions))
                                ProtocolValidation.Fail("connection.busy", "Finish or reconcile this connection's run first.");
                            var request = message.Payload.Deserialize<HostRunRequest>(HostProtocol.Json)
                                ?? throw new HostProtocolException("start.invalid", "A run request is required.");
                            attached?.Detach(connection);
                            attached = registry.Create(request);
                            attached.Attach(connection);
                            try { await connection.SendAsync(new ServiceMessage("started", attached.Snapshot(false), attached.RunId, message.RequestId), context.RequestAborted).ConfigureAwait(false); }
                            finally { attached.Start(); }
                            break;
                        case "resume":
                            var resume = message.Payload.Deserialize<ResumeRequest>(HostProtocol.Json)
                                ?? throw new HostProtocolException("resume.invalid", "A resume request is required.");
                            var target = registry.Get(resume.RunId);
                            if (target.Request.SessionId != resume.SessionId || target.Request.SessionEpoch != resume.SessionEpoch)
                                ProtocolValidation.Fail("resume.binding", "Resume session identity does not match.");
                            if (attached is not null && attached != target && !attached.Terminal)
                                ProtocolValidation.Fail("connection.busy", "This connection already owns another run.");
                            target.Attach(connection);
                            attached?.Detach(connection);
                            attached = target;
                            attached.Attach(connection);
                            await connection.SendAsync(new ServiceMessage("resumed", attached.Snapshot(false), attached.RunId, message.RequestId), context.RequestAborted).ConfigureAwait(false);
                            await attached.ReplayPendingAsync(context.RequestAborted).ConfigureAwait(false);
                            break;
                        case "action.result":
                            EnsureAttached(attached, message.RunId);
                            var result = message.Payload.Deserialize<HostActionResult>(HostProtocol.Json)
                                ?? throw new HostProtocolException("result.invalid", "An action result is required.");
                            var changed = attached!.AcceptResult(result);
                            await connection.SendAsync(new ServiceMessage("action.accepted", new { result.ActionId, duplicate = !changed }, attached.RunId, message.RequestId), context.RequestAborted).ConfigureAwait(false);
                            break;
                        case "cancel":
                            EnsureAttached(attached, message.RunId);
                            attached!.Cancel();
                            await connection.SendAsync(new ServiceMessage("cancelled", new { stopRequested = true }, attached.RunId, message.RequestId), context.RequestAborted).ConfigureAwait(false);
                            break;
                        default: ProtocolValidation.Fail("message.unknown", "Unsupported host message type."); break;
                    }
                }
                catch (Exception exception) when (exception is HostProtocolException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException or IOException)
                {
                    var storageUnavailable = exception is InvalidDataException or IOException;
                    await connection.SendAsync(new ServiceMessage("error", new
                    {
                        code = exception is HostProtocolException protocol ? protocol.Code : storageUnavailable ? "storage.unavailable" : "message.invalid",
                        message = exception is HostProtocolException ? exception.Message : storageUnavailable ? "Persistent evidence could not be read or written. Reconcile the original action after restoring storage." : "Message failed contract validation."
                    }, attached?.RunId, message?.RequestId), context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { attached?.Detach(connection); }
    }

    private static void EnsureAttached(HostRun? run, string? runId)
    {
        if (run is null || run.RunId != runId) ProtocolValidation.Fail("run.binding", "Message is not bound to this connection's run.");
    }
}

using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;

namespace Agentica.Lab.Web.Chat;

public static class ExperimentEndpoints
{
    public static void MapExperimentChat(this WebApplication app)
    {
        var group = app.MapGroup("/api/chats");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var origin = context.HttpContext.Request.Headers.Origin.FirstOrDefault();
            if (origin is not null && origin != $"{context.HttpContext.Request.Scheme}://{context.HttpContext.Request.Host}")
                return Results.Json(new { code = "chat.origin", message = "Open experimental chat from this Lab's own origin." }, statusCode: 403);
            try { return await next(context).ConfigureAwait(false); }
            catch (ExperimentException exception) when (!context.HttpContext.Response.HasStarted)
            { return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode); }
            catch (Exception exception) when (!context.HttpContext.Response.HasStarted && exception is ArgumentException or JsonException)
            { return Results.BadRequest(new { code = "chat.invalid", message = "The experiment request is invalid." }); }
        });
        group.MapGet("", (ExperimentStore store) => store.List());
        group.MapPost("", (ExperimentCreateRequest request, ExperimentService service) => service.Create(request.Title));
        group.MapGet("/capabilities", () => ExperimentTools.Capabilities);
        group.MapGet("/models", async (IExperimentExecutor executor, OpenAiProviderConfiguration configuration, CancellationToken cancellationToken) =>
        {
            var current = configuration.GetSettings();
            IReadOnlyList<string> models = [current.Model];
            string? issue = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (current.Configured && executor is OpenAiExperimentExecutor openAi)
                try { models = await openAi.ListModelsAsync(deadline.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is ExperimentException or HttpRequestException or InvalidDataException or JsonException or OperationCanceledException)
                { issue = "The account model list is unavailable. Enter an exact model ID; the provider validates compatibility."; }
            return new
            {
                models = models.Append(current.Model).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                current.Configured,
                defaultModel = current.Model,
                reasoningEfforts = new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" },
                capabilitySource = "account model IDs when available; effort support is model-specific and provider-validated",
                issue
            };
        });
        group.MapGet("/{id}", (string id, ExperimentStore store) => store.Get(id));
        group.MapPatch("/{id}", (string id, ExperimentRenameRequest request, ExperimentStore store) => store.Rename(id, request.Title));
        group.MapPut("/{id}/settings", (string id, ExperimentSettingsUpdate request, ExperimentStore store) => store.UpdateSettings(id, request));
        group.MapPost("/{id}/turns", (string id, ExperimentSendRequest request, ExperimentService service) => service.Send(id, request));
        group.MapGet("/{id}/turns/{turnId}", (string id, string turnId, ExperimentStore store) => store.GetTurn(id, turnId));
        group.MapPost("/{id}/turns/{turnId}/cancel", (string id, string turnId, ExperimentService service) =>
        { service.Cancel(id, turnId); return Results.Ok(new { status = "cancel_requested" }); });
        group.MapGet("/{id}/turns/{turnId}/trace", (string id, string turnId, long? after, int? limit, ExperimentStore store) =>
        {
            var events = store.ReadEvents(id, turnId, after ?? 0, limit ?? 100);
            var next = events.Count > 0 ? events[^1].Sequence : after ?? 0;
            return new { events, next, hasMore = store.GetTurn(id, turnId).TraceCount > next };
        });
        group.MapGet("/{id}/turns/{turnId}/events/{sequence:long}/payload", (string id, string turnId, long sequence, ExperimentStore store) => store.ReadPayload(id, turnId, sequence));
        group.MapGet("/{id}/turns/{turnId}/citations", (string id, string turnId, long? after, int? limit, ExperimentStore store) =>
        {
            var events = store.ReadCitationEvents(id, turnId, after ?? 0, limit ?? 100);
            var next = events.Count > 0 ? events[^1].Sequence : after ?? 0;
            return new { events, next, hasMore = store.ReadCitationEvents(id, turnId, next, 1).Count > 0 };
        });
        group.MapGet("/{id}/turns/{turnId}/events", StreamAsync);
    }

    private static async Task StreamAsync(HttpContext context, string id, string turnId, ExperimentStore store)
    {
        _ = store.GetTurn(id, turnId);
        var cursor = context.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? context.Request.Query["after"].FirstOrDefault();
        var after = long.TryParse(cursor, out var parsed) ? Math.Max(0, parsed) : 0;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var events = store.ReadEvents(id, turnId, after, 100);
                foreach (var item in events)
                {
                    await context.Response.WriteAsync($"id: {item.Sequence}\ndata: {JsonSerializer.Serialize(item, HostProtocol.Json)}\n\n", context.RequestAborted).ConfigureAwait(false);
                    after = item.Sequence;
                }
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                var turn = store.GetTurn(id, turnId);
                if (turn.Status != "running" && after >= turn.TraceCount) return;
                await Task.Delay(100, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }
}

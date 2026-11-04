using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Chat.Recall;

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
        group.MapGet("/capabilities", (bool? threadRecallEnabled, bool? threadSemanticRecallEnabled, string? threadRecallMode, string? mode) =>
        {
            var settings = new ExperimentSettings
            {
                ThreadRecallEnabled = threadRecallEnabled ?? false,
                ThreadSemanticRecallEnabled = threadSemanticRecallEnabled ?? false,
                ThreadRecallMode = threadRecallMode ?? "auto",
                Mode = mode ?? "clean-room"
            };
            ExperimentStore.ValidateSettings(settings);
            return ExperimentTools.CapabilitiesFor(settings);
        });
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
        group.MapGet("/{id}", (string id, int? limit, int? beforeSequence, ExperimentStore store) =>
        {
            if (limit is null && beforeSequence is null) return store.Get(id);
            var head = store.GetHead(id);
            var page = store.GetTurnsPage(id, beforeSequence, Math.Clamp(limit ?? 20, 1, 100));
            return head.Conversation with
            {
                Turns = page.Turns,
                HistoryPage = new(page.NextBeforeSequence, page.TotalTurns, page.LatestSequence, head.CompletedTurns)
            };
        });
        group.MapPatch("/{id}", (string id, ExperimentRenameRequest request, ExperimentStore store) => store.Rename(id, request.Title));
        group.MapPut("/{id}/settings", (string id, ExperimentSettingsUpdate request, ExperimentStore store) => store.UpdateSettings(id, request));
        group.MapPost("/{id}/turns", (string id, ExperimentSendRequest request, ExperimentService service, CancellationToken cancellationToken) => service.SendAsync(id, request, cancellationToken));
        group.MapPost("/{id}/context-check", (string id, ExperimentSendRequest request, ExperimentService service, CancellationToken cancellationToken) => service.CheckContextAsync(id, request, cancellationToken));
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
        group.MapGet("/{id}/history/status", (string id, int? asOfSequence, ExperimentStore store, ExperimentHistoryMaintenance maintenance) =>
        {
            var source = HistorySource(store, id, asOfSequence);
            return new { head = source.GetHead(), coverage = source.GetCoverage(), maintenance = maintenance.Status(id) };
        });
        group.MapGet("/{id}/history/messages", (string id, int? asOfSequence, int? beforeOrder, int? limit, ExperimentStore store) =>
            HistorySource(store, id, asOfSequence).ListMessageHeadersPage(beforeOrder, Math.Clamp(limit ?? 40, 1, 100)));
        group.MapGet("/{id}/history/messages/{sourceId}", (string id, string sourceId, int? asOfSequence, int? startOffset,
            int? maxCharacters, int? neighborRadius, ExperimentStore store, CancellationToken cancellationToken) =>
        {
            var source = HistorySource(store, id, asOfSequence);
            var maximum = Math.Clamp(maxCharacters ?? 4000, 1, 32000);
            var exact = source.ReadExact(sourceId, startOffset ?? 0, maximum, cancellationToken);
            if (neighborRadius is not > 0 || exact.Hits.Count == 0) return exact;
            var remaining = maximum - exact.Hits.Sum(hit => hit.Excerpt.Length);
            if (remaining < 2) return exact with
            {
                Truncated = true,
                Reasons = exact.Reasons.Append("The exact requested source extent consumed the character ceiling; optional neighbors were omitted.").ToArray()
            };
            var neighbors = source.ReadNeighbors(sourceId, Math.Clamp(neighborRadius.Value, 0, 2), maximum, cancellationToken);
            var hits = exact.Hits.ToList();
            foreach (var hit in neighbors.Hits.Where(hit => hit.Source.SourceId != sourceId))
            {
                var length = Math.Min(remaining, hit.Excerpt.Length);
                if (length > 0 && char.IsHighSurrogate(hit.Excerpt[length - 1])) length--;
                if (length == 0) continue;
                hits.Add(hit with { EndOffset = hit.StartOffset + length, Excerpt = hit.Excerpt[..length] });
                remaining -= length;
            }
            var refs = hits.Select(hit => hit.Source.SourceId).ToArray();
            return exact with
            {
                QueryMethod = "canonical-exact+neighbors",
                Hits = hits.Select(hit => hit with { NeighborRefs = refs.Where(reference => reference != hit.Source.SourceId).ToArray() }).ToArray(),
                InspectedCount = exact.InspectedCount + neighbors.InspectedCount,
                Truncated = exact.Truncated || neighbors.Truncated || hits.Count < neighbors.Hits.Count
            };
        });
        group.MapPost("/{id}/history/search", (string id, ConversationEvidenceSearch request, int? asOfSequence,
            ExperimentStore store, CancellationToken cancellationToken) =>
            HistorySource(store, id, asOfSequence).Search(request, cancellationToken));
        group.MapPost("/{id}/history/rebuild", (string id, ExperimentHistoryMaintenance maintenance) => maintenance.Start(id));
        group.MapPost("/{id}/history/rebuild/cancel", (string id, ExperimentHistoryMaintenance maintenance) => maintenance.Cancel(id));
    }

    private static IConversationEvidenceSource HistorySource(ExperimentStore store, string conversationId, int? asOf)
    {
        var head = store.GetHead(conversationId);
        var cut = asOf ?? head.LatestSequence;
        if (cut < 0 || cut > head.LatestSequence)
            throw new ExperimentException("history.as_of", "Historical reads must use a sequence at or before this conversation's current head.");
        return store.BindEvidenceSource(conversationId, cut);
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

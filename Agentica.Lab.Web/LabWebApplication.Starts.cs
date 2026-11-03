using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web;

public static partial class LabWebApplication
{
    private static void MapStartRoutes(WebApplication app)
    {
        app.MapGet("/api/starts", (string hostId, string sessionId, string attemptId, HttpContext context, HostRunRegistry registry) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return registry.GetStart(new HostStartKey(hostId, sessionId, attemptId));
        });
        app.MapPost("/api/starts/stop", (HostStartReference reference, HttpContext context, HostRunRegistry registry) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return registry.StopStart(reference);
        });
    }

    // Recovery needs the original binding, not an unbounded snapshot of pending action payloads.
    private static object StartPayload(HostRun run, HostStartEntry entry) => new
    {
        run.RunId,
        run.Request.HostId,
        run.Request.SessionId,
        run.Request.SessionEpoch,
        run.Request.ObjectiveId,
        run.Request.ScopeId,
        run.Request.PerspectiveId,
        run.ManifestHash,
        startAdmission = entry
    };
}

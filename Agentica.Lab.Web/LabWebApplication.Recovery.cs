using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web;

public static partial class LabWebApplication
{
    private static HostRunRecoveryPage GetRunRecovery(string hostId, string sessionId, string sessionEpoch,
        string runId, HttpContext context, ActionCustodyStore custody, string? afterActionId = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        return custody.ListRetainedForRun(hostId, sessionId, sessionEpoch, runId, afterActionId);
    }
}

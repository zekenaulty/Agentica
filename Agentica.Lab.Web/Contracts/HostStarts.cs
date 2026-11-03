namespace Agentica.Lab.Web.Contracts;

public sealed record HostStartSubmission(string AttemptId, HostRunRequest Request);
public sealed record HostStartKey(string HostId, string SessionId, string AttemptId);
public sealed record HostStartReference(string HostId, string SessionId, string AttemptId, string RunId, string AcceptedRequestHash);

/// <summary>Registry-certified Stop settlement, not objective completion or continuation usage.</summary>
public sealed record HostStartTerminal(string Reason, HostRunUsage? Usage, bool UsageUnknown, DateTimeOffset SettledAt);

public sealed record HostStartEntry(
    int Schema,
    string AttemptId,
    string RunId,
    HostRunRequest OriginalRequest,
    HostRunRequest AcceptedRequest,
    string OriginalRequestHash,
    string AcceptedRequestHash,
    string ManifestHash,
    bool StopRequested,
    HostStartTerminal? Terminal,
    DateTimeOffset AdmittedAt);

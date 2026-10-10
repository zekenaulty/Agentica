using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Lab.Web.Providers;
using Agentica.Tools;

namespace Agentica.Lab.Web.Contracts;

/// <summary>Host-owned, deliberately bound capabilities. No game vocabulary enters this contract.</summary>
public sealed record HostRunRequest(
    int ProtocolVersion,
    string HostId,
    string SessionId,
    string SessionEpoch,
    string ScopeId,
    string PerspectiveId,
    string ObjectiveId,
    string Objective,
    HostObservation Observation,
    IReadOnlyList<HostCapability> Capabilities,
    ProviderSettings? Provider = null,
    HostRunLimits? Limits = null);

public sealed record HostCapability(
    string Id,
    string Name,
    string Description,
    ToolKind Kind,
    ToolEffect Effect,
    ToolInputSchema InputSchema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DurableHandoff = false);

public sealed record HostObservation(
    string ObservationId,
    long Revision,
    DateTimeOffset ObservedAt,
    JsonElement Data,
    IReadOnlyList<KnowledgeFact>? Facts = null);

public sealed record KnowledgeFact(
    string Key,
    string Summary,
    JsonElement Value,
    string State = "observed",
    IReadOnlyList<string>? EvidenceObservationIds = null,
    string? Supersedes = null);

public sealed record HostRunLimits(
    int MaxSteps = 32,
    int MaxRefinements = 32,
    int MaxPlanContinuations = 16,
    int TimeoutSeconds = 300,
    int ActionTimeoutSeconds = 45,
    int MaxRecentObservations = 6,
    int MaxRecentReceipts = 6);

public sealed record HostActionRequest(
    string ActionId,
    string RunId,
    string RunnerRunId,
    string StepId,
    string SessionId,
    string SessionEpoch,
    string CapabilityId,
    string ManifestHash,
    JsonElement Arguments,
    long ExpectedRevision,
    DateTimeOffset DeadlineAt);

public sealed record HostActionResult(
    string ActionId,
    string SessionId,
    string SessionEpoch,
    string Disposition,
    long BeforeRevision,
    long AfterRevision,
    string EvidenceId,
    string Summary,
    HostObservation? Observation = null,
    HostCompletion? Completion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HostOperationAdmission? Operation = null);

public sealed record HostCompletion(string ObjectiveId, string EvidenceId, string Summary);

public sealed record ResumeRequest(string RunId, string SessionId, string SessionEpoch);
public sealed record HostRecoveryRequest(string HostId, string SessionId, HostActionResult Result);

public sealed record HostMessage(
    string Type,
    JsonElement Payload,
    string? RequestId = null,
    string? RunId = null,
    int ProtocolVersion = 1);

public sealed record ServiceMessage(
    string Type,
    object Payload,
    string? RunId = null,
    string? RequestId = null,
    int ProtocolVersion = 1);

public static class HostProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 262_144;
    public static JsonSerializerOptions Json { get; } = CreateJson();

    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

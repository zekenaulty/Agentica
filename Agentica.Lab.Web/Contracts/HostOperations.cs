using System.Text.Json;

namespace Agentica.Lab.Web.Contracts;

/// <summary>Admission to host-owned durable work, never proof of its completion.</summary>
public sealed record HostOperationAdmission(string OperationId, string Summary, JsonElement? Usage = null);

/// <summary>A separately receipted host event. Progress does not activate cognition.</summary>
public sealed record HostOperationEvent(
    string HostId, string SessionId, string SessionEpoch, string ActionId, string OperationId,
    long Sequence, string EventId, string Kind, HostObservation Observation, string Summary,
    JsonElement? Usage = null, HostCompletion? Completion = null, string? NextSessionEpoch = null);

/// <summary>Cumulative Agentica work across the execution windows of one objective.</summary>
public sealed record HostRunUsage(int Steps = 0, int Refinements = 0, int Continuations = 0, int ProviderCalls = 0);

using System.Text.Json.Serialization;

namespace Agentica.Tools;

public sealed record ToolDescriptor(
    string ToolId,
    string Name,
    ToolKind Kind,
    ToolEffect Effect,
    bool RequiresApproval = false,
    ToolInputSchema? InputSchema = null,
    string? Description = null,
    ToolContextHint? ContextHint = null,
    ToolCooldownPolicy? Cooldown = null,
    ToolRetrySafety RetrySafety = ToolRetrySafety.Unknown);

public sealed record ToolContextHint(
    string Produces,
    IReadOnlyList<string> Complements,
    IReadOnlyList<string> CanBatchWith,
    IReadOnlyList<string> ShouldPrecede)
{
    public string? UseWhen { get; init; }

    public string? NotEnoughWhen { get; init; }
}

public sealed record ToolCooldownPolicy(
    int? PlanStepCount = null,
    TimeSpan? Duration = null,
    IReadOnlyList<string>? ScopeInputKeys = null,
    string? Reason = null,
    bool ResetOnMutation = false)
{
    [JsonIgnore]
    public bool IsActive =>
        PlanStepCount is > 0 ||
        Duration is { Ticks: > 0 };
}

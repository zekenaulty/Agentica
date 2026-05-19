namespace Agentica.Events;

public sealed record UserFacingReason(
    string Summary,
    string? Detail = null,
    string? Status = null)
{
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string ProjectionSource { get; init; } = "agentica.default";
}

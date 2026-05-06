namespace Agentica.Planning;

public sealed record PlanningContextOptions(
    int? MaxRecentObservations = null,
    int? MaxRecentReceipts = null)
{
    public static PlanningContextOptions FullHistory { get; } = new();
}

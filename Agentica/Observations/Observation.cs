namespace Agentica.Observations;

public sealed record Observation(
    string ObservationId,
    string StepId,
    ObservationKind Kind,
    string Summary,
    IReadOnlyDictionary<string, object?> Data,
    IReadOnlyList<EvidenceRef> Evidence);

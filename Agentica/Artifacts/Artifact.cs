using Agentica.Observations;

namespace Agentica.Artifacts;

public sealed record Artifact(
    string ArtifactId,
    string Kind,
    IReadOnlyDictionary<string, object?> Payload,
    IReadOnlyList<EvidenceRef> Evidence);

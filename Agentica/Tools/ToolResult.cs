using Agentica.Artifacts;
using Agentica.Observations;

namespace Agentica.Tools;

public sealed record ToolResult(
    Receipt Receipt,
    Observation? Observation = null,
    Artifact? Artifact = null);

using Agentica.Artifacts;

namespace Agentica.Execution;

public sealed record CompletionEvidenceRequirement(string Kind, string Value)
{
    public static CompletionEvidenceRequirement ArtifactKind(string artifactKind) =>
        new("artifact.kind", artifactKind);

    public static CompletionEvidenceRequirement SuccessfulReceiptTool(string toolId) =>
        new("receipt.tool", toolId);

    public bool IsSatisfiedBy(IReadOnlyList<Artifact> artifacts, IReadOnlyList<Receipt> receipts) =>
        Kind switch
        {
            "artifact.kind" => artifacts.Any(artifact => string.Equals(artifact.Kind, Value, StringComparison.Ordinal)),
            "receipt.tool" => receipts.Any(receipt =>
                receipt.Status == ReceiptStatus.Succeeded &&
                string.Equals(receipt.ToolId, Value, StringComparison.Ordinal)),
            _ => false
        };
}

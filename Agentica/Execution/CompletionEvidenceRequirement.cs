using Agentica.Artifacts;
using Agentica.Observations;

namespace Agentica.Execution;

public sealed record CompletionEvidenceRequirement(string Kind, string Value)
{
    public static CompletionEvidenceRequirement ArtifactKind(string artifactKind) =>
        new("artifact.kind", artifactKind);

    public static CompletionEvidenceRequirement SuccessfulReceiptTool(string toolId) =>
        new("receipt.tool", toolId);

    public bool IsSatisfiedBy(IReadOnlyList<Artifact> artifacts, IReadOnlyList<Receipt> receipts) =>
        Resolve(artifacts, receipts) is not null;

    public EvidenceRef? Resolve(IReadOnlyList<Artifact> artifacts, IReadOnlyList<Receipt> receipts) =>
        Kind switch
        {
            "artifact.kind" => ResolveArtifact(artifacts, receipts),
            "receipt.tool" => receipts.FirstOrDefault(receipt =>
                receipt.Status == ReceiptStatus.Succeeded &&
                string.Equals(receipt.ToolId, Value, StringComparison.Ordinal)) is { } receipt
                ? new EvidenceRef("receipt", receipt.ReceiptId)
                : null,
            _ => null
        };

    private EvidenceRef? ResolveArtifact(
        IReadOnlyList<Artifact> artifacts,
        IReadOnlyList<Receipt> receipts)
    {
        var successfulReceiptIds = receipts
            .Where(receipt => receipt.Status == ReceiptStatus.Succeeded)
            .Select(receipt => receipt.ReceiptId)
            .ToHashSet(StringComparer.Ordinal);
        var artifact = artifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.Kind, Value, StringComparison.Ordinal) &&
            candidate.Evidence.Any(reference =>
                string.Equals(reference.Kind, "receipt", StringComparison.Ordinal) &&
                successfulReceiptIds.Contains(reference.RefId)));
        return artifact is null
            ? null
            : new EvidenceRef("artifact", artifact.ArtifactId);
    }
}

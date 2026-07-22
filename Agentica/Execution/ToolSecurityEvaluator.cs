using Agentica.Tools;

namespace Agentica.Execution;

internal static class ToolSecurityEvaluator
{
    public static IReadOnlyList<ToolDataBoundary> PlannerBoundaryViolations(
        ToolSecurityPolicy policy,
        IEnumerable<ToolDataBoundary> exposedBoundaries)
    {
        if (policy.ExternalPlannerAllowedBoundaries is null)
        {
            return [];
        }

        return exposedBoundaries
            .Where(boundary => !policy.ExternalPlannerAllowedBoundaries.Contains(boundary))
            .Distinct()
            .OrderBy(boundary => boundary.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static ToolGrantEvaluation EvaluateDispatch(
        ToolSecurityPolicy policy,
        string manifestHash,
        CompiledToolRegistration registration,
        IEnumerable<ToolDataBoundary> exposedBoundaries,
        DateTimeOffset now)
    {
        var security = registration.Security;
        var requiresGrant = security.Effect == ToolEffect.ExternalSideEffect ||
            security.ApprovalRequirement != ToolApprovalRequirement.None;
        if (!requiresGrant)
        {
            return ToolGrantEvaluation.Allow;
        }

        var requiredOutboundBoundaries = exposedBoundaries
            .Concat(security.Reads)
            .Distinct()
            .ToArray();
        var candidates = policy.ExecutionGrants.Where(grant =>
            string.Equals(grant.ManifestHash, manifestHash, StringComparison.Ordinal) &&
            string.Equals(grant.ToolId, registration.PlannerProjection.ToolId, StringComparison.Ordinal));

        foreach (var grant in candidates)
        {
            if (grant.ExpiresAt <= now || string.IsNullOrWhiteSpace(grant.Issuer))
            {
                continue;
            }

            if (requiredOutboundBoundaries.Any(boundary =>
                    !grant.AllowedOutboundBoundaries.Contains(boundary)))
            {
                continue;
            }

            if (!grant.AllowedExternalOutputs.Contains(security.ExternalOutput))
            {
                continue;
            }

            return ToolGrantEvaluation.Allow;
        }

        return new ToolGrantEvaluation(
            false,
            "tool.security.grant_required",
            $"Tool '{registration.PlannerProjection.ToolId}' requires an unexpired execution grant bound to " +
            "the exact manifest, tool, outbound boundaries, and external-output classification.");
    }
}

internal sealed record ToolGrantEvaluation(bool Allowed, string? Code, string? Message)
{
    public static ToolGrantEvaluation Allow { get; } = new(true, null, null);
}

using Agentica.Tools;
using Agentica.Planning;

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
        PlanStep step,
        string? authorizationScopeId,
        int attemptNumber,
        IEnumerable<ToolDataBoundary> exposedBoundaries,
        DateTimeOffset now,
        bool enforceAuthorizationScope = true,
        IReadOnlySet<string>? reservedGrantIds = null,
        string? operationalInputDigest = null)
    {
        var security = registration.Security;
        var requiresGrant = security.Effect == ToolEffect.ExternalSideEffect ||
            security.ApprovalRequirement != ToolApprovalRequirement.None;
        if (!requiresGrant)
        {
            return ToolGrantEvaluation.Allow;
        }

        string invocationInputDigest;
        if (operationalInputDigest is not null)
        {
            if (!ToolInvocationAuthorization.IsVersionedDigest(operationalInputDigest))
            {
                return new ToolGrantEvaluation(
                    false,
                    "tool.security.input_digest_invalid",
                    $"Tool '{registration.PlannerProjection.ToolId}' received an invalid operational input digest.",
                    null,
                    null);
            }

            invocationInputDigest = operationalInputDigest;
        }
        else
        {
            try
            {
                invocationInputDigest = ToolInvocationAuthorization.ComputeInputDigest(step.Input);
            }
            catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
            {
                return new ToolGrantEvaluation(
                    false,
                    "tool.security.input_digest_invalid",
                    $"Tool '{registration.PlannerProjection.ToolId}' input could not be bound to an execution grant: " +
                    $"{exception.GetType().Name}.",
                    null,
                    null);
            }
        }

        var requiredOutboundBoundaries = exposedBoundaries
            .Concat(security.Reads)
            .Distinct()
            .ToArray();
        var candidates = policy.ExecutionGrants.Where(grant =>
            string.Equals(grant.ManifestHash, manifestHash, StringComparison.Ordinal) &&
            string.Equals(grant.ToolId, registration.PlannerProjection.ToolId, StringComparison.Ordinal) &&
            string.Equals(grant.StepId, step.StepId, StringComparison.Ordinal) &&
            string.Equals(grant.InvocationInputDigest, invocationInputDigest, StringComparison.Ordinal) &&
            grant.AttemptNumber == attemptNumber &&
            (!enforceAuthorizationScope ||
             string.Equals(grant.AuthorizationScopeId, authorizationScopeId, StringComparison.Ordinal)))
            .ToArray();

        foreach (var grant in candidates)
        {
            if (grant.IsConsumed ||
                reservedGrantIds?.Contains(grant.GrantId) == true ||
                grant.ExpiresAt <= now ||
                string.IsNullOrWhiteSpace(grant.Issuer))
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

            return new ToolGrantEvaluation(true, null, null, grant, invocationInputDigest);
        }

        var unavailableCode = candidates.Any(grant => grant.IsConsumed)
            ? "tool.security.grant_consumed"
            : candidates.Any(grant => reservedGrantIds?.Contains(grant.GrantId) == true)
                ? "tool.security.grant_reuse"
                : "tool.security.grant_required";

        return new ToolGrantEvaluation(
            false,
            unavailableCode,
            $"Tool '{registration.PlannerProjection.ToolId}' requires an unexpired execution grant bound to " +
            "the authorization scope, attempt, step, invocation input, exact manifest, tool, outbound " +
            "boundaries, and external-output classification.",
            null,
            invocationInputDigest);
    }

    /// <summary>
    /// Checks that a dependency-blocked plan step has a structurally sufficient ticket without
    /// claiming that its not-yet-final input is authorized. Exact input binding is mandatory at
    /// dispatch after every dependency has completed and source identities have been restored.
    /// </summary>
    public static ToolGrantEvaluation EvaluateDeferredPlanAuthorization(
        ToolSecurityPolicy policy,
        string manifestHash,
        CompiledToolRegistration registration,
        PlanStep step,
        string? authorizationScopeId,
        int attemptNumber,
        IEnumerable<ToolDataBoundary> exposedBoundaries,
        DateTimeOffset now,
        IReadOnlySet<string>? reservedGrantIds = null)
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
            string.Equals(grant.ToolId, registration.PlannerProjection.ToolId, StringComparison.Ordinal) &&
            string.Equals(grant.StepId, step.StepId, StringComparison.Ordinal) &&
            grant.AttemptNumber == attemptNumber &&
            string.Equals(grant.AuthorizationScopeId, authorizationScopeId, StringComparison.Ordinal))
            .ToArray();

        foreach (var grant in candidates)
        {
            if (grant.IsConsumed ||
                reservedGrantIds?.Contains(grant.GrantId) == true ||
                grant.ExpiresAt <= now ||
                string.IsNullOrWhiteSpace(grant.Issuer) ||
                requiredOutboundBoundaries.Any(boundary =>
                    !grant.AllowedOutboundBoundaries.Contains(boundary)) ||
                !grant.AllowedExternalOutputs.Contains(security.ExternalOutput))
            {
                continue;
            }

            return new ToolGrantEvaluation(true, null, null, grant, null);
        }

        var unavailableCode = candidates.Any(grant => grant.IsConsumed)
            ? "tool.security.grant_consumed"
            : candidates.Any(grant => reservedGrantIds?.Contains(grant.GrantId) == true)
                ? "tool.security.grant_reuse"
                : "tool.security.grant_required";

        return new ToolGrantEvaluation(
            false,
            unavailableCode,
            $"Tool '{registration.PlannerProjection.ToolId}' requires an unexpired execution grant with a " +
            "matching scope, attempt, step, manifest, tool, outbound boundaries, and external-output " +
            "classification. Its final invocation input will be checked at dispatch.",
            null,
            null);
    }
}

internal sealed record ToolGrantEvaluation(
    bool Allowed,
    string? Code,
    string? Message,
    ToolExecutionGrant? Grant,
    string? InvocationInputDigest)
{
    public static ToolGrantEvaluation Allow { get; } = new(true, null, null, null, null);
}

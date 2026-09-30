using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class PlannerSurfacePolicyTests
{
    [Fact]
    public async Task Host_bound_external_tool_without_extra_grant_is_visible_and_dispatchable()
    {
        var plan = new WorkflowPlan(
            "plan.bound.external", 1,
            [new PlanStep("step.bound", "external.send", ToolKind.Action,
                ToolEffect.ExternalSideEffect, EmptyInput())],
            "Use the installed external capability.");
        var planner = new CapturingExternalPlanner(plan);
        var catalog = ToolCatalog.Create(Registration("external.send",
            ToolEffect.ExternalSideEffect,
            exposes: [ToolDataBoundary.ExternalUntrusted],
            requiresApproval: false));
        var runner = new AgenticaRunner(planner, catalog,
            new InMemoryEventSink(), new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown,
                SecurityPolicy: new ToolSecurityPolicy(
                    InitialBoundaries: [ToolDataBoundary.UserContent],
                    ExternalPlannerAllowedBoundaries:
                    [ToolDataBoundary.UserContent, ToolDataBoundary.Public,
                     ToolDataBoundary.ExternalUntrusted])),
            PlanExhaustionCompletionEvaluator.Instance);

        var outcome = await runner.RunAsync(new RunRequest(
            "Use the installed external capability."));

        Assert.Equal(RunOutcomeStatus.Succeeded, outcome.Outcome.Status);
        Assert.Equal("external.send", Assert.Single(
            Assert.Single(planner.Requests).ToolDescriptors).ToolId);
        Assert.Empty(outcome.Details.GrantConsumptions);
        Assert.Contains(outcome.Receipts.Items,
            receipt => receipt.ToolId == "external.send" &&
                       receipt.Status == ReceiptStatus.Succeeded);
    }

    [Fact]
    public async Task External_planner_only_sees_tools_dispatchable_under_effect_and_boundary_policy()
    {
        var planner = new CapturingExternalPlanner(new WorkflowPlan(
            "plan.visible",
            1,
            [new PlanStep("step.visible", "local.read", ToolKind.Query, ToolEffect.ReadOnly, EmptyInput())],
            "Use the only policy-compatible tool."));
        var catalog = ToolCatalog.Create(
            Registration("local.read", ToolEffect.ReadOnly, exposes: [ToolDataBoundary.Public]),
            Registration("workspace.read", ToolEffect.ReadOnly, exposes: [ToolDataBoundary.WorkspaceContent]),
            Registration("external.send", ToolEffect.ExternalSideEffect, exposes: [ToolDataBoundary.ExternalUntrusted]));
        var policy = new ExecutionPolicy(
            PlanningMode: PlanningMode.PlanOnly,
            EffectPolicy: ToolEffectPolicy.AllowKnown,
            SecurityPolicy: new ToolSecurityPolicy(
                InitialBoundaries: [ToolDataBoundary.UserContent],
                ExternalPlannerAllowedBoundaries:
                [
                    ToolDataBoundary.UserContent,
                    ToolDataBoundary.Public
                ]));
        var runner = new AgenticaRunner(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            policy,
            PlanExhaustionCompletionEvaluator.Instance);

        var envelope = await runner.RunAsync(new RunRequest("Exercise the filtered planner surface."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var request = Assert.Single(planner.Requests);
        var descriptor = Assert.Single(request.ToolDescriptors);
        Assert.Equal("local.read", descriptor.ToolId);
        var surface = Assert.Single(envelope.Details.ToolSurfaces);
        Assert.Equal(["local.read"], surface.ToolDescriptors.Select(item => item.ToolId));
        Assert.Equal(1, surface.PolicySummary["visibleToolCount"]);
        Assert.Equal(2, surface.PolicySummary["filteredToolCount"]);
    }

    private static ToolRegistration Registration(
        string toolId,
        ToolEffect effect,
        IReadOnlyList<ToolDataBoundary> exposes,
        bool? requiresApproval = null) =>
        new(
            new ToolDescriptor(
                toolId,
                toolId,
                effect == ToolEffect.ReadOnly ? ToolKind.Query : ToolKind.Action,
                effect,
                RequiresApproval: requiresApproval ?? effect == ToolEffect.ExternalSideEffect,
                RetrySafety: effect == ToolEffect.ReadOnly
                    ? ToolRetrySafety.Idempotent
                    : ToolRetrySafety.MutationUnsafe),
            new SuccessTool(),
            new ToolSecurityDeclaration(
                effect,
                [ToolDataBoundary.Public],
                exposes,
                effect == ToolEffect.ExternalSideEffect
                    ? ToolExternalOutputClassification.UntrustedStructuredData
                    : ToolExternalOutputClassification.None,
                (requiresApproval ?? effect == ToolEffect.ExternalSideEffect)
                    ? ToolApprovalRequirement.ExplicitGrant
                    : ToolApprovalRequirement.None,
                effect == ToolEffect.ReadOnly
                    ? ToolRetrySafety.Idempotent
                    : ToolRetrySafety.MutationUnsafe,
                new ToolProvenance(ToolProvenanceKind.BuiltIn, "Agentica.Tests", "1")));

    private static IReadOnlyDictionary<string, object?> EmptyInput() =>
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private sealed class SuccessTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Succeeded,
                "ok",
                DateTimeOffset.UtcNow,
                EmptyInput());
            var observation = new Observation(
                AgenticaIds.New("observation"),
                invocation.StepId,
                ObservationKind.StateQuery,
                "ok",
                EmptyInput(),
                [new EvidenceRef("receipt", receipt.ReceiptId)]);
            return Task.FromResult(new ToolResult(receipt, observation));
        }
    }

    private sealed class CapturingExternalPlanner(WorkflowPlan plan) : IWorkflowPlanner, IExternalWorkflowPlanner
    {
        public List<PlanningRequest> Requests { get; } = [];

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(plan);
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);
    }
}

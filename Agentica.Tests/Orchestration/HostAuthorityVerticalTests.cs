using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Orchestration.Tests;

public sealed class HostAuthorityVerticalTests
{
    [Fact]
    public async Task Model_selected_host_bound_project_create_uses_derived_authority_and_receipt()
    {
        const string parentObjective = "Give Fragmented Context its own fresh workspace.";
        const string childObjective = "Create a lab project named Fragmented Context.";
        var registry = new ProjectRegistry();
        var authority = new LabProjectAuthority(registry);
        var tool = new ProjectCreateTool(registry, authority);
        var model = new ProjectPlanClient();
        var orchestrator = CreateOrchestrator(registry, authority, tool, model,
            childObjective);

        var outcome = await orchestrator.RunAsync(new LargeTaskRequest(
            parentObjective,
            RequestOrigin.User,
            new Dictionary<string, object?>(),
            AuthorizationScopeId: "lab.parent"));

        Assert.True(outcome.Status == OrchestrationStatus.Succeeded,
            $"{outcome.Status}/{outcome.StopReason}: {string.Join(" | ", outcome.Diagnostics)}; " +
            $"children={string.Join(",", outcome.RunOutcomes.Select(item =>
                $"{item.Outcome.Status}/{item.Outcome.StopReason}"))}");
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(registry.Contains("lab", "Fragmented Context"));
        var child = Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.Succeeded, child.Outcome.Status);
        Assert.StartsWith("lab.child.", child.Details.Request.AuthorizationScopeId);
        Assert.Equal("lab.derivation.receipt",
            child.Details.Request.Context?["orchestration.authorityDerivationReceiptId"]);
        Assert.Contains(child.Receipts.Items, receipt =>
            receipt.ToolId == "project.create" &&
            receipt.Status == ReceiptStatus.Succeeded &&
            Equals(receipt.Data["authorityScopeId"],
                child.Details.Request.AuthorizationScopeId));
        Assert.Empty(child.Details.GrantConsumptions);
        var llmRequest = Assert.Single(model.Requests);
        Assert.Contains("project.create", llmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(childObjective, llmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("lab.child.", llmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Derived_lab_scope_refuses_model_selected_project_in_other_namespace()
    {
        var registry = new ProjectRegistry();
        var authority = new LabProjectAuthority(registry);
        var tool = new ProjectCreateTool(registry, authority);
        var model = new ProjectPlanClient("production");
        var orchestrator = CreateOrchestrator(registry, authority, tool, model,
            "Create a lab project named Fragmented Context.");

        var outcome = await orchestrator.RunAsync(new LargeTaskRequest(
            "Give Fragmented Context its own fresh workspace.",
            RequestOrigin.User,
            new Dictionary<string, object?>(),
            AuthorizationScopeId: "lab.parent"));

        Assert.NotEqual(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.False(registry.Contains("production", "Fragmented Context"));
        Assert.False(registry.Contains("lab", "Fragmented Context"));
        Assert.Contains(Assert.Single(outcome.RunOutcomes).Receipts.Items,
            receipt => receipt.ToolId == "project.create" &&
                       receipt.Status == ReceiptStatus.Refused);
    }

    [Fact]
    public async Task Parent_without_project_delegation_cannot_start_model_or_tool()
    {
        var registry = new ProjectRegistry();
        var authority = new LabProjectAuthority(registry);
        var tool = new ProjectCreateTool(registry, authority);
        var model = new ProjectPlanClient();
        var orchestrator = CreateOrchestrator(registry, authority, tool, model,
            "Create a lab project named Fragmented Context.");

        var outcome = await orchestrator.RunAsync(new LargeTaskRequest(
            "Give Fragmented Context its own fresh workspace.",
            RequestOrigin.User,
            new Dictionary<string, object?>(),
            AuthorizationScopeId: "lab.viewer"));

        Assert.Equal(OrchestrationStopReason.AuthorityOutOfScope,
            outcome.StopReason);
        Assert.Empty(outcome.RunOutcomes);
        Assert.Empty(model.Requests);
        Assert.Equal(0, tool.ExecutionCount);
    }

    private static TaskOrchestrator CreateOrchestrator(
        ProjectRegistry registry,
        LabProjectAuthority authority,
        ProjectCreateTool tool,
        ProjectPlanClient model,
        string childObjective)
    {
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("project.create", "Create project", ToolKind.Action,
                ToolEffect.WritesLocalState,
                InputSchema: ToolInputSchema.Create(
                    new ToolInputField("namespace", Required: true),
                    new ToolInputField("title", Required: true)),
                Description: "Create one project inside the authorized lab namespace.",
                RetrySafety: ToolRetrySafety.MutationUnsafe),
            tool,
            new ToolSecurityDeclaration(
                ToolEffect.WritesLocalState,
                [ToolDataBoundary.UserContent],
                [ToolDataBoundary.Public],
                ToolExternalOutputClassification.None,
                ToolApprovalRequirement.None,
                ToolRetrySafety.MutationUnsafe,
                new ToolProvenance(ToolProvenanceKind.HostAuthored,
                    "lab-project-host", "1"))));
        var executor = new InProcessAgenticaRunExecutor(
            _ => new LlmWorkflowPlanner(model,
                new LlmPlannerOptions(InvalidJsonRepairAttempts: 0)),
            _ => catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            _ => new ProjectCompletionEvaluator(registry),
            _ => new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown,
                SecurityPolicy: new ToolSecurityPolicy(
                    InitialBoundaries: [ToolDataBoundary.UserContent],
                    ExternalPlannerAllowedBoundaries:
                    [ToolDataBoundary.UserContent, ToolDataBoundary.Public])));
        return new TaskOrchestrator(
            new SingleProjectTaskPlanner(childObjective),
            executor,
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>
            {
                ["projectCreated"] = registry.Contains("lab", "Fragmented Context")
            },
            new OrchestrationPolicy(MaxRuns: 1, MaxRefinements: 0),
            authority);
    }

    private sealed class ProjectRegistry
    {
        private readonly HashSet<(string Namespace, string Title)> _projects = [];

        public bool Contains(string projectNamespace, string title) =>
            _projects.Contains((projectNamespace, title));

        public bool TryCreate(string projectNamespace, string title) =>
            _projects.Count < 1 && _projects.Add((projectNamespace, title));
    }

    private sealed class LabProjectAuthority(ProjectRegistry registry) :
        IChildAuthorityDeriver
    {
        private readonly HashSet<string> _children = new(StringComparer.Ordinal);

        public Task<ChildAuthorityResolution> DeriveAsync(
            ChildAuthorityDerivationRequest request,
            CancellationToken cancellationToken = default)
        {
            var projection = request.ChildContextProjection;
            if (request.ParentAuthorizationScopeId != "lab.parent" ||
                !Equals(projection.GetValueOrDefault("operation"), "project.create") ||
                !Equals(projection.GetValueOrDefault("projectNamespace"), "lab") ||
                !Equals(projection.GetValueOrDefault("projectTitle"),
                    "Fragmented Context") ||
                registry.Contains("lab", "Fragmented Context"))
            {
                return Task.FromResult(new ChildAuthorityResolution(
                    ChildAuthorityDisposition.OutOfScope,
                    Reason: "Project creation is outside this parent scope."));
            }

            var childScope = "lab.child." + request.ChildDispatchId;
            _children.Add(childScope);
            return Task.FromResult(new ChildAuthorityResolution(
                ChildAuthorityDisposition.Derived,
                childScope,
                "lab.derivation.receipt"));
        }

        public bool CanCreate(string? childScope, string projectNamespace, string title) =>
            childScope is not null &&
            _children.Contains(childScope) &&
            projectNamespace == "lab" &&
            title == "Fragmented Context" &&
            !registry.Contains(projectNamespace, title);
    }

    private sealed class ProjectCreateTool(ProjectRegistry registry,
        LabProjectAuthority authority) : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var projectNamespace = invocation.Input["namespace"]?.ToString() ?? "";
            var title = invocation.Input["title"]?.ToString() ?? "";
            var allowed = authority.CanCreate(invocation.AuthorizationScopeId,
                projectNamespace, title);
            var created = allowed && registry.TryCreate(projectNamespace, title);
            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                created ? ReceiptStatus.Succeeded : ReceiptStatus.Refused,
                created ? "Project created." : "Project creation refused by host scope or quota.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>
                {
                    ["namespace"] = projectNamespace,
                    ["title"] = title,
                    ["authorityScopeId"] = invocation.AuthorizationScopeId
                });
            var observation = new Observation(
                AgenticaIds.New("observation"),
                invocation.StepId,
                ObservationKind.ToolResult,
                receipt.Message,
                new Dictionary<string, object?>
                {
                    ["projectCreated"] = created
                },
                [new EvidenceRef("receipt", receipt.ReceiptId)]);
            return Task.FromResult(new ToolResult(receipt, observation));
        }
    }

    private sealed class ProjectCompletionEvaluator(ProjectRegistry registry) :
        ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context)
        {
            var receipt = context.Receipts.LastOrDefault(item =>
                item.ToolId == "project.create" &&
                item.Status == ReceiptStatus.Succeeded);
            return receipt is not null &&
                   registry.Contains("lab", "Fragmented Context")
                ? CompletionEvaluation.Complete(
                    new EvidenceRef("receipt", receipt.ReceiptId))
                : CompletionEvaluation.Continue("Project creation is not proven.");
        }
    }

    private sealed class SingleProjectTaskPlanner(string childObjective) : ITaskPlanner
    {
        public Task<TaskGraphPlan> CreatePlanAsync(TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskGraphPlan(
                "plan.lab.project",
                request.Request.Objective,
                [new TaskNode("project", childObjective, [], false, 1, 1,
                    new Dictionary<string, object?>
                    {
                        ["operation"] = "project.create",
                        ["projectNamespace"] = "lab",
                        ["projectTitle"] = "Fragmented Context"
                    },
                    [new TaskAcceptanceRequirement(
                        TaskAcceptanceRequirementKind.Receipt,
                        ToolId: "project.create")])],
                [new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "projectCreated", HostStateValue: true)],
                DateTimeOffset.UtcNow));

        public Task<TaskGraphRefinement> RefinePlanAsync(TaskRefinementRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement is not expected.");
    }

    private sealed class ProjectPlanClient(string projectNamespace = "lab") : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmResponse> GenerateAsync(LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var plan = $$"""
                {
                  "planId": "plan.model.project",
                  "description": "Create the requested workspace.",
                  "steps": [
                    {
                      "stepId": "step.create.project",
                      "toolId": "project.create",
                      "kind": "Action",
                      "effect": "WritesLocalState",
                      "input": {
                        "namespace": "{{projectNamespace}}",
                        "title": "Fragmented Context"
                      },
                      "reason": "The host offers project creation for this objective."
                    }
                  ],
                  "completionCondition": "A receipt proves project creation."
                }
                """;
            return Task.FromResult(new LlmResponse("fixture", "fixture-model",
                plan, plan, FinishReason: LlmFinishReason.Stop));
        }
    }
}

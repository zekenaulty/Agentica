using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class ToolSecurityManifestTests
{
    [Fact]
    public void CanonicalManifestHashIsVersionedAndIndependentOfRegistrationOrder()
    {
        var first = LocalRegistration("tool.alpha");
        var second = LocalRegistration("tool.beta");

        var forward = ToolCatalog.Create(first, second);
        var reverse = ToolCatalog.Create(second, first);

        Assert.Equal(forward.ManifestHash, reverse.ManifestHash);
        Assert.Matches("^sha256-v1:[0-9a-f]{64}$", forward.ManifestHash);
        Assert.Equal(
            ["tool.alpha", "tool.beta"],
            forward.Descriptors.Select(descriptor => descriptor.ToolId).ToArray());
    }

    [Fact]
    public async Task ToolSurfaceKeepsRandomInstanceIdentityAndPinsTheCanonicalManifestHash()
    {
        var catalog = ToolCatalog.Create(LocalRegistration("local.read"));
        var planner = new StaticPlanner(OneStepPlan("local.read", ToolKind.Query, ToolEffect.ReadOnly));
        var runner = CreateRunner(
            planner,
            catalog,
            new ExecutionPolicy(PlanningMode: PlanningMode.PlanOnly),
            new InMemoryEventSink());

        var first = await runner.RunAsync(new RunRequest("first"));
        var second = await runner.RunAsync(new RunRequest("second"));
        var firstSurface = Assert.Single(first.Details.ToolSurfaces);
        var secondSurface = Assert.Single(second.Details.ToolSurfaces);

        Assert.NotEqual(firstSurface.SurfaceId, secondSurface.SurfaceId);
        Assert.Equal(catalog.ManifestHash, firstSurface.ManifestHash);
        Assert.Equal(firstSurface.ManifestHash, secondSurface.ManifestHash);
    }

    [Fact]
    public void CompilationDeepSnapshotsCallerOwnedProjectionAndPolicyCollections()
    {
        var allowedValues = new List<string> { "before" };
        var structuredExample = new Dictionary<string, object?>
        {
            ["nested"] = new List<string> { "before" }
        };
        var fields = new List<ToolInputField>
        {
            new(
                "mode",
                Required: true,
                AllowedValues: allowedValues,
                Example: structuredExample)
        };
        var complements = new List<string> { "tool.beta" };
        var canBatchWith = new List<string> { "tool.gamma" };
        var shouldPrecede = new List<string> { "tool.delta" };
        var scopeKeys = new List<string> { "mode" };
        var reads = new HashSet<ToolDataBoundary> { ToolDataBoundary.HostState };
        var registration = new ToolRegistration(
            new ToolDescriptor(
                "tool.alpha",
                "Alpha",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: new ToolInputSchema(fields),
                ContextHint: new ToolContextHint("state", complements, canBatchWith, shouldPrecede),
                Cooldown: new ToolCooldownPolicy(ScopeInputKeys: scopeKeys),
                RetrySafety: ToolRetrySafety.Idempotent),
            new CountingTool(),
            new ToolSecurityDeclaration(
                ToolEffect.ReadOnly,
                reads,
                [ToolDataBoundary.HostState],
                ToolExternalOutputClassification.None,
                ToolApprovalRequirement.None,
                ToolRetrySafety.Idempotent,
                BuiltInProvenance()));
        var catalog = ToolCatalog.Create(registration);
        var originalHash = catalog.ManifestHash;

        allowedValues.Add("after");
        ((List<string>)structuredExample["nested"]!).Add("after");
        fields.Clear();
        complements.Add("tool.changed");
        canBatchWith.Clear();
        shouldPrecede.Add("tool.changed");
        scopeKeys.Add("changed");
        reads.Add(ToolDataBoundary.WorkspaceContent);

        var projection = Assert.Single(catalog.Descriptors);
        var field = Assert.Single(projection.InputSchema!.Fields);
        Assert.Equal(["before"], field.AllowedValues);
        var example = Assert.IsType<JsonElement>(field.Example);
        Assert.Equal("before", example.GetProperty("nested")[0].GetString());
        Assert.Equal(["tool.beta"], projection.ContextHint!.Complements);
        Assert.Equal(["tool.gamma"], projection.ContextHint.CanBatchWith);
        Assert.Equal(["tool.delta"], projection.ContextHint.ShouldPrecede);
        Assert.Equal(["mode"], projection.Cooldown!.ScopeInputKeys);
        Assert.DoesNotContain(
            ToolDataBoundary.WorkspaceContent,
            catalog.Manifest.Resolve("tool.alpha")!.Security.Reads);
        Assert.Equal(originalHash, catalog.ManifestHash);
        Assert.NotEqual(originalHash, ToolManifestCompiler.Compile([registration]).ManifestHash);

        var mutableEffects = new HashSet<ToolEffect> { ToolEffect.ReadOnly };
        var effectPolicy = new ToolEffectPolicy(mutableEffects);
        mutableEffects.Add(ToolEffect.ExternalSideEffect);
        Assert.False(effectPolicy.Allows(ToolEffect.ExternalSideEffect));

        var initial = new List<ToolDataBoundary> { ToolDataBoundary.UserContent };
        var external = new List<ToolDataBoundary> { ToolDataBoundary.UserContent };
        var securityPolicy = new ToolSecurityPolicy(initial, external);
        initial.Add(ToolDataBoundary.WorkspaceContent);
        external.Clear();
        Assert.Contains(ToolDataBoundary.UserContent, securityPolicy.InitialBoundaries);
        Assert.Contains(ToolDataBoundary.UserContent, securityPolicy.ExternalPlannerAllowedBoundaries!);
    }

    [Fact]
    public void CompilerRejectsDescriptorSecurityMismatches()
    {
        Assert.Throws<ArgumentException>(() => ToolCatalog.Create(Registration(
            descriptorEffect: ToolEffect.ReadOnly,
            securityEffect: ToolEffect.WritesLocalState)));
        Assert.Throws<ArgumentException>(() => ToolCatalog.Create(Registration(
            requiresApproval: false,
            approvalRequirement: ToolApprovalRequirement.ExplicitGrant)));
        Assert.Throws<ArgumentException>(() => ToolCatalog.Create(Registration(
            descriptorRetry: ToolRetrySafety.Idempotent,
            securityRetry: ToolRetrySafety.MutationUnsafe)));
    }

    [Fact]
    public void CompilerRejectsEveryUnknownSecurityClassification()
    {
        var registrations = new[]
        {
            Registration(securityEffect: ToolEffect.Unknown),
            Registration(externalOutput: ToolExternalOutputClassification.Unknown),
            Registration(approvalRequirement: ToolApprovalRequirement.Unknown),
            Registration(securityRetry: ToolRetrySafety.Unknown),
            Registration(provenance: new ToolProvenance(ToolProvenanceKind.Unknown, "tests")),
            Registration(reads: [ToolDataBoundary.Unknown]),
            Registration(exposes: [ToolDataBoundary.Unknown])
        };

        foreach (var registration in registrations)
        {
            var exception = Assert.Throws<ArgumentException>(() => ToolCatalog.Create(registration));
            Assert.Contains("Unknown", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SensitiveDispatchRequiresOneExactUnexpiredSufficientGrant()
    {
        var catalog = ToolCatalog.Create(ExternalRegistration(new CountingTool()));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var validGrant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);

        AssertGrantDenied(catalog, plan, []);
        AssertGrantAllowed(catalog, plan, [validGrant]);
        AssertGrantDenied(catalog, plan,
        [
            Grant(
                catalog.ManifestHash,
                "external.send",
                [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
                [ToolExternalOutputClassification.UntrustedStructuredData],
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ]);
        AssertGrantDenied(catalog, plan,
        [
            Grant(
                FakeManifestHash(),
                "external.send",
                [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
                [ToolExternalOutputClassification.UntrustedStructuredData])
        ]);
        AssertGrantDenied(catalog, plan,
        [
            Grant(
                catalog.ManifestHash,
                "external.other",
                [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
                [ToolExternalOutputClassification.UntrustedStructuredData])
        ]);
        AssertGrantDenied(catalog, plan,
        [
            Grant(
                catalog.ManifestHash,
                "external.send",
                [ToolDataBoundary.UserContent],
                [ToolExternalOutputClassification.UntrustedStructuredData])
        ]);
        AssertGrantDenied(catalog, plan,
        [
            Grant(
                catalog.ManifestHash,
                "external.send",
                [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
                [ToolExternalOutputClassification.UntrustedText])
        ]);
    }

    [Fact]
    public async Task ExactGrantAllowsDispatchButNeverOverridesTheIndependentEffectPolicy()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(ExternalRegistration(tool));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var grant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);

        var allowedRunner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy([grant]),
            new InMemoryEventSink());
        var allowed = await allowedRunner.RunAsync(new RunRequest("authorized external dispatch"));
        Assert.Equal(RunOutcomeStatus.Succeeded, allowed.Outcome.Status);
        Assert.Equal(1, tool.Calls);

        var deniedTool = new CountingTool();
        var deniedCatalog = ToolCatalog.Create(ExternalRegistration(deniedTool));
        var deniedGrant = Grant(
            deniedCatalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);
        var deniedRunner = CreateRunner(
            new StaticPlanner(plan),
            deniedCatalog,
            new ExecutionPolicy(
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.LocalOnly,
                SecurityPolicy: new ToolSecurityPolicy(
                    InitialBoundaries: [ToolDataBoundary.UserContent],
                    ExecutionGrants: [deniedGrant])),
            new InMemoryEventSink());
        var denied = await deniedRunner.RunAsync(new RunRequest("effect policy remains independent"));
        Assert.Equal(RunOutcomeStatus.PlanInvalid, denied.Outcome.Status);
        Assert.Contains(denied.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_not_allowed");
        Assert.Equal(0, deniedTool.Calls);
    }

    [Fact]
    public void SecurityPolicyRejectsMalformedOrAmbiguousGrants()
    {
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "not-versioned",
            "external.send",
            [],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            FakeManifestHash(),
            "external.send",
            [ToolDataBoundary.Unknown],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            FakeManifestHash(),
            "external.send",
            [],
            [ToolExternalOutputClassification.Unknown],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));

        var duplicate = Grant(
            FakeManifestHash(),
            "external.send",
            [ToolDataBoundary.UserContent],
            [ToolExternalOutputClassification.None]);
        Assert.Throws<ArgumentException>(() => new ToolSecurityPolicy(ExecutionGrants: [duplicate, duplicate]));
        Assert.False(ToolSecurityPolicy.Local.UsesExternalPlanner);
        Assert.True(new ToolSecurityPolicy(ExternalPlannerAllowedBoundaries: []).UsesExternalPlanner);
    }

    [Fact]
    public async Task ExternalPlannerCannotRunWithoutAnExplicitBoundaryPolicy()
    {
        var planner = new ExternalStaticPlanner(OneStepPlan(
            "local.read",
            ToolKind.Query,
            ToolEffect.ReadOnly));
        var tool = new CountingTool();
        var runner = CreateRunner(
            planner,
            ToolCatalog.Create(LocalRegistration("local.read", tool)),
            ExecutionPolicy.Default,
            new InMemoryEventSink());

        var result = await runner.RunAsync(new RunRequest("test external planner profile"));

        Assert.Equal(RunOutcomeStatus.Blocked, result.Outcome.Status);
        Assert.Equal(StopReason.PlannerDataBoundaryDenied, result.Outcome.StopReason);
        Assert.Equal(0, planner.CreateCalls);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task InitialExternalPlannerCallFailsClosedWhenInitialTaintExceedsPolicy()
    {
        var planner = new ExternalStaticPlanner(OneStepPlan(
            "local.read",
            ToolKind.Query,
            ToolEffect.ReadOnly));
        var runner = CreateRunner(
            planner,
            ToolCatalog.Create(LocalRegistration("local.read")),
            new ExecutionPolicy(SecurityPolicy: new ToolSecurityPolicy(
                InitialBoundaries: [ToolDataBoundary.WorkspaceContent],
                ExternalPlannerAllowedBoundaries: [ToolDataBoundary.UserContent])),
            new InMemoryEventSink());

        var result = await runner.RunAsync(new RunRequest("test initial egress"));

        Assert.Equal(RunOutcomeStatus.Blocked, result.Outcome.Status);
        Assert.Equal(StopReason.PlannerDataBoundaryDenied, result.Outcome.StopReason);
        Assert.Equal(0, planner.CreateCalls);
    }

    [Fact]
    public async Task ExplicitEmptyExternalPlannerAllowanceIsNotTreatedAsLocal()
    {
        var planner = new ExternalStaticPlanner(OneStepPlan(
            "local.read",
            ToolKind.Query,
            ToolEffect.ReadOnly));
        var runner = CreateRunner(
            planner,
            ToolCatalog.Create(LocalRegistration("local.read")),
            new ExecutionPolicy(SecurityPolicy: new ToolSecurityPolicy(
                ExternalPlannerAllowedBoundaries: [])),
            new InMemoryEventSink());

        var result = await runner.RunAsync(new RunRequest("user objective is classified"));

        Assert.Equal(RunOutcomeStatus.Blocked, result.Outcome.Status);
        Assert.Equal(StopReason.PlannerDataBoundaryDenied, result.Outcome.StopReason);
        Assert.Equal(0, planner.CreateCalls);
    }

    [Fact]
    public async Task PlannerVisibleToolTaintIsRejectedBeforeExternalRefinement()
    {
        var planner = new ExternalStaticPlanner(OneStepPlan(
            "local.read",
            ToolKind.Query,
            ToolEffect.ReadOnly));
        var tool = new CountingTool(includeObservation: true);
        var registration = LocalRegistration(
            "local.read",
            tool,
            reads: [ToolDataBoundary.HostState],
            exposes: [ToolDataBoundary.WorkspaceContent]);
        var runner = CreateRunner(
            planner,
            ToolCatalog.Create(registration),
            new ExecutionPolicy(
                MaxRefinements: 1,
                SecurityPolicy: new ToolSecurityPolicy(
                    InitialBoundaries: [ToolDataBoundary.UserContent],
                    ExternalPlannerAllowedBoundaries: [ToolDataBoundary.UserContent])),
            new InMemoryEventSink());

        var result = await runner.RunAsync(new RunRequest("test refinement egress"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, result.Outcome.Status);
        Assert.Contains(
            result.Details.ValidationIssues,
            issue => issue.Code == "plan.step.planner_boundary_not_allowed");
        Assert.Equal(1, planner.CreateCalls);
        Assert.Equal(0, planner.RefineCalls);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task RegistrationMutationAtStepStartedFailsClosedBeforeToolCall()
    {
        var allowedValues = new List<string> { "before" };
        var descriptor = new ToolDescriptor(
            "local.read",
            "Read",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            InputSchema: ToolInputSchema.Create(new ToolInputField(
                "mode",
                Required: true,
                AllowedValues: allowedValues)),
            RetrySafety: ToolRetrySafety.Idempotent);
        var tool = new CountingTool();
        var registration = new ToolRegistration(
            descriptor,
            tool,
            Security(
                ToolEffect.ReadOnly,
                ToolRetrySafety.Idempotent,
                [ToolDataBoundary.HostState],
                [ToolDataBoundary.HostState]));
        var plan = OneStepPlan(
            "local.read",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            new Dictionary<string, object?> { ["mode"] = "before" });
        var eventSink = new CallbackEventSink(executionEvent =>
        {
            if (executionEvent.Type == "step.started")
            {
                allowedValues.Add("after");
            }
        });
        var runner = CreateRunner(
            new StaticPlanner(plan),
            ToolCatalog.Create(registration),
            new ExecutionPolicy(PlanningMode: PlanningMode.PlanOnly),
            eventSink);

        var result = await runner.RunAsync(new RunRequest("test stale registration"));

        Assert.Equal(RunOutcomeStatus.Blocked, result.Outcome.Status);
        Assert.Equal(StopReason.ToolRefused, result.Outcome.StopReason);
        Assert.Equal(0, tool.Calls);
        var receipt = Assert.Single(result.Receipts.Items);
        Assert.Equal(ReceiptStatus.Refused, receipt.Status);
        Assert.Equal("tool.security.manifest_changed", receipt.Data["securityCode"]);
    }

    private static void AssertGrantAllowed(
        ToolCatalog catalog,
        WorkflowPlan plan,
        IReadOnlyList<ToolExecutionGrant> grants)
    {
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy(grants),
            new InMemoryEventSink());
        Assert.DoesNotContain(
            runner.ValidatePlan(plan),
            issue => issue.Code == "tool.security.grant_required");
    }

    private static void AssertGrantDenied(
        ToolCatalog catalog,
        WorkflowPlan plan,
        IReadOnlyList<ToolExecutionGrant> grants)
    {
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy(grants),
            new InMemoryEventSink());
        Assert.Contains(
            runner.ValidatePlan(plan),
            issue => issue.Code == "tool.security.grant_required");
    }

    private static ExecutionPolicy SensitivePolicy(IReadOnlyList<ToolExecutionGrant> grants) =>
        new(
            PlanningMode: PlanningMode.PlanOnly,
            EffectPolicy: ToolEffectPolicy.AllowKnown,
            SecurityPolicy: new ToolSecurityPolicy(
                InitialBoundaries: [ToolDataBoundary.UserContent],
                ExecutionGrants: grants));

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ExecutionPolicy policy,
        IEventSink eventSink) =>
        new(
            planner,
            catalog,
            eventSink,
            new DeterministicOutcomeReporter(),
            policy,
            PlanExhaustionCompletionEvaluator.Instance);

    private static WorkflowPlan OneStepPlan(
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        IReadOnlyDictionary<string, object?>? input = null) =>
        new(
            "plan.security",
            1,
            [new PlanStep("step.security", toolId, kind, effect, input ?? new Dictionary<string, object?>())],
            "Security test plan.");

    private static ToolRegistration LocalRegistration(
        string toolId,
        CountingTool? tool = null,
        IReadOnlyList<ToolDataBoundary>? reads = null,
        IReadOnlyList<ToolDataBoundary>? exposes = null) =>
        new(
            new ToolDescriptor(
                toolId,
                toolId,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                RetrySafety: ToolRetrySafety.Idempotent),
            tool ?? new CountingTool(),
            Security(
                ToolEffect.ReadOnly,
                ToolRetrySafety.Idempotent,
                reads ?? [],
                exposes ?? []));

    private static ToolRegistration ExternalRegistration(CountingTool tool) =>
        new(
            new ToolDescriptor(
                "external.send",
                "External Send",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect,
                RequiresApproval: true,
                RetrySafety: ToolRetrySafety.MutationUnsafe),
            tool,
            new ToolSecurityDeclaration(
                ToolEffect.ExternalSideEffect,
                [ToolDataBoundary.WorkspaceContent],
                [ToolDataBoundary.ExternalUntrusted],
                ToolExternalOutputClassification.UntrustedStructuredData,
                ToolApprovalRequirement.ExplicitGrant,
                ToolRetrySafety.MutationUnsafe,
                BuiltInProvenance()));

    private static ToolRegistration Registration(
        ToolEffect descriptorEffect = ToolEffect.ReadOnly,
        ToolEffect securityEffect = ToolEffect.ReadOnly,
        bool requiresApproval = false,
        ToolApprovalRequirement approvalRequirement = ToolApprovalRequirement.None,
        ToolRetrySafety descriptorRetry = ToolRetrySafety.Idempotent,
        ToolRetrySafety securityRetry = ToolRetrySafety.Idempotent,
        ToolExternalOutputClassification externalOutput = ToolExternalOutputClassification.None,
        ToolProvenance? provenance = null,
        IReadOnlyList<ToolDataBoundary>? reads = null,
        IReadOnlyList<ToolDataBoundary>? exposes = null) =>
        new(
            new ToolDescriptor(
                "tool.test",
                "Test",
                ToolKind.Query,
                descriptorEffect,
                requiresApproval,
                RetrySafety: descriptorRetry),
            new CountingTool(),
            new ToolSecurityDeclaration(
                securityEffect,
                reads ?? [],
                exposes ?? [],
                externalOutput,
                approvalRequirement,
                securityRetry,
                provenance ?? BuiltInProvenance()));

    private static ToolSecurityDeclaration Security(
        ToolEffect effect,
        ToolRetrySafety retrySafety,
        IEnumerable<ToolDataBoundary> reads,
        IEnumerable<ToolDataBoundary> exposes) =>
        new(
            effect,
            reads,
            exposes,
            ToolExternalOutputClassification.None,
            ToolApprovalRequirement.None,
            retrySafety,
            BuiltInProvenance());

    private static ToolProvenance BuiltInProvenance() =>
        new(ToolProvenanceKind.BuiltIn, "Agentica.Tests", "1");

    private static ToolExecutionGrant Grant(
        string manifestHash,
        string toolId,
        IEnumerable<ToolDataBoundary> boundaries,
        IEnumerable<ToolExternalOutputClassification> outputs,
        DateTimeOffset? expiresAt = null) =>
        new(
            manifestHash,
            toolId,
            boundaries,
            outputs,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5),
            "Agentica.Tests");

    private static string FakeManifestHash() =>
        $"sha256-v1:{new string('0', 64)}";

    private sealed class CountingTool(bool includeObservation = false) : ITool
    {
        public int Calls { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Succeeded,
                "ok",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>());
            var observation = includeObservation
                ? new Observation(
                    AgenticaIds.New("observation"),
                    invocation.StepId,
                    ObservationKind.StateQuery,
                    "state",
                    new Dictionary<string, object?>(),
                    [new EvidenceRef("receipt", receipt.ReceiptId)])
                : null;
            return Task.FromResult(new ToolResult(receipt, observation));
        }
    }

    private class StaticPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public int CreateCalls { get; protected set; }

        public int RefineCalls { get; protected set; }

        public virtual Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(plan);
        }

        public virtual Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            RefineCalls++;
            return Task.FromResult(plan with
            {
                PlanId = $"{plan.PlanId}.refined",
                Version = plan.Version + 1
            });
        }
    }

    private sealed class ExternalStaticPlanner(WorkflowPlan plan) : StaticPlanner(plan), IExternalWorkflowPlanner
    {
    }

    private sealed class CallbackEventSink(Action<ExecutionEvent> callback) : IEventSink
    {
        public void Emit(ExecutionEvent executionEvent) => callback(executionEvent);
    }
}

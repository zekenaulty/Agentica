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
    private const string TestAuthorizationScopeId = "authorization_scope_tool_security_tests";

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
    public void Compiler_bounds_dishonest_allowed_value_enumeration_independently_of_count()
    {
        var allowedValues = new DishonestReadOnlyList<string>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => $"value_{index}");
        var registration = RegistrationWithDescriptor(new ToolDescriptor(
            "tool.hostile.allowed_values",
            "Hostile allowed values",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            InputSchema: ToolInputSchema.Create(new ToolInputField(
                "mode",
                AllowedValues: allowedValues)),
            RetrySafety: ToolRetrySafety.Idempotent));

        Assert.Throws<InvalidOperationException>(() => ToolCatalog.Create(registration));

        Assert.True(allowedValues.EnumerationCount > allowedValues.Count);
        Assert.InRange(allowedValues.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public void Compiler_bounds_hostile_structured_example_enumeration()
    {
        var example = new DishonestReadOnlyList<object?>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => $"value_{index}");
        var registration = RegistrationWithDescriptor(new ToolDescriptor(
            "tool.hostile.example",
            "Hostile example",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            InputSchema: ToolInputSchema.Create(new ToolInputField(
                "items",
                Example: example)),
            RetrySafety: ToolRetrySafety.Idempotent));

        Assert.Throws<InvalidOperationException>(() => ToolCatalog.Create(registration));

        Assert.True(example.EnumerationCount > example.Count);
        Assert.InRange(example.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public void Compiler_enforces_one_aggregate_budget_across_descriptor_metadata()
    {
        var largeText = new string('m', 210_000);
        var registration = RegistrationWithDescriptor(new ToolDescriptor(
            "tool.aggregate.metadata",
            "Aggregate metadata",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            InputSchema: ToolInputSchema.Create(new ToolInputField(
                "value",
                Description: largeText)),
            Description: largeText,
            ContextHint: new ToolContextHint(
                largeText,
                [],
                [],
                [])
            {
                UseWhen = largeText
            },
            Cooldown: new ToolCooldownPolicy(
                PlanStepCount: 1,
                Reason: largeText),
            RetrySafety: ToolRetrySafety.Idempotent));

        Assert.Throws<InvalidOperationException>(() => ToolCatalog.Create(registration));
    }

    [Fact]
    public void Compiler_rejects_oversized_field_name_before_unicode_normalization()
    {
        var registration = RegistrationWithDescriptor(new ToolDescriptor(
            "tool.oversized.field_name",
            "Oversized field name",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            InputSchema: ToolInputSchema.Create(new ToolInputField(new string('n', 300_000))),
            RetrySafety: ToolRetrySafety.Idempotent));

        Assert.Throws<InvalidOperationException>(() => ToolCatalog.Create(registration));
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
    public void SecurityBoundariesRejectEveryUndefinedEnumValue()
    {
        var invalidBoundary = (ToolDataBoundary)999;
        var invalidOutput = (ToolExternalOutputClassification)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSecurityDeclaration(
            (ToolEffect)999,
            [],
            [],
            ToolExternalOutputClassification.None,
            ToolApprovalRequirement.None,
            ToolRetrySafety.Idempotent,
            BuiltInProvenance()));
        Assert.Throws<ArgumentException>(() => new ToolSecurityDeclaration(
            ToolEffect.ReadOnly,
            [invalidBoundary],
            [],
            ToolExternalOutputClassification.None,
            ToolApprovalRequirement.None,
            ToolRetrySafety.Idempotent,
            BuiltInProvenance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSecurityDeclaration(
            ToolEffect.ReadOnly,
            [],
            [],
            invalidOutput,
            ToolApprovalRequirement.None,
            ToolRetrySafety.Idempotent,
            BuiltInProvenance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSecurityDeclaration(
            ToolEffect.ReadOnly,
            [],
            [],
            ToolExternalOutputClassification.None,
            (ToolApprovalRequirement)999,
            ToolRetrySafety.Idempotent,
            BuiltInProvenance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSecurityDeclaration(
            ToolEffect.ReadOnly,
            [],
            [],
            ToolExternalOutputClassification.None,
            ToolApprovalRequirement.None,
            (ToolRetrySafety)999,
            BuiltInProvenance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolSecurityDeclaration(
            ToolEffect.ReadOnly,
            [],
            [],
            ToolExternalOutputClassification.None,
            ToolApprovalRequirement.None,
            ToolRetrySafety.Idempotent,
            new ToolProvenance((ToolProvenanceKind)999, "tests")));
        Assert.Throws<ArgumentException>(() => new ToolEffectPolicy([(ToolEffect)999]));
        Assert.Throws<ArgumentException>(() => new ToolSecurityPolicy(
            InitialBoundaries: [invalidBoundary]));

        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_invalid_boundary_numeric",
            TestAuthorizationScopeId,
            1,
            "step.security",
            ToolInvocationAuthorization.ComputeInputDigest(new Dictionary<string, object?>()),
            FakeManifestHash(),
            "external.send",
            [invalidBoundary],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_invalid_output_numeric",
            TestAuthorizationScopeId,
            1,
            "step.security",
            ToolInvocationAuthorization.ComputeInputDigest(new Dictionary<string, object?>()),
            FakeManifestHash(),
            "external.send",
            [],
            [invalidOutput],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));

        var registration = LocalRegistration("tool.undefined");
        Assert.Throws<ArgumentException>(() => ToolCatalog.Create(registration with
        {
            Descriptor = registration.Descriptor with { Kind = (ToolKind)999 }
        }));
        Assert.Throws<ArgumentException>(() => ToolCatalog.Create(registration with
        {
            Descriptor = registration.Descriptor with { RetrySafety = (ToolRetrySafety)999 }
        }));
        Assert.Throws<ArgumentException>(() => ToolCatalog.Create(registration with
        {
            Descriptor = registration.Descriptor with
            {
                InputSchema = ToolInputSchema.Create(new ToolInputField(
                    "value",
                    (ToolInputValueType)999))
            }
        }));
    }

    [Fact]
    public void Grant_issuance_bounds_text_and_dishonest_authority_enumeration()
    {
        var digest = ToolInvocationAuthorization.ComputeInputDigest(
            new Dictionary<string, object?>());
        var dishonestBoundaries = new DishonestReadOnlyList<ToolDataBoundary>(
            reportedCount: 1,
            yieldedCount: 20_000,
            _ => ToolDataBoundary.UserContent);

        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            new string('g', 4_097),
            TestAuthorizationScopeId,
            1,
            "step.security",
            digest,
            FakeManifestHash(),
            "external.send",
            [],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_oversized_issuer",
            TestAuthorizationScopeId,
            1,
            "step.security",
            digest,
            FakeManifestHash(),
            "external.send",
            [],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            new string('i', 4_097)));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_dishonest_boundaries",
            TestAuthorizationScopeId,
            1,
            "step.security",
            digest,
            FakeManifestHash(),
            "external.send",
            dishonestBoundaries,
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.InRange(dishonestBoundaries.EnumerationCount, 1, 33);
    }

    [Fact]
    public void InvocationDigestPreservesExactStringsKeysAndNumericTypes()
    {
        static string Digest(string key, object? value) =>
            ToolInvocationAuthorization.ComputeInputDigest(
                new Dictionary<string, object?>(StringComparer.Ordinal) { [key] = value });

        Assert.NotEqual(Digest("value", "\u00e9"), Digest("value", "e\u0301"));
        Assert.NotEqual(Digest("value", "line\r\nnext"), Digest("value", "line\nnext"));
        Assert.NotEqual(Digest("\u00e9", "value"), Digest("e\u0301", "value"));
        Assert.NotEqual(Digest("line\r\nnext", "value"), Digest("line\nnext", "value"));

        var numericDigests = new object[] { (byte)1, (short)1, 1, 1L, 1U, 1UL, 1F, 1D, 1M }
            .Select(value => Digest("value", value))
            .ToArray();
        Assert.Equal(numericDigests.Length, numericDigests.Distinct(StringComparer.Ordinal).Count());

        var firstShape = ToolInvocationAuthorization.ComputeInputDigest(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["a"] = "b\0c"
            });
        var secondShape = ToolInvocationAuthorization.ComputeInputDigest(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["a\0b"] = "c"
            });
        Assert.NotEqual(firstShape, secondShape);
    }

    [Fact]
    public void InvocationDigestBoundsDictionaryEnumerationIndependentlyOfReportedCount()
    {
        var input = new MisreportedCountDictionary(20_000);

        var exception = Assert.Throws<InvalidOperationException>(
            () => ToolInvocationAuthorization.ComputeInputDigest(input));

        Assert.Contains("16384 entries", exception.Message, StringComparison.Ordinal);
        Assert.InRange(input.EnumeratedEntries, 1, 16_385);
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
    public void PublicPlanValidationNeverInfersAnAuthorizationScope()
    {
        var catalog = ToolCatalog.Create(ExternalRegistration(new CountingTool()));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var grant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy([grant]),
            new InMemoryEventSink());

        Assert.Contains(
            runner.ValidatePlan(plan),
            issue => issue.Code == "tool.security.grant_required");
        Assert.Contains(
            runner.ValidatePlan(plan, "wrong_scope"),
            issue => issue.Code == "tool.security.grant_required");
        Assert.DoesNotContain(
            runner.ValidatePlan(plan, TestAuthorizationScopeId),
            issue => issue.Code == "tool.security.grant_required");
    }

    [Fact]
    public async Task DeferredPlanTicketNeverSubstitutesForExactDispatchInputAuthorization()
    {
        var sourceTool = new CountingTool();
        var sensitiveTool = new CountingTool();
        var sensitiveRegistration = ApprovalRegistration("sensitive.deferred", sensitiveTool);
        var catalog = ToolCatalog.Create(
            LocalRegistration("local.source", sourceTool),
            sensitiveRegistration);
        var plan = new WorkflowPlan(
            "plan.deferred.security",
            1,
            [
                new PlanStep(
                    "step.source",
                    "local.source",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?>()),
                new PlanStep(
                    "step.deferred",
                    "sensitive.deferred",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?> { ["value"] = "not-approved" })
                {
                    DependsOn = ["step.source"]
                }
            ],
            "A dependency-blocked grant can only be provisionally validated.");
        var grant = Grant(
            catalog.ManifestHash,
            "sensitive.deferred",
            [ToolDataBoundary.UserContent],
            [ToolExternalOutputClassification.None],
            input: new Dictionary<string, object?> { ["value"] = "approved" },
            stepId: "step.deferred");
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy([grant]),
            new InMemoryEventSink());

        Assert.Contains(
            runner.ValidatePlan(plan, TestAuthorizationScopeId),
            issue => issue.Code == "tool.security.grant_required");

        var envelope = await runner.RunAsync(new RunRequest(
            "Never dispatch drifted deferred input.",
            AuthorizationScopeId: TestAuthorizationScopeId));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolRefused, envelope.Outcome.StopReason);
        Assert.Equal(1, sourceTool.Calls);
        Assert.Equal(0, sensitiveTool.Calls);
        Assert.False(grant.IsConsumed);
        Assert.Contains(
            envelope.Receipts.Items,
            receipt => receipt.Data.TryGetValue("securityCode", out var code) &&
                Equals(code, "tool.security.grant_required"));
    }

    [Fact]
    public async Task CanonicalResultAliasIsRestoredBeforeExactGrantCheckAndToolDispatch()
    {
        const string sourceReceiptId = "provider-receipt-source-id";
        var sourceTool = new SourceIdentityTool(sourceReceiptId);
        var targetTool = new CapturingInputTool("receiptId");
        var catalog = ToolCatalog.Create(
            LocalRegistration("local.alias-source", sourceTool),
            ApprovalRegistration("sensitive.alias-target", targetTool));
        var approvedInput = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["receiptId"] = sourceReceiptId
        };
        var grant = Grant(
            catalog.ManifestHash,
            "sensitive.alias-target",
            [ToolDataBoundary.UserContent],
            [ToolExternalOutputClassification.None],
            input: approvedInput,
            stepId: "step.alias-target");
        var planner = new AliasRefinementPlanner();
        var policy = SensitivePolicy([grant]) with
        {
            MaxSteps = 3,
            MaxRefinements = 1,
            PlanningMode = PlanningMode.QueryAndBlockerDriven
        };
        var runner = CreateRunner(planner, catalog, policy, new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest(
            "Use a provider identity in a granted follow-up.",
            AuthorizationScopeId: TestAuthorizationScopeId));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.NotNull(planner.CanonicalReceiptId);
        Assert.NotEqual(sourceReceiptId, planner.CanonicalReceiptId);
        Assert.Equal(sourceReceiptId, targetTool.ObservedValue);
        Assert.Equal(grant.InvocationInputDigest, Assert.Single(
            envelope.Details.GrantConsumptions).InvocationInputDigest);
    }

    [Fact]
    public async Task GrantExpiryIsRecheckedAfterGrantEventDeliveryAndBeforeToolCall()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(ExternalRegistration(tool));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(1_500);
        var grant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData],
            expiresAt);
        var sink = new DelayUntilEventSink(
            ExecutionEventType.GrantConsumed.WireName(),
            expiresAt.AddMilliseconds(50));
        var policy = SensitivePolicy([grant]) with
        {
            EventSinkDeliveryTimeout = TimeSpan.FromSeconds(3)
        };
        var runner = CreateRunner(new StaticPlanner(plan), catalog, policy, sink);

        var envelope = await runner.RunAsync(new RunRequest(
            "Expire approval at the dispatch edge.",
            AuthorizationScopeId: TestAuthorizationScopeId));

        Assert.True(sink.Delayed);
        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolRefused, envelope.Outcome.StopReason);
        Assert.Equal(0, tool.Calls);
        Assert.True(grant.IsConsumed);
        Assert.Single(envelope.Details.GrantConsumptions);
        var refusal = Assert.Single(
            envelope.Receipts.Items,
            receipt => receipt.Status == ReceiptStatus.Refused);
        Assert.Equal("tool.security.grant_expired", refusal.Data["securityCode"]);
    }

    [Fact]
    public async Task ParallelGrantEventsHaveStrictUniqueLedgerSequences()
    {
        const int stepCount = 8;
        var tool = new YieldingCountingTool();
        var catalog = ToolCatalog.Create(ApprovalRegistration("sensitive.parallel", tool));
        var steps = Enumerable.Range(0, stepCount)
            .Select(index => new PlanStep(
                $"step.parallel.{index}",
                "sensitive.parallel",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new Dictionary<string, object?> { ["index"] = index })
            {
                BatchId = "batch.parallel"
            })
            .ToArray();
        var plan = new WorkflowPlan("plan.parallel.security", 1, steps, "Parallel grant event ordering.");
        var grants = steps.Select(step => Grant(
                catalog.ManifestHash,
                "sensitive.parallel",
                [ToolDataBoundary.UserContent],
                [ToolExternalOutputClassification.None],
                input: step.Input,
                stepId: step.StepId))
            .ToArray();
        var policy = SensitivePolicy(grants) with
        {
            MaxSteps = stepCount + 1,
            MaxBatchSize = stepCount,
            MaxParallelism = stepCount
        };
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            policy,
            new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest(
            "Run an approval-bound read batch.",
            AuthorizationScopeId: TestAuthorizationScopeId));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(stepCount, tool.Calls);
        Assert.Equal(stepCount, envelope.Details.GrantConsumptions.Count);
        Assert.Equal(
            Enumerable.Range(1, envelope.Details.Events.Count).Select(index => (long?)index),
            envelope.Details.Events.Select(executionEvent => executionEvent.Sequence));
        Assert.Equal(
            envelope.Details.Events.Count,
            envelope.Details.Events.Select(executionEvent => executionEvent.EventId)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void ExistingExecutionEventNumericValuesRemainStable()
    {
        Assert.Equal(0, (int)ExecutionEventType.RunCreated);
        Assert.Equal(1, (int)ExecutionEventType.RequestAccepted);
        Assert.Equal(2, (int)ExecutionEventType.PlanCreationStarted);
        Assert.Equal(3, (int)ExecutionEventType.PlanCreationCancelled);
        Assert.Equal(4, (int)ExecutionEventType.PlanCreated);
        Assert.Equal(5, (int)ExecutionEventType.PlanContinuationStarted);
        Assert.Equal(6, (int)ExecutionEventType.PlanContinuationCancelled);
        Assert.Equal(7, (int)ExecutionEventType.BatchStarted);
        Assert.Equal(8, (int)ExecutionEventType.BatchCompleted);
        Assert.Equal(9, (int)ExecutionEventType.StepStarted);
        Assert.Equal(10, (int)ExecutionEventType.ObservationMade);
        Assert.Equal(11, (int)ExecutionEventType.ReceiptEmitted);
        Assert.Equal(12, (int)ExecutionEventType.PlanRefinementStarted);
        Assert.Equal(13, (int)ExecutionEventType.PlanRefinementCancelled);
        Assert.Equal(14, (int)ExecutionEventType.PlanRefined);
        Assert.Equal(15, (int)ExecutionEventType.OutcomeReported);
        Assert.Equal(16, (int)ExecutionEventType.RunSucceeded);
        Assert.Equal(17, (int)ExecutionEventType.RunBlocked);
        Assert.Equal(18, (int)ExecutionEventType.RunFailed);
        Assert.Equal(19, (int)ExecutionEventType.RunStopped);
        Assert.Equal(20, (int)ExecutionEventType.GrantConsumed);
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
        var allowed = await allowedRunner.RunAsync(new RunRequest(
            "authorized external dispatch",
            AuthorizationScopeId: TestAuthorizationScopeId));
        Assert.Equal(RunOutcomeStatus.Succeeded, allowed.Outcome.Status);
        Assert.Equal(1, tool.Calls);
        var consumption = Assert.Single(allowed.Details.GrantConsumptions);
        Assert.Equal(grant.GrantId, consumption.GrantId);
        Assert.Equal(allowed.Outcome.RunId, consumption.RunId);
        Assert.Equal("step.security", consumption.StepId);
        Assert.Equal(grant.InvocationInputDigest, consumption.InvocationInputDigest);
        Assert.Equal(grant.Issuer, consumption.Issuer);
        Assert.Equal(grant.ExpiresAt, consumption.ExpiresAt);
        Assert.Equal(
            grant.AllowedOutboundBoundaries.OrderBy(boundary => boundary),
            consumption.AllowedOutboundBoundaries);
        Assert.Equal(
            grant.AllowedExternalOutputs.OrderBy(output => output),
            consumption.AllowedExternalOutputs);
        var consumptionEvent = Assert.Single(
            allowed.Details.Events,
            item => item.Type == ExecutionEventType.GrantConsumed.WireName());
        Assert.Equal(grant.Issuer, consumptionEvent.Payload["issuer"]);
        Assert.Equal(
            grant.ExpiresAt,
            Assert.IsType<DateTimeOffset>(consumptionEvent.Payload["expiresAt"]));
        Assert.Equal(
            grant.AllowedOutboundBoundaries.Select(boundary => boundary.ToString()).Order(),
            Assert.IsAssignableFrom<IEnumerable<object?>>(
                    consumptionEvent.Payload["allowedOutboundBoundaries"])
                .Select(Convert.ToString)
                .Order());
        Assert.Equal(
            grant.AllowedExternalOutputs.Select(output => output.ToString()).Order(),
            Assert.IsAssignableFrom<IEnumerable<object?>>(
                    consumptionEvent.Payload["allowedExternalOutputs"])
                .Select(Convert.ToString)
                .Order());

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
        var denied = await deniedRunner.RunAsync(new RunRequest(
            "effect policy remains independent",
            AuthorizationScopeId: TestAuthorizationScopeId));
        Assert.Equal(RunOutcomeStatus.PlanInvalid, denied.Outcome.Status);
        Assert.Contains(denied.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_not_allowed");
        Assert.Equal(0, deniedTool.Calls);
    }

    [Fact]
    public async Task OneShotGrantCannotCrossRunsAndSecondRunFailsClosedBeforeDispatch()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(ExternalRegistration(tool));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var grant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy([grant]),
            new InMemoryEventSink());
        var request = new RunRequest("dispatch once", AuthorizationScopeId: TestAuthorizationScopeId);

        var first = await runner.RunAsync(request);
        var second = await runner.RunAsync(request);

        Assert.Equal(RunOutcomeStatus.Succeeded, first.Outcome.Status);
        Assert.Equal(RunOutcomeStatus.PlanInvalid, second.Outcome.Status);
        Assert.Contains(second.Details.ValidationIssues, issue => issue.Code == "tool.security.grant_consumed");
        Assert.Equal(1, tool.Calls);
        Assert.Single(first.Details.GrantConsumptions);
        Assert.Empty(second.Details.GrantConsumptions);
    }

    [Fact]
    public async Task EquivalentFreshGrantsAuthorizeExactlyTwoIndependentDispatches()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(ExternalRegistration(tool));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var firstGrant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);
        var secondGrant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            SensitivePolicy([firstGrant, secondGrant]),
            new InMemoryEventSink());
        var request = new RunRequest("dispatch twice", AuthorizationScopeId: TestAuthorizationScopeId);

        var first = await runner.RunAsync(request);
        var second = await runner.RunAsync(request);
        var third = await runner.RunAsync(request);

        Assert.Equal(RunOutcomeStatus.Succeeded, first.Outcome.Status);
        Assert.Equal(RunOutcomeStatus.Succeeded, second.Outcome.Status);
        Assert.Equal(RunOutcomeStatus.PlanInvalid, third.Outcome.Status);
        Assert.Equal(2, tool.Calls);
        Assert.NotEqual(
            Assert.Single(first.Details.GrantConsumptions).GrantId,
            Assert.Single(second.Details.GrantConsumptions).GrantId);
    }

    [Fact]
    public async Task BlockedRetryRequiresAndConsumesAFreshAttemptBoundGrant()
    {
        var tool = new UnavailableThenSuccessTool();
        var registration = new ToolRegistration(
            new ToolDescriptor(
                "sensitive.read",
                "Sensitive read",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                RequiresApproval: true,
                RetrySafety: ToolRetrySafety.Idempotent),
            tool,
            new ToolSecurityDeclaration(
                ToolEffect.ReadOnly,
                [ToolDataBoundary.UserContent],
                [ToolDataBoundary.Public],
                ToolExternalOutputClassification.None,
                ToolApprovalRequirement.ExplicitGrant,
                ToolRetrySafety.Idempotent,
                BuiltInProvenance()));
        var catalog = ToolCatalog.Create(registration);
        var plan = OneStepPlan("sensitive.read", ToolKind.Query, ToolEffect.ReadOnly);
        var firstGrant = Grant(
            catalog.ManifestHash,
            "sensitive.read",
            [ToolDataBoundary.UserContent],
            [ToolExternalOutputClassification.None],
            attemptNumber: 1);
        var retryGrant = Grant(
            catalog.ManifestHash,
            "sensitive.read",
            [ToolDataBoundary.UserContent],
            [ToolExternalOutputClassification.None],
            attemptNumber: 2);
        var runner = CreateRunner(
            new StaticPlanner(plan),
            catalog,
            new ExecutionPolicy(
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 1,
                SecurityPolicy: new ToolSecurityPolicy(
                    InitialBoundaries: [ToolDataBoundary.UserContent],
                    ExecutionGrants: [firstGrant, retryGrant])),
            new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest(
            "retry with fresh approval",
            AuthorizationScopeId: TestAuthorizationScopeId));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, tool.Calls);
        var prior = Assert.Single(envelope.PriorAttempts);
        Assert.Equal(StopReason.ToolUnavailable, prior.Outcome.StopReason);
        Assert.Equal(firstGrant.GrantId, Assert.Single(prior.Details.GrantConsumptions).GrantId);
        Assert.Equal(retryGrant.GrantId, Assert.Single(envelope.Details.GrantConsumptions).GrantId);
    }

    [Fact]
    public async Task GrantFailsClosedOnAuthorizationScopeOrInputDrift()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(ExternalRegistration(tool));
        var approvedInput = new Dictionary<string, object?> { ["recipient"] = "A" };
        var changedPlan = OneStepPlan(
            "external.send",
            ToolKind.Action,
            ToolEffect.ExternalSideEffect,
            new Dictionary<string, object?> { ["recipient"] = "B" });
        var grant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData],
            input: approvedInput);

        var inputDrift = await CreateRunner(
                new StaticPlanner(changedPlan),
                catalog,
                SensitivePolicy([grant]),
                new InMemoryEventSink())
            .RunAsync(new RunRequest("input drift", AuthorizationScopeId: TestAuthorizationScopeId));

        var approvedPlan = OneStepPlan(
            "external.send",
            ToolKind.Action,
            ToolEffect.ExternalSideEffect,
            approvedInput);
        var scopeDrift = await CreateRunner(
                new StaticPlanner(approvedPlan),
                catalog,
                SensitivePolicy([grant]),
                new InMemoryEventSink())
            .RunAsync(new RunRequest("scope drift", AuthorizationScopeId: "different_scope"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, inputDrift.Outcome.Status);
        Assert.Equal(RunOutcomeStatus.PlanInvalid, scopeDrift.Outcome.Status);
        Assert.All(
            new[] { inputDrift, scopeDrift },
            envelope => Assert.Contains(
                envelope.Details.ValidationIssues,
                issue => issue.Code == "tool.security.grant_required"));
        Assert.Equal(0, tool.Calls);
        Assert.False(grant.IsConsumed);
    }

    [Fact]
    public async Task AtomicGrantConsumptionAllowsOnlyOneConcurrentRun()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(ExternalRegistration(tool));
        var plan = OneStepPlan("external.send", ToolKind.Action, ToolEffect.ExternalSideEffect);
        var grant = Grant(
            catalog.ManifestHash,
            "external.send",
            [ToolDataBoundary.UserContent, ToolDataBoundary.WorkspaceContent],
            [ToolExternalOutputClassification.UntrustedStructuredData]);
        var policy = SensitivePolicy([grant]);
        var firstRunner = CreateRunner(new StaticPlanner(plan), catalog, policy, new InMemoryEventSink());
        var secondRunner = CreateRunner(new StaticPlanner(plan), catalog, policy, new InMemoryEventSink());
        var request = new RunRequest("race the grant", AuthorizationScopeId: TestAuthorizationScopeId);

        var envelopes = await Task.WhenAll(
            firstRunner.RunAsync(request),
            secondRunner.RunAsync(request));

        Assert.Equal(1, tool.Calls);
        Assert.Single(envelopes, envelope => envelope.Outcome.Status == RunOutcomeStatus.Succeeded);
        Assert.Single(envelopes, envelope => envelope.Outcome.Status != RunOutcomeStatus.Succeeded);
        Assert.Single(envelopes.SelectMany(envelope => envelope.Details.GrantConsumptions));
    }

    [Fact]
    public void SecurityPolicyRejectsMalformedOrAmbiguousGrants()
    {
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_invalid_manifest",
            TestAuthorizationScopeId,
            1,
            "step.security",
            ToolInvocationAuthorization.ComputeInputDigest(new Dictionary<string, object?>()),
            "not-versioned",
            "external.send",
            [],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_invalid_boundary",
            TestAuthorizationScopeId,
            1,
            "step.security",
            ToolInvocationAuthorization.ComputeInputDigest(new Dictionary<string, object?>()),
            FakeManifestHash(),
            "external.send",
            [ToolDataBoundary.Unknown],
            [ToolExternalOutputClassification.None],
            DateTimeOffset.UtcNow.AddMinutes(1),
            "tests"));
        Assert.Throws<ArgumentException>(() => new ToolExecutionGrant(
            "grant_invalid_output",
            TestAuthorizationScopeId,
            1,
            "step.security",
            ToolInvocationAuthorization.ComputeInputDigest(new Dictionary<string, object?>()),
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
            runner.ValidatePlan(plan, TestAuthorizationScopeId),
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
            runner.ValidatePlan(plan, TestAuthorizationScopeId),
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
        ITool? tool = null,
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

    private static ToolRegistration RegistrationWithDescriptor(ToolDescriptor descriptor) =>
        new(
            descriptor,
            new CountingTool(),
            Security(
                ToolEffect.ReadOnly,
                ToolRetrySafety.Idempotent,
                [],
                []));

    private static ToolRegistration ApprovalRegistration(string toolId, ITool tool) =>
        new(
            new ToolDescriptor(
                toolId,
                toolId,
                ToolKind.Query,
                ToolEffect.ReadOnly,
                RequiresApproval: true,
                RetrySafety: ToolRetrySafety.Idempotent),
            tool,
            new ToolSecurityDeclaration(
                ToolEffect.ReadOnly,
                [],
                [],
                ToolExternalOutputClassification.None,
                ToolApprovalRequirement.ExplicitGrant,
                ToolRetrySafety.Idempotent,
                BuiltInProvenance()));

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
        DateTimeOffset? expiresAt = null,
        IReadOnlyDictionary<string, object?>? input = null,
        string stepId = "step.security",
        string authorizationScopeId = TestAuthorizationScopeId,
        int attemptNumber = 1,
        string? grantId = null) =>
        new(
            grantId ?? AgenticaIds.New("grant"),
            authorizationScopeId,
            attemptNumber,
            stepId,
            ToolInvocationAuthorization.ComputeInputDigest(input ?? new Dictionary<string, object?>()),
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

    private sealed class UnavailableThenSuccessTool : ITool
    {
        public int Calls { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ToolResult(new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                Calls == 1 ? ReceiptStatus.Unavailable : ReceiptStatus.Succeeded,
                Calls == 1 ? "temporarily unavailable" : "ok",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>())));
        }
    }

    private sealed class SourceIdentityTool(string sourceReceiptId) : ITool
    {
        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            var receipt = new Receipt(
                sourceReceiptId,
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Succeeded,
                "source identity",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>());
            var observation = new Observation(
                "provider-observation-source-id",
                invocation.StepId,
                ObservationKind.ToolResult,
                "provider identity",
                new Dictionary<string, object?> { ["receiptId"] = sourceReceiptId },
                []);
            return Task.FromResult(new ToolResult(receipt, observation));
        }
    }

    private sealed class CapturingInputTool(string key) : ITool
    {
        public string? ObservedValue { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ObservedValue = Convert.ToString(invocation.Input[key]);
            return Task.FromResult(new ToolResult(new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Succeeded,
                "captured",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>())));
        }
    }

    private sealed class YieldingCountingTool : ITool
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            return new ToolResult(new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Succeeded,
                "ok",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class AliasRefinementPlanner : IWorkflowPlanner
    {
        public string? CanonicalReceiptId { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "plan.alias.initial",
                1,
                [new PlanStep(
                    "step.alias-source",
                    "local.alias-source",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?>())],
                "Produce an aliased provider identity."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            CanonicalReceiptId = Convert.ToString(observation.Data["receiptId"])
                ?? throw new InvalidOperationException("Canonical receipt identity is required.");
            return Task.FromResult(new WorkflowPlan(
                "plan.alias.refined",
                2,
                [new PlanStep(
                    "step.alias-target",
                    "sensitive.alias-target",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?> { ["receiptId"] = CanonicalReceiptId })
                {
                    DependsOn = ["step.alias-source"]
                }],
                "Use the canonical identity in an approved provider follow-up."));
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

    private sealed class DelayUntilEventSink(string eventType, DateTimeOffset releaseAt) : IEventSink
    {
        public bool Delayed { get; private set; }

        public void Emit(ExecutionEvent executionEvent)
        {
            if (!string.Equals(executionEvent.Type, eventType, StringComparison.Ordinal))
            {
                return;
            }

            Delayed = true;
            var remaining = releaseAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                Thread.Sleep(remaining);
            }
        }
    }

    private sealed class DishonestReadOnlyList<T>(
        int reportedCount,
        int yieldedCount,
        Func<int, T> itemFactory) : IReadOnlyList<T>
    {
        public int Count => reportedCount;

        public int EnumerationCount { get; private set; }

        public T this[int index] => itemFactory(index);

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < yieldedCount; index++)
            {
                EnumerationCount++;
                yield return itemFactory(index);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class MisreportedCountDictionary(int actualEntryCount)
        : IReadOnlyDictionary<string, object?>
    {
        public int EnumeratedEntries { get; private set; }

        public int Count => 0;

        public IEnumerable<string> Keys => this.Select(pair => pair.Key);

        public IEnumerable<object?> Values => this.Select(pair => pair.Value);

        public object? this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out object? value)
        {
            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; index < actualEntryCount; index++)
            {
                EnumeratedEntries++;
                yield return new KeyValuePair<string, object?>($"key-{index:D5}", index);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

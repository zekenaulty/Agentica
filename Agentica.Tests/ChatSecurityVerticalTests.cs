extern alias AgenticaLab;

using Agentica.Artifacts;
using Agentica.Clients.Images;
using Agentica.Clients.Llm;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;
using Microsoft.Data.Sqlite;
using LabChatArtifactKinds = AgenticaLab::ChatArtifactKinds;
using LabChatPersona = AgenticaLab::ChatPersona;
using LabChatStore = AgenticaLab::ChatStore;
using LabChatToolDependencies = AgenticaLab::ChatToolDependencies;
using LabChatToolIds = AgenticaLab::ChatToolIds;
using LabChatTools = AgenticaLab::ChatTools;

namespace Agentica.Tests;

public sealed class ChatSecurityVerticalTests
{
    [Fact]
    public async Task LocalOnlyRejectsImageGenerationBeforeProviderDispatch()
    {
        using var fixture = new ChatFixture();
        var catalog = fixture.CreateCatalog();
        var envelope = await RunAsync(
            new StaticPlanner(ImagePlan()),
            catalog,
            new ExecutionPolicy(
                PlanningMode: PlanningMode.PlanOnly,
                SecurityPolicy: LocalChatPolicy()),
            EvidenceCompletionEvaluator.ForArtifactKind(LabChatArtifactKinds.WorkspaceImage));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_not_allowed");
        Assert.Equal(0, fixture.ImageClient.CallCount);
        Assert.Empty(envelope.Receipts.Items);
    }

    [Fact]
    public async Task ExternalTransmissionRequiresAnExactExplicitGrant()
    {
        using var fixture = new ChatFixture();
        var catalog = fixture.CreateCatalog();
        var noGrant = await RunAsync(
            new StaticPlanner(ImagePlan()),
            catalog,
            ExternalToolPolicy(LocalChatPolicy()),
            EvidenceCompletionEvaluator.ForArtifactKind(LabChatArtifactKinds.WorkspaceImage));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, noGrant.Outcome.Status);
        Assert.Contains(noGrant.Details.ValidationIssues, issue => issue.Code == "tool.security.grant_required");
        Assert.Equal(0, fixture.ImageClient.CallCount);

        var grant = ImageGrant(catalog.ManifestHash);
        var authorized = await RunAsync(
            new StaticPlanner(ImagePlan()),
            catalog,
            ExternalToolPolicy(LocalChatPolicy(grant)),
            EvidenceCompletionEvaluator.ForArtifactKind(LabChatArtifactKinds.WorkspaceImage));

        Assert.Equal(RunOutcomeStatus.Succeeded, authorized.Outcome.Status);
        Assert.Equal(1, fixture.ImageClient.CallCount);
        Assert.Single(authorized.Details.Artifacts, artifact => artifact.Kind == LabChatArtifactKinds.WorkspaceImage);
        var request = Assert.Single(fixture.ImageClient.Requests);
        Assert.Null(request.Metadata);
    }

    [Fact]
    public async Task WorkspaceContentCannotFlowToAnExternalPlannerWithoutBoundaryApproval()
    {
        using var fixture = new ChatFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "secret.txt"), "workspace-secret-value");
        var planner = new ExternalStaticPlanner(FileReadPlan());
        var policy = new ToolSecurityPolicy(
            InitialBoundaries:
            [
                ToolDataBoundary.UserContent,
                ToolDataBoundary.ConversationContent,
                ToolDataBoundary.HostState
            ],
            ExternalPlannerAllowedBoundaries:
            [
                ToolDataBoundary.UserContent,
                ToolDataBoundary.ConversationContent,
                ToolDataBoundary.HostState,
                ToolDataBoundary.ExternalUntrusted
            ]);

        var envelope = await RunAsync(
            planner,
            fixture.CreateCatalog(),
            new ExecutionPolicy(PlanningMode: PlanningMode.PlanOnly, SecurityPolicy: policy),
            PlanExhaustionCompletionEvaluator.Instance);

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "plan.step.planner_boundary_not_allowed");
        Assert.Equal(1, planner.CreateCalls);
        Assert.Equal(0, planner.RefineCalls);
        Assert.Empty(envelope.Receipts.Items);
        Assert.Equal(0, fixture.ImageClient.CallCount);
    }

    [Fact]
    public async Task PlannedWorkspaceContentBlocksAProviderGrantThatDoesNotCoverIt()
    {
        using var fixture = new ChatFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "secret.txt"), "workspace-secret-value");
        var catalog = fixture.CreateCatalog();
        var envelope = await RunAsync(
            new StaticPlanner(FileThenImagePlan()),
            catalog,
            ExternalToolPolicy(LocalChatPolicy(ImageGrant(catalog.ManifestHash))),
            EvidenceCompletionEvaluator.ForArtifactKind(LabChatArtifactKinds.WorkspaceImage));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.StepId == "step_image" && issue.Code == "tool.security.grant_required");
        Assert.Empty(envelope.Receipts.Items);
        Assert.Equal(0, fixture.ImageClient.CallCount);
    }

    [Fact]
    public async Task ChangedChatRegistrationFailsClosedImmediatelyBeforeDispatch()
    {
        using var fixture = new ChatFixture();
        var registrations = fixture.CreateRegistrations();
        var imageRegistration = Assert.Single(
            registrations,
            registration => registration.Descriptor.ToolId == LabChatToolIds.WorkspaceImageGenerate);
        var fields = Assert.IsType<ToolInputField[]>(imageRegistration.Descriptor.InputSchema!.Fields);
        var catalog = ToolCatalog.Create(registrations);
        var eventSink = new CallbackEventSink(executionEvent =>
        {
            if (executionEvent.Type == "step.started")
            {
                fields[0] = fields[0] with { Description = "changed after planning" };
            }
        });
        var envelope = await RunAsync(
            new StaticPlanner(ImagePlan()),
            catalog,
            ExternalToolPolicy(LocalChatPolicy(ImageGrant(catalog.ManifestHash))),
            EvidenceCompletionEvaluator.ForArtifactKind(LabChatArtifactKinds.WorkspaceImage),
            eventSink);

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal(ReceiptStatus.Refused, receipt.Status);
        Assert.Equal("tool.security.manifest_changed", receipt.Data["securityCode"]);
        Assert.Equal(0, fixture.ImageClient.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
    }

    private static Task<OutcomeEnvelope> RunAsync(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ExecutionPolicy policy,
        ICompletionEvaluator completionEvaluator,
        IEventSink? eventSink = null) =>
        new AgenticaRunner(
                planner,
                catalog,
                eventSink ?? new InMemoryEventSink(),
                new DeterministicOutcomeReporter(),
                policy,
                completionEvaluator)
            .RunAsync(new RunRequest("Exercise the Chat security boundary.", RequestOrigin.User));

    private static ExecutionPolicy ExternalToolPolicy(ToolSecurityPolicy securityPolicy) =>
        new(
            MaxSteps: 4,
            MaxRefinements: 0,
            PlanningMode: PlanningMode.PlanOnly,
            EffectPolicy: ToolEffectPolicy.AllowKnown,
            SecurityPolicy: securityPolicy);

    private static ToolSecurityPolicy LocalChatPolicy(params ToolExecutionGrant[] grants) =>
        new(
            InitialBoundaries:
            [
                ToolDataBoundary.UserContent,
                ToolDataBoundary.ConversationContent
            ],
            ExecutionGrants: grants);

    private static ToolExecutionGrant ImageGrant(string manifestHash) =>
        new(
            manifestHash,
            LabChatToolIds.WorkspaceImageGenerate,
            [
                ToolDataBoundary.Public,
                ToolDataBoundary.HostState,
                ToolDataBoundary.UserContent,
                ToolDataBoundary.ConversationContent,
                ToolDataBoundary.ExternalUntrusted
            ],
            [ToolExternalOutputClassification.Mixed],
            DateTimeOffset.UtcNow.AddMinutes(5),
            "Agentica.Tests");

    private static WorkflowPlan ImagePlan() =>
        new(
            "plan_chat_image",
            1,
            [
                new PlanStep(
                    "step_image",
                    LabChatToolIds.WorkspaceImageGenerate,
                    ToolKind.Action,
                    ToolEffect.ExternalSideEffect,
                    new Dictionary<string, object?> { ["prompt"] = "Draw a bounded test image." })
            ],
            "Generate one image.");

    private static WorkflowPlan FileReadPlan() =>
        new(
            "plan_chat_file",
            1,
            [
                new PlanStep(
                    "step_file",
                    LabChatToolIds.WorkspaceFileRead,
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?> { ["path"] = "secret.txt" })
            ],
            "Read one workspace file.");

    private static WorkflowPlan FileThenImagePlan() =>
        new(
            "plan_chat_file_image",
            1,
            [
                new PlanStep(
                    "step_file",
                    LabChatToolIds.WorkspaceFileRead,
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new Dictionary<string, object?> { ["path"] = "secret.txt" }),
                new PlanStep(
                    "step_image",
                    LabChatToolIds.WorkspaceImageGenerate,
                    ToolKind.Action,
                    ToolEffect.ExternalSideEffect,
                    new Dictionary<string, object?> { ["prompt"] = "Do not transmit prior workspace content." })
                {
                    DependsOn = ["step_file"]
                }
            ],
            "Read locally, then exercise provider authorization.");

    private sealed class ChatFixture : IDisposable
    {
        public ChatFixture()
        {
            WorkspaceRoot = Path.Combine(
                Path.GetTempPath(),
                $"agentica-chat-security-{Guid.NewGuid():N}");
            Directory.CreateDirectory(WorkspaceRoot);
            Store = new LabChatStore(Path.Combine(WorkspaceRoot, "chat.db"));
            Store.EnsureCreated();
            Conversation = Store.CreateConversation("Security test", "plain", WorkspaceRoot);
            Persona = new LabChatPersona("plain", "Plain", "Be concise.", "Plain");
            ImageClient = new RecordingImageClient();
            Dependencies = new LabChatToolDependencies(new StubLlmClient(), ImageClient);
        }

        public string WorkspaceRoot { get; }

        public LabChatStore Store { get; }

        public AgenticaLab::ChatConversation Conversation { get; }

        public LabChatPersona Persona { get; }

        public RecordingImageClient ImageClient { get; }

        public LabChatToolDependencies Dependencies { get; }

        public ToolCatalog CreateCatalog() =>
            LabChatTools.CreateCatalog(Store, Conversation, Persona, WorkspaceRoot, Dependencies);

        public ToolRegistration[] CreateRegistrations() =>
            LabChatTools.CreateRegistrations(Store, Conversation, Persona, WorkspaceRoot, Dependencies);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(WorkspaceRoot))
            {
                Directory.Delete(WorkspaceRoot, recursive: true);
            }
        }
    }

    private sealed class RecordingImageClient : IImageGenerationClient
    {
        public int CallCount { get; private set; }

        public List<ImageGenerationRequest> Requests { get; } = [];

        public Task<ImageGenerationResponse> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(new ImageGenerationResponse(
                "fake",
                request.ModelId,
                [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")]));
        }
    }

    private sealed class StubLlmClient : ILlmClient
    {
        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmResponse("fake", request.ModelId, "{}", StructuredJson: "{}"));
    }

    private class StaticPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public int CreateCalls { get; private set; }

        public int RefineCalls { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(plan);
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Agentica.Observations.Observation observation,
            CancellationToken cancellationToken = default)
        {
            RefineCalls++;
            return Task.FromResult(plan);
        }
    }

    private sealed class ExternalStaticPlanner(WorkflowPlan plan) : StaticPlanner(plan), IExternalWorkflowPlanner;

    private sealed class CallbackEventSink(Action<ExecutionEvent> callback) : IEventSink
    {
        public void Emit(ExecutionEvent executionEvent) => callback(executionEvent);
    }
}

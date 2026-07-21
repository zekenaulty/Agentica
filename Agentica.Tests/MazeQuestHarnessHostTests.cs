using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Lab.Scenarios.MazeQuest;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class MazeQuestHarnessHostTests
{
    [Fact]
    public void MazeQuest_active_surface_context_is_public_safe_and_richer_than_tool_descriptors()
    {
        var stage = CreateStage();
        var state = MazeQuestRunState.Create(stage);

        var context = MazeQuestCapabilitySurfaceCompiler.BuildPlannerContext(
            stage,
            state,
            stage.Quest.Objective);

        var harness = Assert.IsType<MazeQuestHarnessContext>(context[MazeQuestCapabilitySurfaceCompiler.ContextKey]);
        Assert.Equal("mazequest.harness", harness.ActiveCapabilitySurface.ManifestId);
        Assert.Equal(harness.ActiveCapabilitySurface.SurfaceId, harness.ContextSurfaceReceipt.SurfaceId);
        Assert.Equal(harness.ActiveCapabilitySurface.SurfaceHash, harness.ContextSurfaceReceipt.SurfaceHash);
        Assert.Contains(harness.ActiveCapabilitySurface.Bindings, binding => binding.State == MazeQuestCapabilityBindingState.Preferred);
        Assert.Contains(harness.ActiveCapabilitySurface.Bindings, binding => binding.State == MazeQuestCapabilityBindingState.Blocked);
        Assert.Contains(harness.ActiveCapabilitySurface.Bindings, binding => binding.State == MazeQuestCapabilityBindingState.Demoted);
        Assert.Contains(harness.ActiveCapabilitySurface.Bindings, binding => binding.State == MazeQuestCapabilityBindingState.Hidden);
        Assert.Contains(harness.ActiveCapabilitySurface.Bindings, binding => binding.State == MazeQuestCapabilityBindingState.Unavailable);
        Assert.Contains(harness.ContextSurfaceReceipt.ExposedToolIds, toolId => toolId == MazeQuestToolIds.EvaluateMoves);
        Assert.Contains(harness.ContextSurfaceReceipt.ExposedToolIds, toolId => toolId == MazeQuestToolIds.AnalyzeProgress);
        Assert.Contains(harness.ContextSurfaceReceipt.ExposedToolIds, toolId => toolId == MazeQuestToolIds.EvaluateEscapeMoves);
        Assert.Contains(harness.ContextSurfaceReceipt.ExposedToolIds, toolId => toolId == MazeQuestToolIds.MoveTo);
        Assert.Contains(harness.ContextSurfaceReceipt.AntiLeakRules, rule => rule.Contains("unrevealed", StringComparison.OrdinalIgnoreCase));
        Assert.True(harness.ActiveCapabilitySurface.PublicStateSummary.ContainsKey("knownTravelOptions"));

        var json = JsonSerializer.Serialize(context, JsonOptions());
        Assert.DoesNotContain("Developer Reveal", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ShortestPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("perfect route", json, StringComparison.OrdinalIgnoreCase);

        foreach (var questObject in stage.Objects.Values.Where(item => !state.Discovered.Contains(item.Point)))
        {
            Assert.DoesNotContain(PointJson(questObject.Point), json, StringComparison.Ordinal);
            Assert.DoesNotContain($"({questObject.Point.X},{questObject.Point.Y})", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MazeQuest_run_links_host_surface_context_to_agentica_tool_surface()
    {
        var stage = CreateStage();
        var session = new MazeQuestSession(stage);
        var initialState = session.CurrentRunState;
        var context = MazeQuestCapabilitySurfaceCompiler.BuildPlannerContext(
            stage,
            initialState,
            stage.Quest.Objective);
        var events = new InMemoryEventSink();
        var runner = new AgenticaRunner(
            new MazeQuestDeterministicPlanner(session),
            MazeQuestTools.CreateCatalog(session),
            events,
            new MazeQuestOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 240,
                MaxRefinements: 240,
                PlanningMode: PlanningMode.Stepwise,
                MaxPlanContinuations: 16,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 8, MaxRecentReceipts: 8)),
            EvidenceCompletionEvaluator.ForArtifactKind("mazequest.objective_completed"),
            planningFrameProjector: new MazeQuestCockpitFrameProjector(session));

        var envelope = await runner.RunAsync(new RunRequest(
            stage.Quest.Objective,
            RequestOrigin.User,
            context));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var harness = Assert.IsType<MazeQuestHarnessContext>(
            envelope.Details.Request.Context![MazeQuestCapabilitySurfaceCompiler.ContextKey]);
        Assert.NotEmpty(envelope.Details.ToolSurfaces);
        var initialToolSurface = envelope.Details.ToolSurfaces[0];

        Assert.NotEqual(harness.ActiveCapabilitySurface.SurfaceId, initialToolSurface.SurfaceId);
        Assert.All(
            harness.ContextSurfaceReceipt.ExposedToolIds,
            toolId => Assert.Contains(initialToolSurface.ToolDescriptors, descriptor => descriptor.ToolId == toolId));
        Assert.Contains(
            envelope.Details.Events,
            executionEvent => executionEvent.Context?.ToolSurfaceId == initialToolSurface.SurfaceId);
        Assert.NotEmpty(envelope.Details.PlanningFrames);
        var cockpitFrame = Assert.Single(envelope.Details.PlanningFrames.Take(1));
        Assert.Equal("mazequest.cockpit", cockpitFrame.Kind);
        Assert.Equal(initialToolSurface.SurfaceId, cockpitFrame.ToolSurfaceId);
        Assert.True(cockpitFrame.Payload.ContainsKey("activeCapabilitySurface"));
        Assert.True(cockpitFrame.Payload.ContainsKey("contextSurfaceReceipt"));
        Assert.Contains(
            envelope.Details.Events,
            executionEvent =>
                executionEvent.Payload.TryGetValue("contextFrameIds", out var value) &&
                value is IReadOnlyList<string> frameIds &&
                value is not string[] &&
                frameIds.Contains(cockpitFrame.FrameId, StringComparer.Ordinal));

        var observationWithSurface = Assert.Single(
            envelope.Details.Observations.Take(1),
            observation => observation.Data.ContainsKey("agenticHarness"));
        var observationHarness = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            observationWithSurface.Data["agenticHarness"]);
        Assert.IsNotType<MazeQuestHarnessContext>(observationHarness);
        var observationSurface = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            observationHarness["ActiveCapabilitySurface"]);
        Assert.Equal("mazequest.harness", observationSurface["ManifestId"]);
        Assert.NotEqual(harness.ActiveCapabilitySurface.SurfaceId, observationSurface["SurfaceId"]);
    }

    [Fact]
    public void MazeQuest_cockpit_frame_detects_public_two_cell_cycle_and_escape_candidates()
    {
        var stage = CreateStage();
        var session = new MazeQuestSession(stage);
        session.State.Energy = 100;

        foreach (var direction in new[]
        {
            "east", "east", "south", "south", "east", "east", "south", "south", "south", "south",
            "west", "west", "south", "south", "west", "west", "north", "north", "north", "north",
            "east", "east", "west", "east", "west", "east", "west", "east", "west", "east", "west"
        })
        {
            var result = session.Execute(new ToolInvocation(
                "run_test",
                $"step_{session.Turns.Count + 1:000}",
                MazeQuestToolIds.Move,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["direction"] = direction
                }));
            Assert.True(result.Receipt.Status == Agentica.Artifacts.ReceiptStatus.Succeeded, result.Receipt.Message);
        }

        var frame = MazeQuestCockpitFrameCompiler.BuildFrame(stage, session.CurrentRunState, session.Turns);

        Assert.True(frame.LoopSignals.StagnationSuspected);
        Assert.Equal("two_cell_cycle", frame.LoopSignals.CycleType);
        Assert.True(frame.RecentTrajectory.RepeatedMoveCount >= 4);
        Assert.Contains(frame.EscapeCandidateMoves, move => move.ProgressClass == "looping");
        Assert.Contains(frame.EscapeCandidateMoves, move => move.WouldBreakLoop);
        Assert.Contains(frame.PlannerGuidance, item => item.Contains("stagnation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MazeQuest_progress_tools_return_public_cockpit_data()
    {
        var session = new MazeQuestSession(CreateStage());

        var progress = session.Execute(new ToolInvocation(
            "run_test",
            "step_001",
            MazeQuestToolIds.AnalyzeProgress,
            new Dictionary<string, object?>(StringComparer.Ordinal)));
        var escape = session.Execute(new ToolInvocation(
            "run_test",
            "step_002",
            MazeQuestToolIds.EvaluateEscapeMoves,
            new Dictionary<string, object?>(StringComparer.Ordinal)));

        Assert.True(progress.Receipt.Data.ContainsKey("cockpitFrame"));
        Assert.True(progress.Receipt.Data.ContainsKey("trajectorySummary"));
        Assert.True(progress.Receipt.Data.ContainsKey("loopSignals"));
        Assert.True(escape.Receipt.Data.ContainsKey("escapeCandidateMoves"));
        Assert.True(escape.Receipt.Data.ContainsKey("recommendedPlannerPosture"));
    }

    [Fact]
    public void MazeQuest_user_facing_reason_projector_uses_domain_copy()
    {
        var reason = MazeQuestUserFacingReasonProjector.Instance.Project(new UserFacingReasonProjectionRequest(
            EventType: "step.started",
            Source: "Runner",
            Context: new ExecutionEventContext(
                RunId: "run_test",
                AttemptNumber: 1,
                StepId: "step_001",
                ToolId: MazeQuestToolIds.MoveTo),
            Intent: new ExecutionIntent(
                "Move through the exposed route.",
                "The public knownTravelOptions show a fully exposed route to the requested destination."),
            Data: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["step"] = "step_001",
                ["tool"] = MazeQuestToolIds.MoveTo
            },
            Payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = ToolKind.Action.ToString(),
                ["effect"] = ToolEffect.WritesLocalState.ToString()
            },
            Diagnostics: null));

        Assert.NotNull(reason);
        Assert.Equal("Following a known route.", reason!.Summary);
        Assert.Equal("mazequest.host", reason.ProjectionSource);
        Assert.DoesNotContain("knownTravelOptions", reason.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MazeQuest_move_to_moves_across_fully_exposed_public_path()
    {
        var session = new MazeQuestSession(CreateStage());
        var option = MazeQuestAnalyzer.KnownTravelOptions(session.Stage, session.CurrentRunState)
            .FirstOrDefault(item => item.HopCount >= 2);
        Assert.NotNull(option);

        var result = session.Execute(new ToolInvocation(
            "run_test",
            "step_001",
            MazeQuestToolIds.MoveTo,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = option!.Destination.X,
                ["y"] = option.Destination.Y
            }));

        Assert.Equal(Agentica.Artifacts.ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(option.Destination, session.State.Position);
        Assert.Equal(option.HopCount, session.State.StepCount);
        Assert.Single(session.Turns);
        Assert.Equal(MazeQuestToolIds.MoveTo, session.LastTurn!.Invocation.ToolId);
        Assert.Equal(option.HopCount, result.Receipt.Data["hopCount"]);
        var hops = Assert.IsAssignableFrom<IReadOnlyList<MazeKnownTravelHop>>(result.Receipt.Data["hops"]);
        Assert.Equal(option.HopCount, hops.Count);

        var frame = MazeQuestCockpitFrameCompiler.BuildFrame(session.Stage, session.CurrentRunState, session.Turns);
        Assert.Equal(option.HopCount, frame.RecentTrajectory.RecentMoveLog.Count);
        Assert.Equal(option.Destination, frame.RecentTrajectory.RecentMoveLog[^1].To);
    }

    [Fact]
    public void MazeQuest_move_to_refuses_hidden_destination_without_mutating_state()
    {
        var stage = CreateStage();
        var session = new MazeQuestSession(stage);
        var before = session.CurrentRunState;
        var hiddenDestination = stage.Grid.AllCells()
            .Select(cell => cell.Point)
            .First(point => !before.Discovered.Contains(point));

        var result = session.Execute(new ToolInvocation(
            "run_test",
            "step_001",
            MazeQuestToolIds.MoveTo,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = hiddenDestination.X,
                ["y"] = hiddenDestination.Y
            }));

        Assert.Equal(Agentica.Artifacts.ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("target_not_discovered", result.Receipt.Data["reason"]);
        Assert.Equal(before.Position, session.State.Position);
        Assert.Equal(before.StepCount, session.State.StepCount);
        Assert.Equal(before.Energy, session.State.Energy);
        Assert.Equal(before.Health, session.State.Health);
    }

    [Fact]
    public void MazeQuest_query_tools_describe_context_expansion_relationships()
    {
        var session = new MazeQuestSession(CreateStage());
        var descriptors = MazeQuestTools.CreateCatalog(session).Descriptors
            .Where(descriptor => descriptor.Kind == ToolKind.Query && descriptor.Effect == ToolEffect.ReadOnly)
            .ToArray();

        Assert.NotEmpty(descriptors);
        Assert.All(descriptors, descriptor => Assert.NotNull(descriptor.ContextHint));

        var state = Assert.Single(descriptors, descriptor => descriptor.ToolId == MazeQuestToolIds.GetState);
        Assert.Contains(MazeQuestToolIds.EvaluateMoves, state.ContextHint!.CanBatchWith);
        Assert.Contains(MazeQuestToolIds.Move, state.ContextHint.ShouldPrecede);

        var progress = Assert.Single(descriptors, descriptor => descriptor.ToolId == MazeQuestToolIds.AnalyzeProgress);
        Assert.Contains(MazeQuestToolIds.EvaluateEscapeMoves, progress.ContextHint!.Complements);
        Assert.Contains(MazeQuestToolIds.EvaluateEscapeMoves, progress.ContextHint.CanBatchWith);

        var moveTo = Assert.Single(MazeQuestTools.CreateCatalog(session).Descriptors, descriptor => descriptor.ToolId == MazeQuestToolIds.MoveTo);
        Assert.Equal(ToolKind.Action, moveTo.Kind);
        Assert.Equal(ToolEffect.WritesLocalState, moveTo.Effect);
        Assert.Contains(moveTo.InputSchema!.Fields, field => field.Name == "x" && field.Type == ToolInputValueType.Integer);
        Assert.Contains(moveTo.InputSchema.Fields, field => field.Name == "y" && field.Type == ToolInputValueType.Integer);
    }

    private static MazeQuestStage CreateStage()
    {
        var descriptor = new MazeQuestBoard().GetQuest("sun_gate_maze");
        return new MazeQuestGenerator().Generate(
            descriptor,
            new MazeQuestGenerationOptions(
                QuestId: descriptor.QuestId,
                Seed: descriptor.DefaultSeed));
    }

    private static string PointJson(MazePoint point) =>
        $"\"x\":{point.X},\"y\":{point.Y}";

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

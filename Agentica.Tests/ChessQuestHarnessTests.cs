using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.CLI.Scenarios.ChessQuest;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Orchestration;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class ChessQuestHarnessTests
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string FoolsMateBlackToMoveFen = "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2";

    [Fact]
    public void ChessQuest_strict_projected_surface_registers_only_referee_tools()
    {
        var session = CreateSession();
        var catalog = ChessQuestTools.CreateCatalog(session);

        Assert.Equal(
            [
                ChessQuestToolIds.GetState,
                ChessQuestToolIds.RenderBoard,
                ChessQuestToolIds.ListLegalMoves,
                ChessQuestToolIds.ProjectLine,
                ChessQuestToolIds.PlayMove,
                ChessQuestToolIds.CompleteObjective
            ],
            catalog.Descriptors.Select(descriptor => descriptor.ToolId));

        AssertDescriptor(catalog, ChessQuestToolIds.GetState, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, ChessQuestToolIds.RenderBoard, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, ChessQuestToolIds.ListLegalMoves, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, ChessQuestToolIds.ProjectLine, ToolKind.Query, ToolEffect.ReadOnly);
        AssertDescriptor(catalog, ChessQuestToolIds.PlayMove, ToolKind.Action, ToolEffect.WritesLocalState);
        AssertDescriptor(catalog, ChessQuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState);

        var descriptorText = JsonSerializer.Serialize(catalog.Descriptors, JsonOptions());
        foreach (var forbidden in new[]
        {
            "find_best_move",
            "best move",
            "engine score",
            "principal variation",
            "mate-in",
            "winning move",
            "recommended",
            "tactic",
            "blunder",
            "threat"
        })
        {
            Assert.DoesNotContain(forbidden, descriptorText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ChessQuest_active_surface_omits_hidden_solution_and_engine_tools()
    {
        var session = CreateSession(hiddenSolutionLine: ["e2e4", "e7e5", "d1h5"]);

        var context = ChessQuestCapabilitySurfaceCompiler.BuildHarnessContext(session);
        var json = JsonSerializer.Serialize(context, JsonOptions());

        Assert.DoesNotContain("d1h5", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("find_best_move", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("engine score", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("principal variation", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(context.ActiveCapabilitySurface.Bindings, binding => binding.ToolId == ChessQuestToolIds.ProjectLine);
        Assert.Contains(context.ContextSurfaceReceipt.ExposedToolIds, toolId => toolId == ChessQuestToolIds.ProjectLine);
    }

    [Fact]
    public async Task ChessQuest_project_line_is_readonly_and_does_not_generate_opponent_moves()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        var before = session.CurrentState.Fen;

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.ProjectLine,
            new Dictionary<string, object?>
            {
                ["line"] = new[] { "e2e4" },
                ["maxPlies"] = 4
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(before, session.CurrentState.Fen);
        var projection = Assert.IsType<ChessLineProjection>(result.Receipt.Data["projection"]);
        Assert.Equal(["e2e4"], projection.AcceptedPrefix);
        Assert.Null(projection.RejectedAt);
        Assert.True(projection.ReadOnly);
        Assert.True(projection.SessionFenUnchanged);
        Assert.Equal(ChessQuestColor.Black, projection.SideToMoveAfter);
        Assert.Contains("without generating any opponent reply", projection.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e7e5", projection.AcceptedPrefix);
    }

    [Fact]
    public async Task ChessQuest_project_line_returns_accepted_prefix_for_illegal_line()
    {
        var session = CreateSession();

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.ProjectLine,
            new Dictionary<string, object?>
            {
                ["line"] = new[] { "e2e4", "g1f5" },
                ["maxPlies"] = 4
            });

        var projection = Assert.IsType<ChessLineProjection>(result.Receipt.Data["projection"]);
        Assert.Equal(["e2e4"], projection.AcceptedPrefix);
        Assert.NotNull(projection.RejectedAt);
        Assert.Equal(1, projection.RejectedAt!.PlyOffset);
        Assert.Equal("g1f5", projection.RejectedAt.Move);
        Assert.Equal("illegal_move", projection.RejectedAt.Reason);
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_project_line_verifies_check_and_checkmate_claims_for_submitted_line()
    {
        const string exposedKingFen = "rnbq3r/1p1p1p2/2n2p2/p6p/5k2/8/PPP2PPP/4R1K1 w - - 2 21";
        var session = CreateSession(fen: exposedKingFen);

        var quietMove = await InvokeAsync(
            session,
            ChessQuestToolIds.ProjectLine,
            new Dictionary<string, object?>
            {
                ["line"] = new[] { "e1f1" },
                ["claims"] = new[] { "check", "checkmate" },
                ["maxPlies"] = 1
            });

        var quietProjection = Assert.IsType<ChessLineProjection>(quietMove.Receipt.Data["projection"]);
        Assert.False(quietProjection.LastAcceptedMoveGivesCheck);
        Assert.False(quietProjection.LastAcceptedMoveGivesCheckmate);
        Assert.False(quietProjection.ClaimVerification["check"]);
        Assert.False(quietProjection.ClaimVerification["checkmate"]);

        var checkingMove = await InvokeAsync(
            session,
            ChessQuestToolIds.ProjectLine,
            new Dictionary<string, object?>
            {
                ["line"] = new[] { "e1e4" },
                ["claims"] = new[] { "check", "checkmate" },
                ["maxPlies"] = 1
            });

        var checkingProjection = Assert.IsType<ChessLineProjection>(checkingMove.Receipt.Data["projection"]);
        Assert.True(checkingProjection.LastAcceptedMoveGivesCheck);
        Assert.False(checkingProjection.LastAcceptedMoveGivesCheckmate);
        Assert.True(checkingProjection.ClaimVerification["check"]);
        Assert.False(checkingProjection.ClaimVerification["checkmate"]);
    }

    [Fact]
    public async Task ChessQuest_project_line_verifies_submitted_checkmate_claim()
    {
        var session = CreateSession(
            fen: FoolsMateBlackToMoveFen,
            agentColor: ChessQuestColor.Black,
            opponentMoves: []);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.ProjectLine,
            new Dictionary<string, object?>
            {
                ["line"] = new[] { "d8h4" },
                ["claims"] = new[] { "check", "checkmate" },
                ["maxPlies"] = 1
            });

        var projection = Assert.IsType<ChessLineProjection>(result.Receipt.Data["projection"]);
        Assert.True(projection.LastAcceptedMoveGivesCheck);
        Assert.True(projection.LastAcceptedMoveGivesCheckmate);
        Assert.True(projection.ClaimVerification["check"]);
        Assert.True(projection.ClaimVerification["checkmate"]);
        Assert.True(projection.Terminal);
        Assert.Equal(ChessQuestColor.Black, projection.TerminalState?.Winner);
    }

    [Fact]
    public async Task ChessQuest_play_move_requires_public_turn_intent()
    {
        var session = CreateSession();

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4"
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("missing_turn_intent", result.Receipt.Data["reason"]);
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_play_move_applies_agent_move_then_engine_backed_opponent()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("e2e4", result.Receipt.Data["agentMove"]);
        Assert.Equal(true, result.Receipt.Data["opponentMoveApplied"]);
        Assert.Equal("e7e5", result.Receipt.Data["opponentMove"]);
        Assert.Equal(true, result.Receipt.Data["agentToMove"]);
        Assert.Equal(ChessQuestColor.White, session.CurrentState.SideToMove);
        Assert.Equal(["e2e4", "e7e5"], session.CurrentState.RecentMovesUci);
    }

    [Fact]
    public async Task ChessQuest_game_record_replays_committed_plies()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });

        var record = ChessQuestGameRecordStore.FromSession(session);
        Assert.Equal(["e2e4", "e7e5"], record.Plies.Select(ply => ply.Move));

        var replayed = CreateSession();
        ChessQuestGameRecordStore.ReplayIntoSession(replayed, record);

        Assert.Equal(session.CurrentState.Fen, replayed.CurrentState.Fen);
        Assert.Equal(session.CurrentState.Ply, replayed.CurrentState.Ply);
        Assert.Equal(["e2e4", "e7e5"], replayed.CommittedPlies.Select(ply => ply.Move));
    }

    [Fact]
    public async Task ChessQuest_game_record_store_writes_and_loads_resume_record()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentica-chessquest-{Guid.NewGuid():N}");
        var session = CreateSession(opponentMoves: ["e7e5"]);
        await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });

        try
        {
            var path = ChessQuestGameRecordStore.WriteDirectory(directory, session);
            var loaded = ChessQuestGameRecordStore.Load(directory);

            Assert.True(File.Exists(path));
            Assert.Equal(session.CurrentState.Fen, loaded.CurrentFen);
            Assert.Equal(["e2e4", "e7e5"], loaded.Plies.Select(ply => ply.Move));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ChessQuest_game_record_store_can_replay_legacy_turn_log_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentica-chessquest-legacy-{Guid.NewGuid():N}");
        var session = CreateSession(opponentMoves: ["e7e5"]);
        await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });

        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "chessquest-scenario.json"),
                JsonSerializer.Serialize(session.Scenario, JsonOptions()));
            var turn = new ChessQuestCockpitTurnEnvelope(
                TurnNumber: 1,
                StepId: "step_001",
                ReceiptId: "receipt_001",
                ReceiptStatus: ReceiptStatus.Succeeded,
                ReceiptMessage: "Agent move e2e4 accepted; opponent move applied.",
                AgentColor: ChessQuestColor.White,
                SideToMoveAfter: ChessQuestColor.White,
                PlyAfter: session.CurrentState.Ply,
                SelectedMove: "e2e4",
                OpponentMove: "e7e5",
                OpponentMoveApplied: true,
                Terminal: false,
                TerminalResult: null,
                FenAfter: session.CurrentState.Fen,
                PublicIntentAction: "Play e2e4 as White.",
                PublicIntentRationale: "Use a public legal move.",
                PublicIntentExpectedOutcome: "Continue the game.",
                TurnPublicReason: "Use a legal opening move.",
                LegalMoveCountBeforeMove: 20,
                CandidateLinesExplored: []);
            File.WriteAllText(
                Path.Combine(directory, "chessquest-turns.jsonl"),
                JsonSerializer.Serialize(turn, JsonOptions()) + Environment.NewLine);

            var loaded = ChessQuestGameRecordStore.Load(directory);

            Assert.Equal(session.CurrentState.Fen, loaded.CurrentFen);
            Assert.Equal(["e2e4", "e7e5"], loaded.Plies.Select(ply => ply.Move));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ChessQuest_phase_context_is_projected_into_planner_context()
    {
        var session = CreateSession();
        var phase = ChessQuestPhaseTracker.Create(session, "opening", maxAgentTurns: 3);

        var context = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session, phase);

        var strategyFrame = Assert.IsType<ChessQuestStrategyFrame>(context["strategyFrame"]);
        var objective = Assert.IsType<ChessQuestPhaseObjective>(context["phaseObjective"]);
        var progress = Assert.IsType<ChessQuestPhaseProgress>(context["phaseProgress"]);
        Assert.Equal("opening", strategyFrame.Phase);
        Assert.Equal("opening", objective.Phase);
        Assert.Equal(3, objective.MaxAgentTurns);
        Assert.Equal(0, progress.AgentTurnsPlayed);
        Assert.Equal(3, progress.AgentTurnsRemaining);
    }

    [Fact]
    public void ChessQuest_projected_strategy_context_is_projected_into_planner_context()
    {
        var session = CreateSession();
        var projection = ChessQuestPhaseTracker.DefaultProjection(
            session,
            "opening",
            source: "test_projection");
        var phase = ChessQuestPhaseTracker.Create(
            session,
            "opening",
            maxAgentTurns: 3,
            strategyProjection: projection);

        var context = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session, phase);

        var projected = Assert.IsType<ChessQuestStrategyProjection>(context["strategyProjection"]);
        var frame = Assert.IsType<ChessQuestStrategyFrame>(context["strategyFrame"]);
        Assert.Equal("test_projection", projected.Source);
        Assert.Equal(projected.StrategyName, frame.StrategyName);
        Assert.Equal(projected.ActiveObjectives, frame.ActiveObjectives);
    }

    [Fact]
    public void ChessQuest_strategy_projection_parser_enforces_verification_rules()
    {
        var projection = ChessQuestStrategyProjectionRunner.ParseProjection(
            """
            {
              "phase": "opening",
              "strategyName": "center development",
              "strategyIntent": "Develop pieces and contest the center.",
              "activeObjectives": ["develop a minor piece"],
              "stopTriggers": ["terminal game state"],
              "progressSignals": ["minor piece developed"],
              "verificationRules": ["legal move receipts override strategy claims"]
            }
            """,
            ChessQuestColor.White,
            "opening",
            "test");

        Assert.Equal("center development", projection.StrategyName);
        Assert.Contains(projection.VerificationRules, rule => rule.Contains("project_line", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projection.VerificationRules, rule => rule.Contains("receipt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChessQuest_phase_completion_stops_after_agent_turn_budget()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        var phase = ChessQuestPhaseTracker.Create(session, "opening", maxAgentTurns: 1);
        var events = new InMemoryEventSink();
        var runner = new AgenticaRunner(
            new SingleMovePlanner(),
            ChessQuestTools.CreateCatalog(session),
            events,
            new ChessQuestPhaseOutcomeReporter(session, phase),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 4,
                MaxPlanContinuations: 4,
                PlanningMode: PlanningMode.QueryAndBlockerDriven),
            completionEvaluator: new ChessQuestPhaseCompletionEvaluator(session, phase),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session, phase));

        var envelope = await runner.RunAsync(new RunRequest(
            "Execute one opening phase turn.",
            RequestOrigin.User,
            ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session, phase)));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(["e2e4", "e7e5"], session.CommittedPlies.Select(ply => ply.Move));
        Assert.Single(session.CommittedPlies, ply => ply.Source == "agent");
        var report = phase.BuildReport(session);
        Assert.Equal("budget_complete", report.Status);
        Assert.Equal("agent_turn_budget_exhausted", report.StopReason);
        Assert.Equal(["e2e4"], report.AgentMoves);
    }

    [Fact]
    public void ChessQuest_phase_task_envelope_projects_orchestration_context_into_strategy()
    {
        var session = CreateSession();
        var envelope = ChessQuestPhaseTaskEnvelope.FromContext(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chessquest.taskKind"] = "phase_run",
                ["chessquest.phase"] = "tactical",
                ["chessquest.taskDirection"] = "verify_tactical_opportunity",
                ["chessquest.publicRationale"] = "A public phase report suggests a forcing opportunity.",
                ["chessquest.phaseGoal"] = "Verify forcing ideas before committing a move.",
                ["chessquest.maxAgentTurns"] = 2,
                ["chessquest.replanTriggers"] = new[] { "terminal game state" }
            },
            defaultMaxAgentTurns: 4,
            taskNumber: 2);

        var projection = envelope.ToProjection(session);

        Assert.Equal("phase_run", envelope.TaskKind);
        Assert.Equal("tactical", envelope.Phase);
        Assert.Equal(2, envelope.MaxAgentTurns);
        Assert.Equal("verify_tactical_opportunity", projection.StrategyName);
        Assert.Equal("A public phase report suggests a forcing opportunity.", projection.StrategyIntent);
        Assert.Contains("Verify forcing ideas before committing a move.", projection.ActiveObjectives);
        Assert.Contains("terminal game state", projection.StopTriggers);
    }

    [Fact]
    public async Task ChessQuest_deterministic_task_planner_creates_phase_task_envelope()
    {
        var planner = new ChessQuestDeterministicTaskPlanner(maxAgentTurns: 3);

        var plan = await planner.CreatePlanAsync(new TaskPlanningRequest(
            new LargeTaskRequest(
                "Choose a ChessQuest phase task.",
                RequestOrigin.User,
                new Dictionary<string, object?>()),
            new OrchestrationPolicy()));

        var task = Assert.Single(plan.Tasks);
        Assert.Equal("chessquest_phase_001", task.TaskId);
        Assert.Equal("phase_run", task.ContextProjection["chessquest.taskKind"]);
        Assert.Equal("opening", task.ContextProjection["chessquest.phase"]);
        Assert.Equal(3, task.ContextProjection["chessquest.maxAgentTurns"]);
        Assert.Contains(task.AcceptanceRequirements, requirement =>
            requirement.Kind == TaskAcceptanceRequirementKind.Artifact &&
            requirement.ArtifactKind == "chessquest.phase_report");
    }

    [Fact]
    public void ChessQuest_board_probe_validates_piece_identification_against_fen_oracle()
    {
        var trial = ChessQuestBoardProbeRunner.CreateTrial(
            seed: 1147,
            trialNumber: 1,
            scramblePlies: 18,
            targetMode: ChessQuestBoardProbeTargetMode.Occupied);
        var expected = trial.Expected;

        var result = ChessQuestBoardProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestBoardProbeAnswer(
                expected.Square,
                expected.Occupied,
                expected.Color,
                expected.Piece)));

        Assert.True(result.Passed);
        Assert.True(expected.Occupied);
        Assert.NotEqual("empty", expected.Piece);
    }

    [Fact]
    public void ChessQuest_board_probe_flags_wrong_piece_claim()
    {
        var trial = ChessQuestBoardProbeRunner.CreateTrial(
            seed: 2219,
            trialNumber: 1,
            scramblePlies: 22,
            targetMode: ChessQuestBoardProbeTargetMode.Occupied);
        var wrongPiece = trial.Expected.Piece == "queen" ? "rook" : "queen";

        var result = ChessQuestBoardProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestBoardProbeAnswer(
                trial.Expected.Square,
                Occupied: true,
                trial.Expected.Color,
                wrongPiece)));

        Assert.False(result.Passed);
        Assert.Contains("piece expected", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_board_probe_ascii_prompt_does_not_include_fen()
    {
        var trial = ChessQuestBoardProbeRunner.CreateTrial(
            seed: 3331,
            trialNumber: 1,
            scramblePlies: 12,
            targetMode: ChessQuestBoardProbeTargetMode.Occupied);

        var prompt = ChessQuestBoardProbeRunner.BuildPrompt(
            trial,
            ChessQuestBoardProbePresentation.Ascii);

        Assert.Contains("ASCII board:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("FEN:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(trial.Fen, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChessQuest_board_probe_options_parse_logging_arguments()
    {
        var options = ChessQuestBoardProbeOptions.Parse(
            [
                "--trials", "2",
                "--log-run",
                "--log-dir", "probe-logs"
            ],
            "test-model");

        Assert.True(options.IsValid);
        Assert.True(options.LogRun);
        Assert.Equal("probe-logs", options.LogDir);
        Assert.Equal(2, options.Trials);
    }

    [Fact]
    public void ChessQuest_board_probe_disables_thinking_by_default()
    {
        var options = ChessQuestBoardProbeOptions.Parse([], "test-model");

        Assert.True(options.IsValid);
        Assert.Equal("off", options.ThinkingBudget);
    }

    [Fact]
    public async Task HeuristicChessOpponent_prefers_immediate_checkmate()
    {
        var rules = new GeraChessRulesEngine(FoolsMateBlackToMoveFen);
        var legalMoves = rules.ListLegalMoves().Select(move => move.Uci).ToArray();
        var opponent = new HeuristicChessOpponent(seed: 1, difficulty: "max");

        var chosen = await opponent.ChooseMoveAsync(
            new ChessOpponentRequest(
                FoolsMateBlackToMoveFen,
                ChessQuestColor.Black,
                "max",
                Ply: 3,
                Seed: 1,
                LegalMoves: legalMoves),
            CancellationToken.None);

        Assert.NotNull(chosen);
        Assert.Equal("d8h4", chosen!.Move);
        Assert.StartsWith("heuristic_", chosen.Policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlannerChessOpponent_uses_bounded_agent_slice_to_choose_legal_reply()
    {
        var rules = new GeraChessRulesEngine(FoolsMateBlackToMoveFen);
        var legalMoves = rules.ListLegalMoves().Select(move => move.Uci).ToArray();
        var opponent = new PlannerChessOpponent(
            plannerFactory: _ => new FixedMovePlanner("d8h4", ChessQuestColor.Black),
            eventSinkFactory: _ => new InMemoryEventSink(),
            options: new PlannerChessOpponentOptions(
                PlannerLabel: "test",
                TimeoutSeconds: 10,
                MaxSteps: 2,
                MaxRefinements: 0,
                MaxPlanContinuations: 0,
                Quiet: true),
            fallback: new RandomLegalMoveOpponent(seed: 1));

        var chosen = await opponent.ChooseMoveAsync(
            new ChessOpponentRequest(
                FoolsMateBlackToMoveFen,
                ChessQuestColor.Black,
                "agent-test",
                Ply: 3,
                Seed: 1,
                LegalMoves: legalMoves),
            CancellationToken.None);

        Assert.NotNull(chosen);
        Assert.Equal("d8h4", chosen!.Move);
        Assert.Equal("agent_test", chosen.Policy);
    }

    [Fact]
    public async Task ChessQuest_complete_objective_emits_artifact_only_after_agent_win()
    {
        var session = CreateSession(
            fen: FoolsMateBlackToMoveFen,
            agentColor: ChessQuestColor.Black,
            opponentMoves: []);

        var early = await InvokeAsync(session, ChessQuestToolIds.CompleteObjective, new Dictionary<string, object?>());
        Assert.Equal(ReceiptStatus.Refused, early.Receipt.Status);

        var move = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "d8h4",
                ["turnIntent"] = TurnIntent("black", "d8h4", "Use the legal move that ends the current game.")
            });
        Assert.Equal(ReceiptStatus.Succeeded, move.Receipt.Status);
        Assert.Equal(true, move.Receipt.Data["terminal"]);

        var complete = await InvokeAsync(session, ChessQuestToolIds.CompleteObjective, new Dictionary<string, object?>());
        Assert.Equal(ReceiptStatus.Succeeded, complete.Receipt.Status);
        Assert.NotNull(complete.Artifact);
        Assert.Equal("chessquest.objective_completed", complete.Artifact!.Kind);
    }

    [Fact]
    public async Task ChessQuest_runner_uses_planning_frame_and_public_execution_intent()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        var events = new InMemoryEventSink();
        var runner = new AgenticaRunner(
            new SingleMovePlanner(),
            ChessQuestTools.CreateCatalog(session),
            events,
            new ChessQuestOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 1, MaxRefinements: 0, MaxPlanContinuations: 0),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session));

        var envelope = await runner.RunAsync(new RunRequest(
            "Win the game as White. Draw is not success.",
            RequestOrigin.User,
            ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session)));

        Assert.Contains(envelope.Details.PlanningFrames, frame => frame.Kind == "chessquest.cockpit");
        var stepStarted = events.Events.First(item => item.Type == ExecutionEventType.StepStarted.WireName());
        Assert.NotNull(stepStarted.Intent);
        Assert.Equal("Play e2e4 as White.", stepStarted.Intent!.Action);
        Assert.Contains("public legal move", stepStarted.Intent.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    private static ChessQuestSession CreateSession(
        string fen = StartFen,
        ChessQuestColor agentColor = ChessQuestColor.White,
        IReadOnlyList<string>? opponentMoves = null,
        IReadOnlyList<string>? hiddenSolutionLine = null)
    {
        var scenario = new ChessQuestScenario(
            ScenarioId: "chessquest_test",
            Title: "ChessQuest Test",
            InitialFen: fen,
            AgentColor: agentColor,
            ObjectiveKind: ChessQuestObjectiveKind.WinGame,
            PublicObjective: $"Win the game as {agentColor}. Draw is not success.",
            Difficulty: new ChessQuestDifficulty(
                Scenario: "test",
                Surface: "strict_projected",
                Opponent: "scripted"),
            DisclosurePolicy: ChessQuestDisclosurePolicy.StrictRefereeProjected,
            HiddenSolutionLine: hiddenSolutionLine);

        return new ChessQuestSession(
            scenario,
            opponent: new ScriptedChessOpponent(opponentMoves ?? []));
    }

    private static Task<ToolResult> InvokeAsync(
        ChessQuestSession session,
        string toolId,
        IReadOnlyDictionary<string, object?> input) =>
        session.ExecuteAsync(new ToolInvocation("run_test", $"step_{session.Turns.Count + 1:000}", toolId, input));

    private static Dictionary<string, object?> TurnIntent(
        string agentColor,
        string selectedMove,
        string publicReason) =>
        new(StringComparer.Ordinal)
        {
            ["agentColor"] = agentColor,
            ["selectedMove"] = selectedMove,
            ["legalBasis"] = "selected_from_current_legal_move_list",
            ["publicReason"] = publicReason,
            ["completionClaim"] = false
        };

    private static void AssertDescriptor(
        ToolCatalog catalog,
        string toolId,
        ToolKind kind,
        ToolEffect effect)
    {
        var descriptor = catalog.Descriptors.Single(descriptor => descriptor.ToolId == toolId);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal(effect, descriptor.Effect);
    }

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

    private sealed class SingleMovePlanner : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "chessquest_single_move",
                1,
                [
                    new PlanStep(
                        "step_001",
                        ChessQuestToolIds.PlayMove,
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>
                        {
                            ["move"] = "e2e4",
                            ["turnIntent"] = TurnIntent("white", "e2e4", "Use a public legal move and keep playing for a win.")
                        })
                    {
                        Intent = new ExecutionIntent(
                            "Play e2e4 as White.",
                            "The current public legal move set allows e2e4 and it is White's turn.",
                            "Commit the move, receive the opponent reply, and return updated public board state.")
                    }
                ],
                "Commit one ChessQuest move."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class FixedMovePlanner : IWorkflowPlanner
    {
        private readonly string _move;
        private readonly ChessQuestColor _agentColor;

        public FixedMovePlanner(string move, ChessQuestColor agentColor)
        {
            _move = move;
            _agentColor = agentColor;
        }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "fixed_chessquest_move",
                1,
                [
                    new PlanStep(
                        "step_fixed_move",
                        ChessQuestToolIds.PlayMove,
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>
                        {
                            ["move"] = _move,
                            ["turnIntent"] = TurnIntent(
                                _agentColor.ToString().ToLowerInvariant(),
                                _move,
                                "Use the selected legal move for this bounded opponent turn.")
                        })
                    {
                        Intent = new ExecutionIntent(
                            $"Play {_move} as {_agentColor}.",
                            "The fixed test planner is choosing a legal move through the strict surface.",
                            "Commit exactly one opponent-agent move.")
                    }
                ],
                "Fixed ChessQuest move."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }
}

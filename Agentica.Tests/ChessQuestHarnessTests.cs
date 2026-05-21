using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.CLI.Scenarios.ChessQuest;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Orchestration;
using Agentica.Orchestration.Context;
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
        Assert.True(projection.LegalProjectionOnly);
        Assert.False(projection.MoveQualityKnown);
        Assert.False(projection.SafetyKnown);
        Assert.False(projection.OpponentReplyModeled);
        Assert.Equal(ChessQuestColor.Black, projection.SideToMoveAfter);
        Assert.Contains("without generating any opponent reply", projection.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e7e5", projection.AcceptedPrefix);
    }

    [Fact]
    public async Task ChessQuest_project_line_marks_agent_authored_opponent_reply_when_supplied()
    {
        var session = CreateSession(opponentMoves: ["c7c5"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.ProjectLine,
            new Dictionary<string, object?>
            {
                ["line"] = new[] { "e2e4", "e7e5" },
                ["maxPlies"] = 4
            });

        var projection = Assert.IsType<ChessLineProjection>(result.Receipt.Data["projection"]);
        Assert.Equal(["e2e4", "e7e5"], projection.AcceptedPrefix);
        Assert.True(projection.LegalProjectionOnly);
        Assert.False(projection.MoveQualityKnown);
        Assert.False(projection.SafetyKnown);
        Assert.True(projection.OpponentReplyModeled);
        Assert.Equal(StartFen, session.CurrentState.Fen);
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
    public void ChessQuest_threat_aware_surface_registers_neutral_attack_inspection()
    {
        var session = CreateSession(policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);
        var catalog = ChessQuestTools.CreateCatalog(session);
        var context = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session);
        var protocol = Assert.IsType<ChessQuestDecisionProtocol>(context["decisionProtocol"]);

        Assert.Contains(catalog.Descriptors, descriptor => descriptor.ToolId == ChessQuestToolIds.InspectAttacks);
        Assert.Contains(catalog.Descriptors, descriptor => descriptor.ToolId == ChessQuestToolIds.InspectCandidate);
        Assert.Contains(protocol.ToolSemantics, pair => pair.Key == ChessQuestToolIds.InspectAttacks);
        Assert.Contains(protocol.ToolSemantics, pair => pair.Key == ChessQuestToolIds.InspectCandidate);

        var harness = ChessQuestCapabilitySurfaceCompiler.BuildHarnessContext(session);
        Assert.Contains(ChessQuestToolIds.InspectAttacks, harness.ContextSurfaceReceipt.ExposedToolIds);
        Assert.Contains(ChessQuestToolIds.InspectCandidate, harness.ContextSurfaceReceipt.ExposedToolIds);
        Assert.Equal(true, harness.ActiveCapabilitySurface.TurnContract["attackInspectionAllowed"]);
        Assert.Equal(true, harness.ActiveCapabilitySurface.TurnContract["agentAuthoredCandidateInspectionAllowed"]);
    }

    [Fact]
    public async Task ChessQuest_inspect_attacks_returns_public_opponent_captures_without_guidance()
    {
        const string attackedBishopFen = "4k3/ppb5/8/8/5B2/8/PP6/4K3 w - - 0 1";
        var session = CreateSession(
            fen: attackedBishopFen,
            policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);

        var result = await InvokeAsync(session, ChessQuestToolIds.InspectAttacks, new Dictionary<string, object?>());

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(attackedBishopFen, session.CurrentState.Fen);
        var inspection = Assert.IsType<ChessAttackInspection>(result.Receipt.Data["inspection"]);
        Assert.True(inspection.ReadOnly);
        Assert.False(inspection.EvaluationIncluded);
        Assert.False(inspection.GuidanceIncluded);
        Assert.Contains(inspection.OpponentLegalCaptures, capture =>
            capture.Move == "c7f4" &&
            capture.From == "c7" &&
            capture.To == "f4" &&
            capture.CapturedPiece == "white_bishop");
        var attacked = Assert.Single(inspection.AttackedAgentPieces);
        Assert.Equal("f4", attacked.Square);
        Assert.Equal("white_bishop", attacked.Piece);
        Assert.Contains("c7f4", attacked.CaptureMoves);
    }

    [Fact]
    public async Task ChessQuest_inspect_candidate_returns_after_candidate_opponent_captures_without_guidance()
    {
        const string candidateFen = "4k3/ppb5/8/8/8/4B3/PP6/4K3 w - - 0 1";
        var session = CreateSession(
            fen: candidateFen,
            policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.InspectCandidate,
            new Dictionary<string, object?>
            {
                ["move"] = "e3f4",
                ["legalMoveObservationId"] = legalMoveObservationId
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(candidateFen, session.CurrentState.Fen);
        var inspection = Assert.IsType<ChessCandidateInspection>(result.Receipt.Data["candidateInspection"]);
        Assert.True(inspection.CandidateLegal);
        Assert.Equal("e3f4", inspection.AcceptedMove);
        Assert.True(inspection.ReadOnly);
        Assert.True(inspection.SessionFenUnchanged);
        Assert.True(inspection.LegalProjectionOnly);
        Assert.True(inspection.CandidateScanOnly);
        Assert.False(inspection.MoveQualityKnown);
        Assert.False(inspection.SafetyKnown);
        Assert.False(inspection.OpponentReplyModeled);
        Assert.False(inspection.EvaluationIncluded);
        Assert.False(inspection.GuidanceIncluded);
        Assert.NotNull(inspection.AttackInspectionAfterCandidate);
        var attackInspection = inspection.AttackInspectionAfterCandidate!;
        Assert.Contains(attackInspection.OpponentLegalCaptures, capture =>
            capture.Move == "c7f4" &&
            capture.From == "c7" &&
            capture.To == "f4" &&
            capture.CapturedPiece == "white_bishop");
    }

    [Fact]
    public async Task ChessQuest_inspect_candidate_requires_current_legal_observation_binding()
    {
        var session = CreateSession(policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.InspectCandidate,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4"
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("missing_legal_move_observation_id", result.Receipt.Data["reason"]);
        Assert.Contains(ChessQuestToolIds.InspectCandidate, result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_inspect_candidate_enforces_per_turn_budget()
    {
        var session = CreateSession(policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        foreach (var move in new[] { "e2e4", "d2d4", "g1f3" })
        {
            var accepted = await InvokeAsync(
                session,
                ChessQuestToolIds.InspectCandidate,
                new Dictionary<string, object?>
                {
                    ["move"] = move,
                    ["legalMoveObservationId"] = legalMoveObservationId
                });
            Assert.Equal(ReceiptStatus.Succeeded, accepted.Receipt.Status);
        }

        var refused = await InvokeAsync(
            session,
            ChessQuestToolIds.InspectCandidate,
            new Dictionary<string, object?>
            {
                ["move"] = "c2c4",
                ["legalMoveObservationId"] = legalMoveObservationId
            });

        Assert.Equal(ReceiptStatus.Refused, refused.Receipt.Status);
        Assert.Equal("candidate_inspection_budget_exhausted", refused.Receipt.Data["reason"]);
        Assert.Equal(3, refused.Receipt.Data["maxCandidateInspectionsPerTurn"]);
    }

    [Fact]
    public async Task ChessQuest_inspect_candidate_requires_agent_turn()
    {
        var session = CreateSession(
            agentColor: ChessQuestColor.Black,
            policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.InspectCandidate,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = legalMoveObservationId
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("agent_not_to_move", result.Receipt.Data["reason"]);
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
    public async Task ChessQuest_list_legal_moves_exposes_observation_binding_and_check_state()
    {
        var session = CreateSession();

        var result = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.NotNull(result.Observation);
        Assert.Equal(result.Observation!.ObservationId, result.Receipt.Data["legalMoveObservationId"]);
        Assert.Equal(result.Receipt.ReceiptId, result.Receipt.Data["legalMoveReceiptId"]);
        Assert.Equal(false, result.Receipt.Data["sideToMoveInCheck"]);
        Assert.Contains("e2e4", Assert.IsType<string[]>(result.Receipt.Data["legalMoves"]));
    }

    [Fact]
    public async Task ChessQuest_play_move_requires_legal_move_observation_id_after_listing_moves()
    {
        var session = CreateSession();
        await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("missing_legal_move_observation_id", result.Receipt.Data["reason"]);
        Assert.Equal(true, result.Receipt.Data["fenUnchanged"]);
        Assert.Contains("e2e4", Assert.IsType<string[]>(result.Receipt.Data["currentLegalMoves"]));
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_play_move_requires_legal_move_observation_id_in_strict_gameplay_before_listing_moves()
    {
        var session = CreateSession();

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("missing_legal_move_observation_id", result.Receipt.Data["reason"]);
        Assert.Equal(true, result.Receipt.Data["fenUnchanged"]);
        Assert.Contains("e2e4", Assert.IsType<string[]>(result.Receipt.Data["currentLegalMoves"]));
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_actor_probe_surface_allows_play_without_legal_move_observation_id()
    {
        var session = CreateSession(
            policy: ChessQuestDisclosurePolicy.ActorProbe);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["turnIntent"] = TurnIntent("white", "e2e4", "Actor probe submitted a raw legal move for host validation.")
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("e2e4", result.Receipt.Data["agentMove"]);
    }

    [Fact]
    public async Task ChessQuest_play_move_accepts_turn_intent_without_duplicate_selected_move_when_top_level_move_is_valid()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);
        var turnIntent = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.");
        turnIntent.Remove("selectedMove");

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = turnIntent
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("e2e4", result.Receipt.Data["agentMove"]);
        var acceptedIntent = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Receipt.Data["turnIntent"]);
        Assert.Equal("e2e4", acceptedIntent["selectedMove"]);
    }

    [Fact]
    public async Task ChessQuest_play_move_rejects_turn_intent_selected_move_that_conflicts_with_top_level_move()
    {
        var session = CreateSession();
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);
        var turnIntent = TurnIntent("white", "d2d4", "Attempt a mismatched turn intent.");

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = turnIntent
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("missing_turn_intent", result.Receipt.Data["reason"]);
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_play_move_rejects_move_absent_from_bound_legal_observation()
    {
        var session = CreateSession();
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "a1a3",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = TurnIntent("white", "a1a3", "Attempt an invalid rook move for observation-binding testing.")
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("move_not_in_legal_move_observation", result.Receipt.Data["reason"]);
        Assert.Equal(legalMoveObservationId, result.Receipt.Data["legalMoveObservationId"]);
        Assert.Equal(true, result.Receipt.Data["fenUnchanged"]);
        Assert.Equal(StartFen, session.CurrentState.Fen);
    }

    [Fact]
    public async Task ChessQuest_play_move_rejects_stale_legal_move_observation_id()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var staleObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        var accepted = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = staleObservationId,
                ["turnIntent"] = TurnIntent("white", "e2e4", "Use a legal opening move and keep playing for a win.")
            });
        Assert.Equal(ReceiptStatus.Succeeded, accepted.Receipt.Status);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "g1f3",
                ["legalMoveObservationId"] = staleObservationId,
                ["turnIntent"] = TurnIntent("white", "g1f3", "Use a legal development move and keep playing for a win.")
            });

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("stale_legal_move_observation", result.Receipt.Data["reason"]);
        Assert.Equal(staleObservationId, result.Receipt.Data["legalMoveObservationId"]);
        Assert.Equal(true, result.Receipt.Data["fenUnchanged"]);
        Assert.Equal(2, result.Receipt.Data["currentPly"]);
        Assert.Contains("g1f3", Assert.IsType<string[]>(result.Receipt.Data["currentLegalMoves"]));
    }

    [Fact]
    public async Task ChessQuest_play_move_applies_agent_move_then_engine_backed_opponent()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);

        var result = await PlayLegalMoveAsync(
            session,
            "e2e4",
            "Use a legal opening move and keep playing for a win.");

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
        await PlayLegalMoveAsync(
            session,
            "e2e4",
            "Use a legal opening move and keep playing for a win.");

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
        await PlayLegalMoveAsync(
            session,
            "e2e4",
            "Use a legal opening move and keep playing for a win.");

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
        await PlayLegalMoveAsync(
            session,
            "e2e4",
            "Use a legal opening move and keep playing for a win.");

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
        var doctrine = Assert.IsType<ChessQuestPlayingDoctrine>(context["playingDoctrine"]);
        var protocol = Assert.IsType<ChessQuestDecisionProtocol>(context["decisionProtocol"]);
        var goalSpine = Assert.IsType<ChessQuestGoalSpine>(context["goalSpine"]);
        var continuityCapsule = Assert.IsType<ChessQuestContinuityCapsule>(context["continuityCapsule"]);
        var chessFrame = Assert.IsType<ChessQuestPlanningFrame>(context["chessFrame"]);
        Assert.Equal("opening", strategyFrame.Phase);
        Assert.Equal("opening", objective.Phase);
        Assert.Equal(3, objective.MaxAgentTurns);
        Assert.Equal(0, progress.AgentTurnsPlayed);
        Assert.Equal(3, progress.AgentTurnsRemaining);
        Assert.Contains(doctrine.GoodPlayCriteria, item => item.Contains("material", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("opening", protocol.Phase);
        Assert.Contains("not mean good or safe", protocol.ToolSemantics[ChessQuestToolIds.ListLegalMoves], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(protocol.ClaimDiscipline, item => item.Contains("legal", StringComparison.OrdinalIgnoreCase) && item.Contains("safe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(chessFrame.GoalSpine, goalSpine);
        Assert.Equal(chessFrame.ContinuityCapsule, continuityCapsule);
        Assert.Contains("legal", goalSpine.ActiveConstraints[2], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proof", goalSpine.ActiveConstraints[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not proof", ChessQuestCapabilitySurfaceCompiler.PromptTemplateShape, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_prompt_shape_preserves_legal_not_safe_and_project_line_not_evaluation_contract()
    {
        var prompt = ChessQuestCapabilitySurfaceCompiler.PromptTemplateShape;

        Assert.Contains("legal move is not necessarily good or safe", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one-ply project_line result does not prove tactical safety or move quality", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Evidence sources have limits", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate scan", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inspect_candidate", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("goalSpine", prompt, StringComparison.Ordinal);
        Assert.Contains("continuityCapsule", prompt, StringComparison.Ordinal);
        Assert.Contains("not proof", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legalMoveObservationId", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChessQuest_projected_surface_does_not_leak_unavailable_candidate_tool()
    {
        var session = CreateSession(policy: ChessQuestDisclosurePolicy.StrictRefereeProjected);
        var catalog = ChessQuestTools.CreateCatalog(session);
        var harness = ChessQuestCapabilitySurfaceCompiler.BuildHarnessContext(session);
        var context = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session);
        var json = JsonSerializer.Serialize(new { harness, context }, JsonOptions());

        Assert.DoesNotContain(catalog.Descriptors, descriptor => descriptor.ToolId == ChessQuestToolIds.InspectCandidate);
        Assert.DoesNotContain(ChessQuestToolIds.InspectCandidate, harness.ContextSurfaceReceipt.ExposedToolIds);
        Assert.False(harness.ActiveCapabilitySurface.TurnContract.ContainsKey("agentAuthoredCandidateInspectionAllowed"));
        Assert.DoesNotContain("chess.inspect_candidate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inspect_agent_candidate", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_threat_aware_legacy_policy_enables_effective_candidate_inspection()
    {
        var policy = new ChessQuestDisclosurePolicy(
            Mode: ChessQuestMode.StrictRefereeThreatAware,
            IncludeSan: false,
            IncludeCurrentCheckStatus: true,
            IncludeHostCandidateConsequences: false,
            IncludeMaterialCounts: false,
            IncludeTacticalLabels: false,
            IncludeEngineEvaluation: false,
            IncludeHiddenObjectiveHints: false,
            AllowLineProjection: true,
            MaxProjectedLinesPerTurn: 4,
            MaxProjectedPliesPerLine: 6,
            IncludeProjectionCaptures: true,
            AllowAttackInspection: true);

        Assert.False(policy.AllowCandidateInspection);
        Assert.True(policy.EffectiveAllowCandidateInspection);
        Assert.Equal(3, policy.EffectiveMaxCandidateInspectionsPerTurn);
    }

    [Fact]
    public void ChessQuest_goal_spine_records_material_loss_as_continuity_pressure_without_move_hinting()
    {
        var session = CreateSession();
        var latest = PhaseReport(materialAfter: -3, materialDelta: -3);

        var spine = ChessQuestGoalSpineCompiler.Compile(session, latestPhaseReport: latest);

        Assert.Contains("lost material", spine.KnownDivergence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stabilize", spine.ActivePriority, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opponent replies", spine.NextDecisionPressure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(spine.RecentLessons, lesson =>
            lesson.Contains("material loss", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("play ", spine.ActivePriority, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e2e4", spine.ActivePriority, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChessQuest_goal_spine_records_unsupported_safety_claim_as_continuity_pressure()
    {
        var session = CreateSession(opponentMoves: []);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentColor"] = "white",
                    ["selectedMove"] = "e2e4",
                    ["legalBasis"] = "selected_from_current_legal_move_list",
                    ["goal"] = "Play a safe move.",
                    ["evidence"] = new[] { "e2e4 appeared in the legal move list" },
                    ["hypothesis"] = "This move is safe and secure.",
                    ["riskCheck"] = "No risk remains.",
                    ["claimLevel"] = "verified",
                    ["publicReason"] = "The move is safe.",
                    ["completionClaim"] = false
                }
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);

        var spine = ChessQuestGoalSpineCompiler.Compile(session);

        Assert.Contains("safety claim", spine.KnownDivergence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opponent", spine.NextDecisionPressure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(spine.RecentLessons, lesson =>
            lesson.Contains("do not prove full tactical safety", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(spine.EvidenceRefs, evidence =>
            evidence == $"receipt:{result.Receipt.ReceiptId}");
    }

    [Fact]
    public async Task ChessQuest_goal_spine_records_unsupported_checkmate_claim_as_continuity_pressure()
    {
        var session = CreateSession(opponentMoves: []);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);

        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentColor"] = "white",
                    ["selectedMove"] = "e2e4",
                    ["legalBasis"] = "selected_from_current_legal_move_list",
                    ["goal"] = "Deliver checkmate.",
                    ["evidence"] = new[] { "e2e4 appeared in the legal move list" },
                    ["hypothesis"] = "This move checkmates Black.",
                    ["riskCheck"] = "Verifier will confirm the terminal state.",
                    ["claimLevel"] = "verified",
                    ["publicReason"] = "This is checkmate.",
                    ["completionClaim"] = false
                }
            });

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);

        var spine = ChessQuestGoalSpineCompiler.Compile(session);

        Assert.Contains("checkmate", spine.KnownDivergence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("terminal", spine.NextDecisionPressure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(spine.RecentLessons, lesson =>
            lesson.Contains("Checkmate claims require", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChessQuest_continuity_capsule_records_chess_native_handoff_without_move_hinting()
    {
        var session = CreateSession();
        var latest = PhaseReport(materialAfter: -3, materialDelta: -3);

        var capsule = ChessQuestContinuityCapsuleCompiler.Compile(session, latest);

        Assert.Equal("chessquest.continuity_capsule", capsule.Kind);
        Assert.Equal(session.CurrentState.Fen, capsule.CurrentFen);
        Assert.Equal("low", capsule.Confidence);
        Assert.Contains(capsule.PressurePoints, point =>
            point.Contains("lost material", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capsule.Uncertainties, uncertainty =>
            uncertainty.Contains("Legal and projection evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("stabilization", capsule.RecommendedNextBias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e2e4", capsule.RecommendedNextBias, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("play ", capsule.RecommendedNextBias, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_continuity_capsule_can_be_imported_into_next_planning_frame()
    {
        var priorSession = CreateSession();
        var prior = ChessQuestContinuityCapsuleCompiler.Compile(
            priorSession,
            PhaseReport(materialAfter: -3, materialDelta: -3));
        var nextSession = CreateSession();

        nextSession.ImportContinuityCapsule(prior);
        var context = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(nextSession);
        var capsule = Assert.IsType<ChessQuestContinuityCapsule>(context["continuityCapsule"]);

        Assert.NotNull(capsule.PriorRunCarryover);
        Assert.Equal(prior.CurrentFen, capsule.PriorRunCarryover!.CurrentFen);
        Assert.Null(capsule.PriorRunCarryover.PriorRunCarryover);
        Assert.Contains(capsule.PriorRunCarryover.PressurePoints, point =>
            point.Contains("lost material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChessQuest_game_record_store_persists_and_loads_continuity_capsule()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chessquest_capsule_{Guid.NewGuid():N}");
        try
        {
            var session = CreateSession();
            var latest = PhaseReport(materialAfter: -3, materialDelta: -3);

            ChessQuestGameRecordStore.WriteDirectory(directory, session);
            ChessQuestGameRecordStore.WriteContinuityCapsule(directory, session, latest);

            var loaded = ChessQuestGameRecordStore.TryLoadContinuityCapsule(directory);

            Assert.NotNull(loaded);
            Assert.Equal(session.CurrentState.Fen, loaded!.CurrentFen);
            Assert.Contains(loaded.PressurePoints, point =>
                point.Contains("lost material", StringComparison.OrdinalIgnoreCase));
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
    public void ChessQuest_goal_spine_is_projected_to_orchestration_host_state()
    {
        var session = CreateSession();
        var state = new ChessQuestStrategicOrchestrationState(session);
        state.PhaseReports.Add(PhaseReport(materialAfter: -3, materialDelta: -3));

        var hostState = state.BuildHostState();

        var spine = Assert.IsType<ChessQuestGoalSpine>(hostState["chessquest.goalSpine"]);
        Assert.Equal(spine.ActivePriority, hostState["chessquest.goalSpine.activePriority"]);
        Assert.Equal(spine.KnownDivergence, hostState["chessquest.goalSpine.knownDivergence"]);
        Assert.Contains("Stabilize", spine.ActivePriority, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_tool_descriptors_explain_project_line_is_not_evaluation()
    {
        var session = CreateSession();
        var catalog = ChessQuestTools.CreateCatalog(session);

        var projectLine = catalog.Descriptors.Single(descriptor => descriptor.ToolId == ChessQuestToolIds.ProjectLine);
        Assert.Contains("does not rank moves", projectLine.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prove safety", projectLine.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evaluate quality", projectLine.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generate opponent replies", projectLine.Description, StringComparison.OrdinalIgnoreCase);

        var legalMoves = catalog.Descriptors.Single(descriptor => descriptor.ToolId == ChessQuestToolIds.ListLegalMoves);
        Assert.Contains("legality does not imply safety", legalMoves.Description, StringComparison.OrdinalIgnoreCase);

        var threatAwareCatalog = ChessQuestTools.CreateCatalog(CreateSession(policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware));
        var inspectCandidate = threatAwareCatalog.Descriptors.Single(descriptor => descriptor.ToolId == ChessQuestToolIds.InspectCandidate);
        Assert.Contains("agent-authored candidate move", inspectCandidate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not rank", inspectCandidate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prove safety", inspectCandidate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("choose a reply", inspectCandidate.Description, StringComparison.OrdinalIgnoreCase);

        var descriptorJson = JsonSerializer.Serialize(catalog.Descriptors, JsonOptions());
        Assert.DoesNotContain("g1f3", descriptorJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e2e4", descriptorJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_tactical_decision_protocol_requires_opponent_reply_modeling_or_uncertainty_for_safety_claims()
    {
        var session = CreateSession();
        var projection = ChessQuestPhaseTracker.DefaultProjection(
            session,
            "tactical",
            source: "test");
        var phase = ChessQuestPhaseTracker.Create(
            session,
            "tactical",
            maxAgentTurns: 2,
            strategyProjection: projection);

        var protocol = ChessQuestGoalShapingPolicy.BuildDecisionProtocol(session, phase);

        Assert.Equal("tactical", protocol.Phase);
        Assert.Contains(protocol.ActiveObjectives, item =>
            item.Contains("opponent replies", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(protocol.ClaimDiscipline, item =>
            item.Contains("one-ply", StringComparison.OrdinalIgnoreCase) &&
            item.Contains("safety", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(protocol.ClaimDiscipline, item =>
            item.Contains("after-candidate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(protocol.EvidenceDepthRules, item =>
            item.Contains("inspect_candidate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(protocol.ForbiddenClaimLanguageWithoutEvidence, item =>
            string.Equals(item, "safe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("does not evaluate quality", protocol.ToolSemantics[ChessQuestToolIds.ProjectLine], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_threat_aware_decision_protocol_mentions_candidate_inspection_only_when_exposed()
    {
        var session = CreateSession(policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);
        var projection = ChessQuestPhaseTracker.DefaultProjection(
            session,
            "tactical",
            source: "test");
        var phase = ChessQuestPhaseTracker.Create(
            session,
            "tactical",
            maxAgentTurns: 2,
            strategyProjection: projection);

        var protocol = ChessQuestGoalShapingPolicy.BuildDecisionProtocol(session, phase);

        Assert.Contains(protocol.ClaimDiscipline, item =>
            item.Contains("after-candidate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(protocol.EvidenceDepthRules, item =>
            item.Contains("inspect_candidate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ChessQuestToolIds.InspectCandidate, protocol.ToolSemantics.Keys);
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
                ["chessquest.activeObjectives"] = new[] { "model opponent replies before safety claims" },
                ["chessquest.successSignals"] = new[] { "phase does not worsen material balance" },
                ["chessquest.claimDiscipline"] = new[] { "do not call a move safe from one-ply projection" },
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
        Assert.Contains("model opponent replies before safety claims", projection.ActiveObjectives);
        Assert.Contains("phase does not worsen material balance", projection.ProgressSignals);
        Assert.Contains("do not call a move safe from one-ply projection", projection.ClaimDiscipline);
        Assert.Contains("terminal game state", projection.StopTriggers);
    }

    [Fact]
    public void ChessQuest_phase_task_envelope_sanitizes_move_level_guidance()
    {
        var envelope = ChessQuestPhaseTaskEnvelope.FromContext(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chessquest.phase"] = "tactical",
                ["chessquest.taskDirection"] = "play e2e4 now",
                ["chessquest.phaseGoal"] = "Attack f7 with a concrete move hint.",
                ["chessquest.activeObjectives"] = new[]
                {
                    "attack f7",
                    "preserve material"
                }
            },
            defaultMaxAgentTurns: 3,
            taskNumber: 1);

        Assert.NotEqual("play e2e4 now", envelope.TaskDirection);
        Assert.NotEqual("Attack f7 with a concrete move hint.", envelope.PhaseGoal);
        Assert.DoesNotContain(envelope.ActiveObjectives, objective => objective.Contains("f7", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("preserve material", envelope.ActiveObjectives);
    }

    [Fact]
    public void ChessQuest_phase_sanity_warns_when_conversion_is_selected_while_materially_behind()
    {
        var envelope = ChessQuestPhaseTaskEnvelope.FromContext(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chessquest.phase"] = "conversion",
                ["chessquest.phaseGoal"] = "Convert toward a win."
            },
            defaultMaxAgentTurns: 3,
            taskNumber: 3);
        var latest = PhaseReport(materialAfter: -3, materialDelta: -3);

        var warnings = ChessQuestPhaseSanityPolicy.Evaluate(envelope, latest);

        Assert.Contains(warnings, warning =>
            warning.Contains("conversion/endgame selected while materially behind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChessQuest_phase_sanity_warns_when_tactical_phase_follows_material_loss_without_recovery_rationale()
    {
        var envelope = ChessQuestPhaseTaskEnvelope.FromContext(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chessquest.phase"] = "tactical",
                ["chessquest.taskDirection"] = "press_attack",
                ["chessquest.phaseGoal"] = "Find tactical pressure."
            },
            defaultMaxAgentTurns: 2,
            taskNumber: 2);
        var latest = PhaseReport(materialAfter: -3, materialDelta: -3);

        var warnings = ChessQuestPhaseSanityPolicy.Evaluate(envelope, latest);

        Assert.Contains(warnings, warning =>
            warning.Contains("tactical selected after material loss", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChessQuest_phase_sanity_allows_tactical_recovery_with_explicit_stabilization_rationale()
    {
        var envelope = ChessQuestPhaseTaskEnvelope.FromContext(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chessquest.phase"] = "tactical",
                ["chessquest.taskDirection"] = "seek_counterplay_after_material_loss",
                ["chessquest.phaseGoal"] = "Stabilize first, then seek counterplay only with modeled opponent replies."
            },
            defaultMaxAgentTurns: 2,
            taskNumber: 2);
        var latest = PhaseReport(materialAfter: -3, materialDelta: -3);

        var warnings = ChessQuestPhaseSanityPolicy.Evaluate(envelope, latest);

        Assert.DoesNotContain(warnings, warning =>
            warning.Contains("tactical selected after material loss", StringComparison.OrdinalIgnoreCase));
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
    public async Task ChessQuest_console_task_planner_falls_back_after_invalid_llm_graph()
    {
        var planner = new ChessQuestConsoleTaskPlanner(
            new InvalidTaskPlanner(),
            new ChessQuestDeterministicTaskPlanner(maxAgentTurns: 2));

        using var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var plan = await planner.CreatePlanAsync(new TaskPlanningRequest(
                new LargeTaskRequest(
                    "Choose a ChessQuest phase task.",
                    RequestOrigin.User,
                    new Dictionary<string, object?>()),
                new OrchestrationPolicy()));

            var task = Assert.Single(plan.Tasks);
            Assert.Equal("chessquest_phase_001", task.TaskId);
            Assert.Equal("phase_run", task.ContextProjection["chessquest.taskKind"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("Planner Repair", writer.ToString(), StringComparison.OrdinalIgnoreCase);
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
    public void ChessQuest_board_probe_options_parse_generated_puzzle_source()
    {
        var options = ChessQuestBoardProbeOptions.Parse(
            ["--puzzle-source", "generated"],
            "test-model");

        Assert.True(options.IsValid);
        Assert.Equal(ChessQuestPuzzleProbeSource.Generated, options.PuzzleSource);
    }

    [Fact]
    public void ChessQuest_board_probe_options_parse_state_probe_kind()
    {
        var options = ChessQuestBoardProbeOptions.Parse(
            ["--probe-kind", "stacked"],
            "test-model");

        Assert.True(options.IsValid);
        Assert.Equal(ChessQuestStateProbeKind.Stacked, options.StateProbeKind);
    }

    [Fact]
    public void ChessQuest_board_probe_disables_thinking_by_default()
    {
        var options = ChessQuestBoardProbeOptions.Parse([], "test-model");

        Assert.True(options.IsValid);
        Assert.Equal("off", options.ThinkingBudget);
    }

    [Fact]
    public void ChessQuest_state_probe_validates_stacked_board_facts()
    {
        var trial = ChessQuestStateProbeRunner.CreateTrial(
            seed: 9091,
            trialNumber: 1,
            scramblePlies: 14,
            kind: ChessQuestStateProbeKind.Stacked);

        var result = ChessQuestStateProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new
            {
                legality = new
                {
                    isLegal = trial.LegalityExpected,
                    publicReason = "Classify the supplied move against the current board."
                },
                capture = new
                {
                    isCapture = trial.CaptureExpected,
                    capturedPiece = trial.CapturedPieceExpected,
                    publicReason = "Classify capture truth from the destination square."
                },
                check = new
                {
                    sideToMoveInCheck = trial.SideToMoveInCheckExpected,
                    publicReason = "Read current check status."
                },
                material = new
                {
                    whiteMaterial = trial.WhiteMaterialExpected,
                    blackMaterial = trial.BlackMaterialExpected,
                    materialDeltaForSideToMove = trial.MaterialDeltaForSideToMoveExpected,
                    publicReason = "Count material points."
                },
                phaseSelection = new
                {
                    phase = trial.PhaseExpected,
                    publicReason = "Select the host-defined phase label."
                }
            }));

        Assert.True(result.Passed);
    }

    [Fact]
    public void ChessQuest_state_probe_prompt_stacks_multiple_questions_without_legal_move_list()
    {
        var trial = ChessQuestStateProbeRunner.CreateTrial(
            seed: 9109,
            trialNumber: 1,
            scramblePlies: 14,
            kind: ChessQuestStateProbeKind.Stacked);

        var prompt = ChessQuestStateProbeRunner.BuildPrompt(
            trial,
            ChessQuestBoardProbePresentation.Ascii);

        Assert.Contains("Answer all checks from this same board", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trial.LegalityMove, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trial.CaptureMove, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("material point totals", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("phase label", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Legal moves:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join(",", trial.LegalMoves), prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_legal_action_probe_validates_actor_move_against_engine_without_prompting_legal_list()
    {
        var trial = ChessQuestLegalActionProbeRunner.CreateTrial(
            seed: 4471,
            trialNumber: 1,
            scramblePlies: 12);
        var legalMove = trial.LegalMoves.First();

        var prompt = ChessQuestLegalActionProbeRunner.BuildPrompt(
            trial,
            ChessQuestBoardProbePresentation.Ascii);
        var result = ChessQuestLegalActionProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                legalMove,
                "Choose a legal actor move.")));

        Assert.True(result.Passed);
        Assert.Contains("You are not given the legal move list", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ground the answer in the board", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("origin square must appear", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not assume the king is on its starting square", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not infer castling rights", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not land on a square occupied by your own piece", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grades legal move validity, not strategic quality", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Any legal move passes", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Avoid long queen, rook, or bishop moves", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not use a default opening move", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coordinate UCI", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Legal moves:", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e2e4", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_legal_action_probe_rejects_non_uci_actor_move()
    {
        var trial = ChessQuestLegalActionProbeRunner.CreateTrial(
            seed: 5519,
            trialNumber: 1,
            scramblePlies: 10);

        var result = ChessQuestLegalActionProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "Nf3",
                "Try SAN notation.")));

        Assert.False(result.Passed);
        Assert.Contains("invalid_uci_format", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_legal_action_probe_flags_own_piece_destination()
    {
        var rules = new GeraChessRulesEngine(StartFen);
        var trial = new ChessQuestLegalActionProbeTrial(
            TrialNumber: 1,
            Seed: 0,
            Fen: StartFen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(StartFen),
            SideToMove: ChessQuestColor.White,
            LegalMoves: rules.ListLegalMoves().Select(move => move.Uci).ToArray());

        var result = ChessQuestLegalActionProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "e1d1",
                "Move the king onto the queen square.",
                OriginSquare: "e1",
                DestinationSquare: "d1",
                Piece: "king")));

        Assert.False(result.Passed);
        Assert.Contains("destination_occupied_by_own_piece", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_legal_action_probe_flags_blocked_sliding_path()
    {
        var rules = new GeraChessRulesEngine(StartFen);
        var trial = new ChessQuestLegalActionProbeTrial(
            TrialNumber: 1,
            Seed: 0,
            Fen: StartFen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(StartFen),
            SideToMove: ChessQuestColor.White,
            LegalMoves: rules.ListLegalMoves().Select(move => move.Uci).ToArray());

        var result = ChessQuestLegalActionProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "d1h5",
                "Move the queen through the blocked diagonal.",
                OriginSquare: "d1",
                DestinationSquare: "h5",
                Piece: "queen")));

        Assert.False(result.Passed);
        Assert.Contains("path_blocked_by_piece", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_legal_action_probe_flags_attempted_king_capture()
    {
        const string fen = "8/4k3/8/8/8/B7/8/4K3 w - - 0 1";
        var rules = new GeraChessRulesEngine(fen);
        var trial = new ChessQuestLegalActionProbeTrial(
            TrialNumber: 1,
            Seed: 0,
            Fen: fen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(fen),
            SideToMove: ChessQuestColor.White,
            LegalMoves: rules.ListLegalMoves().Select(move => move.Uci).ToArray());

        var result = ChessQuestLegalActionProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "a3e7",
                "Capture the king.",
                OriginSquare: "a3",
                DestinationSquare: "e7",
                Piece: "bishop")));

        Assert.False(result.Passed);
        Assert.Contains("destination_is_opponent_king", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_validates_single_correct_answer()
    {
        var trial = ChessQuestPuzzleProbeRunner.BuiltInPuzzle();

        var result = ChessQuestPuzzleProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "d8h4",
                "Black checkmates White.")));

        Assert.True(result.Passed);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_prompt_does_not_reveal_accepted_answer()
    {
        var trial = ChessQuestPuzzleProbeRunner.BuiltInPuzzle();

        var prompt = ChessQuestPuzzleProbeRunner.BuildPrompt(
            trial,
            ChessQuestBoardProbePresentation.Ascii);

        Assert.Contains("Solve the puzzle from the board state", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Return the best move", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("For checkmate objectives", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Current public piece inventory", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coordinate UCI", prompt, StringComparison.OrdinalIgnoreCase);
        foreach (var acceptedMove in trial.AcceptedMoves)
        {
            Assert.DoesNotContain(acceptedMove, prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ChessQuest_puzzle_probe_prompt_supports_best_move_span()
    {
        var trial = new ChessQuestPuzzleProbeTrial(
            TrialNumber: 1,
            PuzzleId: "span_test",
            Source: ChessQuestPuzzleProbeSource.Generated,
            Objective: "Return one top-scoring legal move.",
            Fen: StartFen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(StartFen),
            AgentColor: ChessQuestColor.White,
            AcceptedMoves: ["e2e4", "d2d4"]);

        var prompt = ChessQuestPuzzleProbeRunner.BuildPrompt(
            trial,
            ChessQuestBoardProbePresentation.Ascii);

        Assert.Contains("accepted best-move span", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("There is exactly one accepted answer", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("e2e4", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("d2d4", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_accepts_any_move_in_best_move_span()
    {
        var trial = new ChessQuestPuzzleProbeTrial(
            TrialNumber: 1,
            PuzzleId: "span_test",
            Source: ChessQuestPuzzleProbeSource.Generated,
            Objective: "Return one top-scoring legal move.",
            Fen: StartFen,
            BoardLines: ChessQuestRenderer.RenderBoardLinesFromFen(StartFen),
            AgentColor: ChessQuestColor.White,
            AcceptedMoves: ["e2e4", "d2d4"]);

        var result = ChessQuestPuzzleProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "d2d4",
                "Choose one accepted top move.",
                OriginSquare: "d2",
                DestinationSquare: "d4",
                Piece: "pawn")));

        Assert.True(result.Passed);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_rotates_multiple_built_in_puzzles()
    {
        var puzzles = ChessQuestPuzzleProbeRunner.BuiltInPuzzlesForTests();

        Assert.True(puzzles.Count >= 3);
        Assert.Equal(puzzles.Count, puzzles.Select(puzzle => puzzle.PuzzleId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(puzzles, puzzle => puzzle.PuzzleId != "fools_mate_black_mate_in_one");
    }

    [Fact]
    public void ChessQuest_puzzle_probe_built_in_answers_are_legal()
    {
        foreach (var puzzle in ChessQuestPuzzleProbeRunner.BuiltInPuzzlesForTests())
        {
            var rules = new GeraChessRulesEngine(puzzle.Fen);
            var legalMoves = rules.ListLegalMoves().Select(move => move.Uci).ToHashSet(StringComparer.Ordinal);

            foreach (var acceptedMove in puzzle.AcceptedMoves)
            {
                Assert.Contains(acceptedMove, legalMoves);
            }
        }
    }

    [Fact]
    public void ChessQuest_puzzle_probe_generates_rules_derived_unique_answer()
    {
        var puzzle = ChessQuestPuzzleProbeRunner.CreateGeneratedPuzzle(
            trialNumber: 1,
            seed: 12345,
            scramblePlies: 24);

        var rules = new GeraChessRulesEngine(puzzle.Fen);
        var legalMoves = rules.ListLegalMoves().Select(move => move.Uci).ToHashSet(StringComparer.Ordinal);
        var prompt = ChessQuestPuzzleProbeRunner.BuildPrompt(
            puzzle,
            ChessQuestBoardProbePresentation.Ascii);

        Assert.Equal(ChessQuestPuzzleProbeSource.Generated, puzzle.Source);
        Assert.Single(puzzle.AcceptedMoves);
        Assert.Contains(puzzle.AcceptedMoves[0], legalMoves);
        Assert.DoesNotContain(puzzle.AcceptedMoves[0], prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Generated rules-derived puzzle", puzzle.Objective, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_create_puzzle_honors_generated_source()
    {
        var options = ChessQuestBoardProbeOptions.Parse(
            ["--puzzle-source", "generated", "--seed", "2222", "--scramble-plies", "20"],
            "test-model");

        var puzzle = ChessQuestPuzzleProbeRunner.CreatePuzzle(options, zeroBasedIndex: 0);

        Assert.Equal(ChessQuestPuzzleProbeSource.Generated, puzzle.Source);
        Assert.StartsWith("generated_", puzzle.PuzzleId, StringComparison.Ordinal);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_generated_puzzles_vary_by_seed()
    {
        var puzzles = Enumerable.Range(0, 8)
            .Select(index => ChessQuestPuzzleProbeRunner.CreateGeneratedPuzzle(
                trialNumber: index + 1,
                seed: 10_000 + index * 137,
                scramblePlies: 24))
            .ToArray();

        Assert.True(puzzles.Select(puzzle => puzzle.Fen).Distinct(StringComparer.Ordinal).Count() > 3);
        Assert.All(puzzles, puzzle =>
        {
            Assert.Equal(ChessQuestPuzzleProbeSource.Generated, puzzle.Source);
            Assert.NotEmpty(puzzle.AcceptedMoves);
        });
    }

    [Fact]
    public void ChessQuest_puzzle_probe_flags_san_notation_as_invalid_uci()
    {
        var trial = ChessQuestPuzzleProbeRunner.BuiltInPuzzle();

        var result = ChessQuestPuzzleProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "Qh4#",
                "Black checkmates White.")));

        Assert.False(result.Passed);
        Assert.Contains("invalid_uci_format", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChessQuest_puzzle_probe_rejects_wrong_legal_answer()
    {
        var trial = ChessQuestPuzzleProbeRunner.BuiltInPuzzle();

        var result = ChessQuestPuzzleProbeRunner.Validate(
            trial,
            JsonSerializer.Serialize(new ChessQuestMoveProbeAnswer(
                "b8c6",
                "Develop a knight.")));

        Assert.False(result.Passed);
        Assert.Contains("wrong_move", result.FailureReason, StringComparison.OrdinalIgnoreCase);
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
                MaxSteps: 3,
                MaxRefinements: 1,
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

        var move = await PlayLegalMoveAsync(
            session,
            "d8h4",
            "Use the legal move that ends the current game.",
            agentColor: "black");
        Assert.Equal(ReceiptStatus.Succeeded, move.Receipt.Status);
        Assert.Equal(true, move.Receipt.Data["terminal"]);

        var complete = await InvokeAsync(session, ChessQuestToolIds.CompleteObjective, new Dictionary<string, object?>());
        Assert.Equal(ReceiptStatus.Succeeded, complete.Receipt.Status);
        Assert.NotNull(complete.Artifact);
        Assert.Equal("chessquest.objective_completed", complete.Artifact!.Kind);
    }

    [Fact]
    public void ChessQuest_completion_evaluator_fails_terminal_loss_without_more_refinement()
    {
        var session = CreateSession(
            fen: FoolsMateBlackToMoveFen,
            agentColor: ChessQuestColor.White,
            opponentMoves: []);
        session.ReplayCommittedPlies(
        [
            new ChessQuestRecordedPly(
                Ply: 1,
                Move: "d8h4",
                Color: ChessQuestColor.Black,
                Source: "opponent",
                FenBefore: FoolsMateBlackToMoveFen,
                FenAfter: string.Empty,
                At: DateTimeOffset.UtcNow)
        ]);

        var evaluation = new ChessQuestCompletionEvaluator(session).Evaluate(
            new Agentica.Runs.AgenticaRun(
                "run_test",
                new RunRequest("Win as White.", RequestOrigin.User, new Dictionary<string, object?>())));

        Assert.Equal(CompletionDecision.Failed, evaluation.Decision);
        Assert.Equal(StopReason.TerminalLoss, evaluation.StopReason);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Contains("terminal loss", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChessQuest_orchestration_reports_terminal_checkmate_loss_as_failed_not_blocked()
    {
        const string beforeFoolsMateFen = "rnbqkbnr/pppp1ppp/8/4p3/8/5P2/PPPPP1PP/RNBQKBNR w KQkq - 0 2";
        var session = CreateSession(
            fen: beforeFoolsMateFen,
            agentColor: ChessQuestColor.White,
            opponentMoves: ["d8h4"]);
        var state = new ChessQuestStrategicOrchestrationState(session);
        var executor = new ChessQuestPhaseRunExecutor(
            state,
            _ => new FixedMovePlanner("g2g4", ChessQuestColor.White),
            _ => new InMemoryEventSink(),
            defaultMaxAgentTurns: 1,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 2,
                MaxPlanContinuations: 0,
                PlanningMode: PlanningMode.QueryAndBlockerDriven,
                EvaluateCompletionAfterEachBatch: true));
        var orchestrator = new TaskOrchestrator(
            new SinglePhaseTaskPlanner("tactical", maxAgentTurns: 1),
            executor,
            new ChessQuestTaskAcceptanceEvaluator(state, targetPhaseTasks: 1),
            new DeterministicWorkContextCompiler(),
            state.BuildHostState,
            new OrchestrationPolicy(MaxRuns: 1, MaxRefinements: 0));

        var outcome = await orchestrator.RunAsync(new LargeTaskRequest(
            "Run a bounded phase that should end in checkmate loss.",
            RequestOrigin.User,
            new Dictionary<string, object?>()));

        Assert.True(session.CurrentState.IsTerminal);
        Assert.Equal(ChessQuestColor.Black, session.CurrentState.TerminalState?.Winner);
        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.TerminalLoss, outcome.StopReason);
        Assert.NotEqual(OrchestrationStatus.Blocked, outcome.Status);
        Assert.NotEqual(OrchestrationStopReason.MaxRefinementsReached, outcome.StopReason);
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
            new ExecutionPolicy(
                MaxSteps: 3,
                MaxRefinements: 1,
                MaxPlanContinuations: 0,
                PlanningMode: PlanningMode.QueryAndBlockerDriven),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session));

        var envelope = await runner.RunAsync(new RunRequest(
            "Win the game as White. Draw is not success.",
            RequestOrigin.User,
            ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session)));

        Assert.Contains(envelope.Details.PlanningFrames, frame => frame.Kind == "chessquest.cockpit");
        var stepStarted = events.Events.First(item =>
            item.Type == ExecutionEventType.StepStarted.WireName() &&
            item.Context?.ToolId == ChessQuestToolIds.PlayMove);
        Assert.NotNull(stepStarted.Intent);
        Assert.Equal("Play e2e4 as White.", stepStarted.Intent!.Action);
        Assert.Contains("public legal move", stepStarted.Intent.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChessQuest_deterministic_planner_refreshes_legal_moves_after_committed_turn()
    {
        var session = CreateSession(opponentMoves: ["e7e5", "d7d5"]);
        var runner = new AgenticaRunner(
            new ChessQuestDeterministicPlanner(session),
            ChessQuestTools.CreateCatalog(session),
            new InMemoryEventSink(),
            new ChessQuestOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 8,
                MaxRefinements: 8,
                MaxPlanContinuations: 2,
                PlanningMode: PlanningMode.QueryAndBlockerDriven),
            completionEvaluator: new ChessQuestCompletionEvaluator(session),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session));

        await runner.RunAsync(new RunRequest(
            "Win the game as White. Draw is not success.",
            RequestOrigin.User,
            ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session)));

        var playTurns = session.Turns
            .Where(turn => string.Equals(turn.Invocation.ToolId, ChessQuestToolIds.PlayMove, StringComparison.Ordinal))
            .ToArray();
        Assert.True(playTurns.Count(turn => turn.Result.Receipt.Status == ReceiptStatus.Succeeded) >= 2);
        Assert.True(session.CommittedPlies.Count(ply => ply.Source == "agent") >= 2);
        Assert.True(session.Turns.Count(turn =>
            string.Equals(turn.Invocation.ToolId, ChessQuestToolIds.ListLegalMoves, StringComparison.Ordinal)) >= 2);

        var refusalReasons = session.Turns
            .Where(turn => turn.Result.Receipt.Status == ReceiptStatus.Refused)
            .Select(turn => Convert.ToString(turn.Result.Receipt.Data["reason"]))
            .ToArray();
        Assert.DoesNotContain("missing_legal_move_observation_id", refusalReasons);
        Assert.DoesNotContain("stale_legal_move_observation", refusalReasons);
        Assert.DoesNotContain("move_not_in_legal_move_observation", refusalReasons);
    }

    [Fact]
    public async Task ChessQuest_cockpit_projection_does_not_print_refused_move_as_accepted()
    {
        var session = CreateSession();
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);
        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "a1a3",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = TurnIntent("white", "a1a3", "Attempt an invalid rook move for projection testing.")
            });
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);

        var sink = new ChessQuestCockpitEventSink(session);
        using var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            sink.Emit(new ExecutionEvent(
                "event_test",
                "receipt.emitted",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["receipt"] = result.Receipt.ReceiptId,
                    ["status"] = "refused"
                })
            {
                Context = new ExecutionEventContext(
                    RunId: "run_test",
                    AttemptNumber: 1,
                    StepId: "step_001",
                    ToolId: ChessQuestToolIds.PlayMove,
                    ReceiptId: result.Receipt.ReceiptId)
            });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("Outcome: move refused", output);
        Assert.Contains("FEN unchanged: True", output);
        Assert.Contains("Opponent move: none because the agent move was not committed.", output);
        Assert.DoesNotContain("Outcome: move accepted; no opponent reply applied.", output);
    }

    [Fact]
    public async Task ChessQuest_cockpit_warns_on_unsupported_safety_claim()
    {
        var session = CreateSession(opponentMoves: ["e7e5"]);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);
        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e2e4",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentColor"] = "white",
                    ["selectedMove"] = "e2e4",
                    ["legalBasis"] = "selected_from_current_legal_move_list",
                    ["publicReason"] = "This move is safe and winning.",
                    ["completionClaim"] = false
                }
            });
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);

        var sink = new ChessQuestCockpitEventSink(session);
        using var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            sink.Emit(new ExecutionEvent(
                "event_test",
                "receipt.emitted",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["receipt"] = result.Receipt.ReceiptId,
                    ["status"] = "succeeded"
                })
            {
                Context = new ExecutionEventContext(
                    RunId: "run_test",
                    AttemptNumber: 1,
                    StepId: "step_001",
                    ToolId: ChessQuestToolIds.PlayMove,
                    ReceiptId: result.Receipt.ReceiptId)
            });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("Trace warning: intent uses strong claim language", output);
        Assert.Contains("Trace warning: safety claim is unsupported", output);
    }

    [Fact]
    public async Task ChessQuest_cockpit_warns_when_safety_claim_conflicts_with_candidate_scan()
    {
        const string candidateFen = "4k3/ppb5/8/8/8/4B3/PP6/4K3 w - - 0 1";
        var session = CreateSession(
            fen: candidateFen,
            policy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware);
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);
        var scan = await InvokeAsync(
            session,
            ChessQuestToolIds.InspectCandidate,
            new Dictionary<string, object?>
            {
                ["move"] = "e3f4",
                ["legalMoveObservationId"] = legalMoveObservationId
            });
        var result = await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = "e3f4",
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agentColor"] = "white",
                    ["selectedMove"] = "e3f4",
                    ["legalBasis"] = "selected_from_current_legal_move_list",
                    ["goal"] = "Make a safe improving move.",
                    ["evidence"] = new[] { "e3f4 appeared in the current legal move list" },
                    ["hypothesis"] = "The move is safe.",
                    ["riskCheck"] = "No risk identified.",
                    ["claimLevel"] = "assertion",
                    ["publicReason"] = "This move is safe.",
                    ["completionClaim"] = false
                }
            });
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);

        var sink = new ChessQuestCockpitEventSink(session);
        using var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            EmitReceipt(sink, scan.Receipt, ChessQuestToolIds.InspectCandidate, "step_scan");
            EmitReceipt(sink, result.Receipt, ChessQuestToolIds.PlayMove, "step_play");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("Trace warning: safety claim conflicts with candidate scan", output);
    }

    private static void EmitReceipt(
        ChessQuestCockpitEventSink sink,
        Receipt receipt,
        string toolId,
        string stepId)
    {
        sink.Emit(new ExecutionEvent(
            $"event_{stepId}",
            "receipt.emitted",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["receipt"] = receipt.ReceiptId,
                ["status"] = receipt.Status.ToString()
            })
        {
            Context = new ExecutionEventContext(
                RunId: "run_test",
                AttemptNumber: 1,
                StepId: stepId,
                ToolId: toolId,
                ReceiptId: receipt.ReceiptId)
        });
    }

    private static ChessQuestSession CreateSession(
        string fen = StartFen,
        ChessQuestColor agentColor = ChessQuestColor.White,
        IReadOnlyList<string>? opponentMoves = null,
        IReadOnlyList<string>? hiddenSolutionLine = null,
        ChessQuestDisclosurePolicy? policy = null)
    {
        policy ??= ChessQuestDisclosurePolicy.StrictRefereeProjected;
        var scenario = new ChessQuestScenario(
            ScenarioId: "chessquest_test",
            Title: "ChessQuest Test",
            InitialFen: fen,
            AgentColor: agentColor,
            ObjectiveKind: ChessQuestObjectiveKind.WinGame,
            PublicObjective: $"Win the game as {agentColor}. Draw is not success.",
            Difficulty: new ChessQuestDifficulty(
                Scenario: "test",
                Surface: policy.Mode.ToString(),
                Opponent: "scripted"),
            DisclosurePolicy: policy,
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

    private static async Task<ToolResult> PlayLegalMoveAsync(
        ChessQuestSession session,
        string move,
        string publicReason,
        string agentColor = "white")
    {
        var legalMoves = await InvokeAsync(session, ChessQuestToolIds.ListLegalMoves, new Dictionary<string, object?>());
        var legalMoveObservationId = Assert.IsType<string>(legalMoves.Receipt.Data["legalMoveObservationId"]);
        return await InvokeAsync(
            session,
            ChessQuestToolIds.PlayMove,
            new Dictionary<string, object?>
            {
                ["move"] = move,
                ["legalMoveObservationId"] = legalMoveObservationId,
                ["turnIntent"] = TurnIntent(agentColor, move, publicReason)
            });
    }

    private static ChessQuestPhaseReport PhaseReport(
        int materialAfter,
        int materialDelta,
        string phase = "tactical") =>
        new(
            Kind: "ChessQuestPhaseReport",
            PhaseRunId: "phase_test",
            Phase: phase,
            StrategyName: "test",
            Status: "budget_complete",
            StopReason: "agent_turn_budget_exhausted",
            AgentTurnsPlayed: 1,
            AgentTurnsRemaining: 0,
            StartPly: 0,
            EndPly: 2,
            StartFen: StartFen,
            EndFen: StartFen,
            MaterialBalanceBefore: materialAfter - materialDelta,
            MaterialBalanceAfter: materialAfter,
            MaterialBalanceDelta: materialDelta,
            Terminal: false,
            TerminalState: null,
            AgentMoves: ["e2e4"],
            OpponentMoves: ["e7e5"],
            Evidence: []);

    private static Dictionary<string, object?> TurnIntent(
        string agentColor,
        string selectedMove,
        string publicReason) =>
        new(StringComparer.Ordinal)
        {
            ["agentColor"] = agentColor,
            ["selectedMove"] = selectedMove,
            ["legalBasis"] = "selected_from_current_legal_move_list",
            ["goal"] = "Play a legal move while preserving the win objective.",
            ["evidence"] = new[] { $"{selectedMove} was selected as the current legal move candidate" },
            ["hypothesis"] = "The selected move may improve the position without making an unsupported claim.",
            ["riskCheck"] = "Opponent replies are not fully modeled, so safety is unverified.",
            ["claimLevel"] = "hypothesis",
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
                "chessquest_single_move_list",
                1,
                [
                    new PlanStep(
                        "step_001",
                        ChessQuestToolIds.ListLegalMoves,
                        ToolKind.Query,
                        ToolEffect.ReadOnly,
                        new Dictionary<string, object?>())
                    {
                        Intent = new ExecutionIntent(
                            "List legal moves for White.",
                            "Strict ChessQuest play_move requires a fresh legalMoveObservationId.",
                            "Receive legal UCI moves and the observation id for the current board.")
                    }
                ],
                "Bind current legal moves before committing one ChessQuest move."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            var legalMoveObservationId = Assert.IsType<string>(observation.Data["legalMoveObservationId"]);
            return Task.FromResult(new WorkflowPlan(
                "chessquest_single_move_play",
                2,
                [
                    new PlanStep(
                        "step_002",
                        ChessQuestToolIds.PlayMove,
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>
                        {
                            ["move"] = "e2e4",
                            ["legalMoveObservationId"] = legalMoveObservationId,
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
        }
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
                "fixed_chessquest_move_list",
                1,
                [
                    new PlanStep(
                        "step_fixed_list",
                        ChessQuestToolIds.ListLegalMoves,
                        ToolKind.Query,
                        ToolEffect.ReadOnly,
                        new Dictionary<string, object?>())
                    {
                        Intent = new ExecutionIntent(
                            $"List legal moves as {_agentColor}.",
                            "Strict ChessQuest play_move requires a fresh legalMoveObservationId.",
                            "Receive legal UCI moves and the observation id for the current board.")
                    }
                ],
                "Bind legal moves for fixed ChessQuest move."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            var legalMoveObservationId = Assert.IsType<string>(observation.Data["legalMoveObservationId"]);
            return Task.FromResult(new WorkflowPlan(
                "fixed_chessquest_move_play",
                2,
                [
                    new PlanStep(
                        "step_fixed_move",
                        ChessQuestToolIds.PlayMove,
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>
                        {
                            ["move"] = _move,
                            ["legalMoveObservationId"] = legalMoveObservationId,
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
        }
    }

    private sealed class SinglePhaseTaskPlanner : ITaskPlanner
    {
        private readonly string _phase;
        private readonly int _maxAgentTurns;

        public SinglePhaseTaskPlanner(string phase, int maxAgentTurns)
        {
            _phase = phase;
            _maxAgentTurns = maxAgentTurns;
        }

        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskGraphPlan(
                "single_phase_plan",
                request.Request.Objective,
                [
                    new TaskNode(
                        "phase_001",
                        "Execute one bounded ChessQuest phase.",
                        DependsOn: [],
                        Optional: false,
                        Priority: 0,
                        MaxRuns: 1,
                        ContextProjection: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["chessquest.taskKind"] = "phase_run",
                            ["chessquest.phase"] = _phase,
                            ["chessquest.taskDirection"] = "terminal_loss_regression",
                            ["chessquest.publicRationale"] = "Exercise top-level terminal loss handling.",
                            ["chessquest.phaseGoal"] = "Commit one legal move and let the verifier classify terminal state.",
                            ["chessquest.maxAgentTurns"] = _maxAgentTurns
                        },
                        AcceptanceRequirements:
                        [
                            new TaskAcceptanceRequirement(
                                TaskAcceptanceRequirementKind.Artifact,
                                ArtifactKind: "chessquest.phase_report")
                        ])
                ],
                DefinitionOfDone: [],
                CreatedAt: DateTimeOffset.UtcNow));

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Single phase regression should not refine.");
    }

    private sealed class InvalidTaskPlanner : ITaskPlanner
    {
        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            throw new TaskGraphValidationException("Invalid LLM task graph for testing.");

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default) =>
            throw new TaskGraphValidationException("Invalid LLM task refinement for testing.");
    }
}

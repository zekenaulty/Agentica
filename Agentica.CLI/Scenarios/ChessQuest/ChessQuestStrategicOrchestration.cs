using Agentica;
using Agentica.Artifacts;
using Agentica.Clients.Orchestration;
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

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed record ChessQuestPhaseTaskEnvelope(
    string TaskKind,
    string Phase,
    string TaskDirection,
    string PublicRationale,
    string PhaseGoal,
    IReadOnlyList<string> ActiveObjectives,
    IReadOnlyList<string> SuccessSignals,
    IReadOnlyList<string> ClaimDiscipline,
    int MaxAgentTurns,
    IReadOnlyList<string> ReplanTriggers)
{
    public static ChessQuestPhaseTaskEnvelope FromContext(
        IReadOnlyDictionary<string, object?> context,
        int defaultMaxAgentTurns,
        int taskNumber)
    {
        var phase = ReadString(context, "chessquest.phase")
            ?? ReadString(context, "phase")
            ?? "opening";
        var direction = ReadString(context, "chessquest.taskDirection")
            ?? ReadString(context, "taskDirection")
            ?? DefaultDirection(phase);
        var rationale = ReadString(context, "chessquest.publicRationale")
            ?? ReadString(context, "publicRationale")
            ?? $"Execute bounded {phase} phase task {taskNumber}.";
        var phaseGoal = ReadString(context, "chessquest.phaseGoal")
            ?? ReadString(context, "phaseGoal")
            ?? DefaultGoal(phase);
        var activeObjectives = ReadStringList(context, "chessquest.activeObjectives")
            ?? ReadStringList(context, "activeObjectives")
            ?? DefaultObjectives(phase, phaseGoal);
        var successSignals = ReadStringList(context, "chessquest.successSignals")
            ?? ReadStringList(context, "successSignals")
            ?? DefaultSuccessSignals(phase);
        var claimDiscipline = ReadStringList(context, "chessquest.claimDiscipline")
            ?? ReadStringList(context, "claimDiscipline")
            ?? DefaultClaimDiscipline(phase);
        var maxAgentTurns = ReadInt(context, "chessquest.maxAgentTurns")
            ?? ReadInt(context, "maxAgentTurns")
            ?? defaultMaxAgentTurns;
        var triggers = ReadStringList(context, "chessquest.replanTriggers")
            ?? ReadStringList(context, "replanTriggers")
            ?? DefaultTriggers();

        return new ChessQuestPhaseTaskEnvelope(
            TaskKind: ReadString(context, "chessquest.taskKind") ?? "phase_run",
            Phase: ChessQuestGoalShapingPolicy.SanitizePhase(phase),
            TaskDirection: ChessQuestGoalShapingPolicy.SanitizeText(direction, DefaultDirection(phase)),
            PublicRationale: ChessQuestGoalShapingPolicy.SanitizeText(rationale, $"Execute bounded {phase} phase task {taskNumber}."),
            PhaseGoal: ChessQuestGoalShapingPolicy.SanitizeText(phaseGoal, DefaultGoal(phase)),
            ActiveObjectives: ChessQuestGoalShapingPolicy.SanitizeList(activeObjectives, DefaultObjectives(phase, phaseGoal), maxItems: 6),
            SuccessSignals: ChessQuestGoalShapingPolicy.SanitizeList(successSignals, DefaultSuccessSignals(phase), maxItems: 6),
            ClaimDiscipline: ChessQuestGoalShapingPolicy.SanitizeList(claimDiscipline, DefaultClaimDiscipline(phase), maxItems: 8),
            MaxAgentTurns: Math.Max(1, maxAgentTurns),
            ReplanTriggers: ChessQuestGoalShapingPolicy.SanitizeList(triggers, DefaultTriggers(), maxItems: 8));
    }

    public ChessQuestStrategyProjection ToProjection(ChessQuestSession session)
    {
        var fallback = ChessQuestPhaseTracker.DefaultProjection(
            session,
            Phase,
            source: "orchestration_task");
        var objectives = new[]
            {
                PhaseGoal,
                $"Direction: {TaskDirection}",
                "verify check and checkmate claims with chess.project_line"
            }
            .Concat(ActiveObjectives)
            .Concat(fallback.ActiveObjectives)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return fallback with
        {
            ProjectionId = $"strategy_projection_{Guid.NewGuid():N}"[..31],
            Source = "orchestration_task",
            Phase = Phase,
            StrategyName = TaskDirection,
            StrategyIntent = PublicRationale,
            ActiveObjectives = objectives,
            StopTriggers = ReplanTriggers.Count == 0 ? fallback.StopTriggers : ReplanTriggers,
            ProgressSignals = SuccessSignals.Count == 0
                ? fallback.ProgressSignals
                : SuccessSignals,
            VerificationRules = fallback.VerificationRules,
            ClaimDiscipline = ClaimDiscipline.Count == 0
                ? fallback.ClaimDiscipline
                : ClaimDiscipline
        };
    }

    private static string DefaultDirection(string phase) =>
        Normalize(phase) switch
        {
            "tactical" => "verify_and_convert_tactical_opportunity",
            "endgame" or "conversion" => "convert_durable_advantage",
            "defense" or "recovery" => "stabilize_position",
            _ => "continue_development"
        };

    private static string DefaultGoal(string phase) =>
        Normalize(phase) switch
        {
            "tactical" => "Verify forcing ideas through public projection and convert only legal, checked tactics.",
            "endgame" or "conversion" => "Convert toward a win while preserving material and avoiding draw outcomes.",
            "defense" or "recovery" => "Stabilize the position, resolve immediate threats, and avoid unsupported claims.",
            _ => "Develop pieces, contest the center, improve king safety, and avoid immediate material loss."
        };

    private static IReadOnlyList<string> DefaultObjectives(
        string phase,
        string phaseGoal) =>
        Normalize(phase) switch
        {
            "tactical" =>
            [
                phaseGoal,
                "model opponent replies before safety claims",
                "avoid unsupported material or forcing claims"
            ],
            "defense" or "recovery" =>
            [
                phaseGoal,
                "preserve king safety",
                "avoid further material loss",
                "seek counterplay only after checking public consequences"
            ],
            "endgame" or "conversion" =>
            [
                phaseGoal,
                "simplify only when advantage or stability is real",
                "avoid draw outcomes",
                "verify terminal claims"
            ],
            _ =>
            [
                phaseGoal,
                "develop pieces",
                "contest central squares",
                "improve king safety"
            ]
        };

    private static IReadOnlyList<string> DefaultSuccessSignals(string phase) =>
        Normalize(phase) switch
        {
            "tactical" =>
            [
                "opponent replies are modeled before safety claims",
                "material or terminal claims are evidence-backed",
                "phase does not worsen material balance"
            ],
            "defense" or "recovery" =>
            [
                "king is not in check after opponent reply",
                "material balance does not worsen",
                "immediate threats are reduced or acknowledged"
            ],
            "endgame" or "conversion" =>
            [
                "conversion condition is evidence-backed",
                "simplification does not worsen the position",
                "terminal claims are verifier-backed"
            ],
            _ =>
            [
                "legal move committed",
                "king safety improves or remains stable",
                "piece activity improves without unsupported claims"
            ]
        };

    private static IReadOnlyList<string> DefaultClaimDiscipline(string phase) =>
        Normalize(phase) switch
        {
            "tactical" =>
            [
                "treat tactical ideas as hypotheses until opponent replies are modeled",
                "legal projection does not prove safety",
                "material claims require projected line or receipt evidence"
            ],
            "endgame" or "conversion" =>
            [
                "do not select conversion/endgame framing merely because ply is high",
                "conversion claims require real material, positional, promotion, or terminal evidence",
                "do not claim simplification helps unless evidence supports it"
            ],
            _ =>
            [
                "state what is verified, hypothesized, and unmodeled",
                "legal does not mean safe",
                "do not claim checkmate unless terminal state is verified"
            ]
        };

    private static IReadOnlyList<string> DefaultTriggers() =>
    [
        "terminal game state",
        "agent king in check",
        "major material swing",
        "phase budget exhausted",
        "strategy no longer fits legal board state"
    ];

    private static string Normalize(string phase) =>
        string.IsNullOrWhiteSpace(phase)
            ? "opening"
            : phase.Trim().ToLowerInvariant().Replace('_', '-');

    private static string? ReadString(
        IReadOnlyDictionary<string, object?> context,
        string key) =>
        context.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;

    private static int? ReadInt(
        IReadOnlyDictionary<string, object?> context,
        string key)
    {
        if (!context.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            double doubleValue => (int)Math.Round(doubleValue),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyList<string>? ReadStringList(
        IReadOnlyDictionary<string, object?> context,
        string key)
    {
        if (!context.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        }

        if (value is IEnumerable<object?> objects)
        {
            return objects
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        return null;
    }
}

public static class ChessQuestPhaseSanityPolicy
{
    public static IReadOnlyList<string> Evaluate(
        ChessQuestPhaseTaskEnvelope envelope,
        ChessQuestPhaseReport? latestReport)
    {
        if (latestReport is null)
        {
            return [];
        }

        var warnings = new List<string>();
        var phase = Normalize(envelope.Phase);
        if ((phase is "conversion" or "endgame") && latestReport.MaterialBalanceAfter < 0)
        {
            warnings.Add("conversion/endgame selected while materially behind; conversion framing should be evidence-backed by advantage, promotion, simplification, or a verified terminal route.");
        }

        if (phase is "tactical" &&
            latestReport.MaterialBalanceDelta < 0 &&
            !HasRecoveryRationale(envelope))
        {
            warnings.Add("tactical selected after material loss without recovery or stabilization rationale; tactical claims should model opponent replies or state uncertainty.");
        }

        return warnings;
    }

    private static bool HasRecoveryRationale(ChessQuestPhaseTaskEnvelope envelope)
    {
        var text = string.Join(
            " ",
            new[]
                {
                    envelope.TaskDirection,
                    envelope.PublicRationale,
                    envelope.PhaseGoal
                }
                .Concat(envelope.ActiveObjectives)
                .Concat(envelope.SuccessSignals)
                .Concat(envelope.ClaimDiscipline));

        return text.Contains("stabil", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("recover", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("defen", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("counterplay", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("material loss", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("material deficit", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string phase) =>
        string.IsNullOrWhiteSpace(phase)
            ? "opening"
            : phase.Trim().ToLowerInvariant().Replace('_', '-');
}

public sealed class ChessQuestStrategicOrchestrationState
{
    public ChessQuestStrategicOrchestrationState(ChessQuestSession session)
    {
        Session = session;
    }

    public ChessQuestSession Session { get; }

    public List<ChessQuestPhaseReport> PhaseReports { get; } = [];

    public List<ChessQuestStrategyProjection> StrategyProjections { get; } = [];

    public ChessQuestPhaseTaskEnvelope? LatestTaskEnvelope { get; set; }

    public ChessQuestPhaseReport? LatestPhaseReport => PhaseReports.LastOrDefault();

    public ChessQuestStrategyProjection? LatestStrategyProjection => StrategyProjections.LastOrDefault();

    public IReadOnlyDictionary<string, object?> BuildHostState()
    {
        var state = Session.CurrentState;
        var latest = LatestPhaseReport;
        var projection = LatestStrategyProjection;
        var agentWon = state.TerminalState?.Winner == Session.Scenario.AgentColor;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chessquest.currentFen"] = state.Fen,
            ["chessquest.ply"] = state.Ply,
            ["chessquest.agentColor"] = Session.Scenario.AgentColor.ToString(),
            ["chessquest.sideToMove"] = state.SideToMove.ToString(),
            ["chessquest.terminal"] = state.IsTerminal,
            ["chessquest.agentWon"] = agentWon,
            ["chessquest.phaseReports.count"] = PhaseReports.Count
        };
        var goalSpine = ChessQuestGoalSpineCompiler.Compile(Session, latestPhaseReport: latest);
        result["chessquest.goalSpine"] = goalSpine;
        result["chessquest.goalSpine.currentReality"] = goalSpine.CurrentReality;
        result["chessquest.goalSpine.activePriority"] = goalSpine.ActivePriority;
        result["chessquest.goalSpine.knownDivergence"] = goalSpine.KnownDivergence;
        result["chessquest.goalSpine.nextDecisionPressure"] = goalSpine.NextDecisionPressure;

        if (latest is not null)
        {
            result["chessquest.latestPhaseReport.phase"] = latest.Phase;
            result["chessquest.latestPhaseReport.status"] = latest.Status;
            result["chessquest.latestPhaseReport.stopReason"] = latest.StopReason;
            result["chessquest.latestPhaseReport.agentTurnsPlayed"] = latest.AgentTurnsPlayed;
            result["chessquest.latestPhaseReport.materialDelta"] = latest.MaterialBalanceDelta;
            result["chessquest.latestPhaseReport.terminal"] = latest.Terminal;
            result["chessquest.latestPhaseReport.agentMoves"] = latest.AgentMoves;
            result["chessquest.latestPhaseReport.opponentMoves"] = latest.OpponentMoves;
        }

        if (projection is not null)
        {
            result["chessquest.strategyProjection.phase"] = projection.Phase;
            result["chessquest.strategyProjection.strategyName"] = projection.StrategyName;
            result["chessquest.strategyProjection.intent"] = projection.StrategyIntent;
        }

        return result;
    }
}

public sealed class ChessQuestPhaseRunExecutor : IRunExecutor
{
    private readonly ChessQuestStrategicOrchestrationState _state;
    private readonly Func<RunRequest, IWorkflowPlanner> _plannerFactory;
    private readonly Func<ChessQuestPhaseTracker, IEventSink> _eventSinkFactory;
    private readonly Action<ChessQuestStrategyProjection, ChessQuestPhaseReport>? _phaseCompleted;
    private readonly int _defaultMaxAgentTurns;
    private readonly ExecutionPolicy _policy;

    public ChessQuestPhaseRunExecutor(
        ChessQuestStrategicOrchestrationState state,
        Func<RunRequest, IWorkflowPlanner> plannerFactory,
        Func<ChessQuestPhaseTracker, IEventSink> eventSinkFactory,
        int defaultMaxAgentTurns,
        ExecutionPolicy policy,
        Action<ChessQuestStrategyProjection, ChessQuestPhaseReport>? phaseCompleted = null)
    {
        _state = state;
        _plannerFactory = plannerFactory;
        _eventSinkFactory = eventSinkFactory;
        _defaultMaxAgentTurns = defaultMaxAgentTurns;
        _policy = policy;
        _phaseCompleted = phaseCompleted;
    }

    public async Task<OutcomeEnvelope> RunAsync(
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        var taskNumber = _state.PhaseReports.Count + 1;
        var requestContext = request.Context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var envelope = ChessQuestPhaseTaskEnvelope.FromContext(
            requestContext,
            _defaultMaxAgentTurns,
            taskNumber);
        var phaseSanityWarnings = ChessQuestPhaseSanityPolicy.Evaluate(
            envelope,
            _state.LatestPhaseReport);
        var projection = envelope.ToProjection(_state.Session);
        _state.LatestTaskEnvelope = envelope;
        _state.StrategyProjections.Add(projection);

        Console.WriteLine();
        Console.WriteLine("=== ChessQuest Orchestration Tier | Dispatch Phase Task ===");
        Console.WriteLine($"Task: {requestContext.GetValueOrDefault("orchestration.taskId") ?? $"phase_{taskNumber:000}"}");
        Console.WriteLine($"Direction: {envelope.TaskDirection}");
        Console.WriteLine($"Rationale: {envelope.PublicRationale}");
        Console.WriteLine($"Envelope: phase={envelope.Phase} maxAgentTurns={envelope.MaxAgentTurns}");
        PrintList("Objectives", envelope.ActiveObjectives);
        PrintList("Success signals", envelope.SuccessSignals);
        PrintList("Claim discipline", envelope.ClaimDiscipline);
        PrintList("Phase sanity warnings", phaseSanityWarnings);

        var phaseTracker = ChessQuestPhaseTracker.Create(
            _state.Session,
            envelope.Phase,
            envelope.MaxAgentTurns,
            projection,
            envelope.PhaseGoal,
            envelope.ReplanTriggers);
        var runContext = new Dictionary<string, object?>(requestContext, StringComparer.Ordinal);
        var plannerContext = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(
            _state.Session,
            phaseTracker,
            _state.LatestPhaseReport);
        foreach (var pair in plannerContext)
        {
            runContext[pair.Key] = pair.Value;
        }

        runContext["chessquest.phaseSanityWarnings"] = phaseSanityWarnings;
        var goalSpine = (ChessQuestGoalSpine)plannerContext["goalSpine"]!;

        var phaseObjective = BuildPhaseObjective(
            request.Objective,
            envelope,
            projection,
            goalSpine,
            _state.Session.Scenario.DisclosurePolicy.AllowAttackInspection);
        var phaseRequest = new RunRequest(phaseObjective, request.Origin, runContext);
        var runner = new AgenticaRunner(
            _plannerFactory(phaseRequest),
            ChessQuestTools.CreateCatalog(_state.Session),
            _eventSinkFactory(phaseTracker),
            new ChessQuestPhaseOutcomeReporter(_state.Session, phaseTracker),
            _policy,
            completionEvaluator: new ChessQuestPhaseCompletionEvaluator(_state.Session, phaseTracker),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(_state.Session, phaseTracker, _state.LatestPhaseReport));

        Console.WriteLine();
        Console.WriteLine("=== ChessQuest Active Run Tier | Phase Execution ===");
        Console.WriteLine($"Phase: {envelope.Phase}; Direction: {envelope.TaskDirection}");
        Console.WriteLine($"Goal: {envelope.PhaseGoal}");
        Console.WriteLine($"Start FEN: {_state.Session.CurrentState.Fen}");
        Console.WriteLine();

        var outcome = await runner.RunAsync(phaseRequest, cancellationToken).ConfigureAwait(false);
        var report = phaseTracker.BuildReport(_state.Session);
        _state.PhaseReports.Add(report);
        _phaseCompleted?.Invoke(projection, report);

        Console.WriteLine();
        Console.WriteLine("=== ChessQuest Orchestration Tier | Phase Report ===");
        Console.WriteLine($"Phase: {report.Phase}");
        Console.WriteLine($"Status: {report.Status}");
        Console.WriteLine($"Stop Reason: {report.StopReason}");
        Console.WriteLine($"Agent Moves: {(report.AgentMoves.Count == 0 ? "none" : string.Join(", ", report.AgentMoves))}");
        Console.WriteLine($"Opponent Moves: {(report.OpponentMoves.Count == 0 ? "none" : string.Join(", ", report.OpponentMoves))}");
        Console.WriteLine($"Material Delta: {report.MaterialBalanceDelta}");
        Console.WriteLine($"Terminal: {report.Terminal}");

        return AppendPhaseReportArtifact(outcome, report);
    }

    private static string BuildPhaseObjective(
        string taskObjective,
        ChessQuestPhaseTaskEnvelope task,
        ChessQuestStrategyProjection projection,
        ChessQuestGoalSpine goalSpine,
        bool attackInspectionAllowed)
    {
        var doctrine = ChessQuestGoalShapingPolicy.StaticDoctrine;
        var attackInspectionContract = attackInspectionAllowed
            ? "- chess.inspect_attacks provides neutral public opponent-capture facts only; it does not score, choose, or prove a response is safe."
            : string.Empty;
        return
        $"""
        {taskObjective}

        ChessQuest orchestration task envelope:
        - Phase: {task.Phase}
        - Direction: {task.TaskDirection}
        - Public rationale: {task.PublicRationale}
        - Phase goal: {task.PhaseGoal}
        - Max agent turns: {task.MaxAgentTurns}
        - Strategy projection: {projection.StrategyName}
        - Strategy intent: {projection.StrategyIntent}
        - Active objectives:
        {FormatBulletBlock(task.ActiveObjectives)}
        - Success signals:
        {FormatBulletBlock(task.SuccessSignals)}
        - Claim discipline:
        {FormatBulletBlock(task.ClaimDiscipline)}

        Playing doctrine:
        - {doctrine.Summary}
        - Good play criteria:
        {FormatBulletBlock(doctrine.GoodPlayCriteria)}
        - Evidence discipline:
        {FormatBulletBlock(doctrine.EvidenceDiscipline)}

        GoalSpine continuity:
        - Current reality: {goalSpine.CurrentReality}
        - Active priority: {goalSpine.ActivePriority}
        - Known divergence: {goalSpine.KnownDivergence ?? "none"}
        - Next decision pressure: {goalSpine.NextDecisionPressure}
        - GoalSpine is compact continuity from receipts/state; it is not proof, not a hidden solution, and not move-level guidance.

        Active run contract:
        - You choose legal chess moves through the ChessQuest strict referee tools.
        - The orchestration tier chose this phase envelope; it did not choose a move.
        - Board truth, legal receipts, and chess.project_line verification override strategy claims.
        - Legal moves are affordances only; legal does not mean good or safe.
        - A one-ply chess.project_line result proves only the submitted move's public-rule projection.
        {attackInspectionContract}
        - chess.play_move turnIntent should include goal, evidence, hypothesis, riskCheck, claimLevel, and publicReason.
        """;
    }

    private static string FormatBulletBlock(IReadOnlyList<string> values) =>
        values.Count == 0
            ? "  - none"
            : string.Join(Environment.NewLine, values.Select(value => $"  - {value}"));

    private static void PrintList(string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{label}:");
        foreach (var value in values)
        {
            Console.WriteLine($"  - {value}");
        }
    }

    private static OutcomeEnvelope AppendPhaseReportArtifact(
        OutcomeEnvelope outcome,
        ChessQuestPhaseReport report)
    {
        var artifact = new Artifact(
            ArtifactId: AgenticaIds.New("artifact"),
            Kind: "chessquest.phase_report",
            Payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["report"] = report,
                ["phase"] = report.Phase,
                ["status"] = report.Status,
                ["stopReason"] = report.StopReason,
                ["agentTurnsPlayed"] = report.AgentTurnsPlayed,
                ["materialDelta"] = report.MaterialBalanceDelta,
                ["terminal"] = report.Terminal
            },
            Evidence: report.Evidence
                .Select(evidence => new EvidenceRef("phaseEvidence", evidence))
                .ToArray());

        return outcome with
        {
            Details = outcome.Details with
            {
                Artifacts = outcome.Details.Artifacts.Append(artifact).ToArray()
            },
            Outcome = outcome.Outcome with
            {
                CompletionEvidence = outcome.Outcome.CompletionEvidence
                    .Append(new EvidenceRef("artifact", artifact.ArtifactId))
                    .ToArray()
            }
        };
    }
}

public sealed class ChessQuestTaskAcceptanceEvaluator : ITaskAcceptanceEvaluator
{
    private readonly ChessQuestStrategicOrchestrationState _state;
    private readonly int _targetPhaseTasks;

    public ChessQuestTaskAcceptanceEvaluator(
        ChessQuestStrategicOrchestrationState state,
        int targetPhaseTasks)
    {
        _state = state;
        _targetPhaseTasks = Math.Max(1, targetPhaseTasks);
    }

    public Task<TaskAcceptanceResult> EvaluateAsync(
        TaskNode task,
        OutcomeEnvelope outcome,
        TaskAcceptanceContext context,
        CancellationToken cancellationToken = default)
    {
        var report = _state.LatestPhaseReport;
        if (report is null)
        {
            return Task.FromResult(new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                ["ChessQuest phase task did not produce a phase report."],
                []));
        }

        var terminal = _state.Session.CurrentState.IsTerminal;
        var agentWon = _state.Session.CurrentState.TerminalState?.Winner == _state.Session.Scenario.AgentColor;
        if (terminal && !agentWon)
        {
            return Task.FromResult(new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                ["ChessQuest reached a terminal state without an agent win."],
                Evidence(report)));
        }

        if (outcome.Outcome.Status != RunOutcomeStatus.Succeeded)
        {
            return Task.FromResult(new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                [$"Phase run outcome was {outcome.Outcome.Status}."],
                Evidence(report),
                RequiresGraphRefinement: true));
        }

        var shouldRefine = !terminal && _state.PhaseReports.Count < _targetPhaseTasks;
        return Task.FromResult(new TaskAcceptanceResult(
            TaskAcceptanceStatus.Accepted,
            [],
            Evidence(report),
            RequiresGraphRefinement: shouldRefine));
    }

    private static IReadOnlyList<EvidenceRef> Evidence(ChessQuestPhaseReport report) =>
        report.Evidence
            .Select(item => new EvidenceRef("phaseEvidence", item))
            .ToArray();
}

public sealed class ChessQuestDeterministicTaskPlanner : ITaskPlanner
{
    private readonly int _maxAgentTurns;

    public ChessQuestDeterministicTaskPlanner(int maxAgentTurns)
    {
        _maxAgentTurns = Math.Max(1, maxAgentTurns);
    }

    public Task<TaskGraphPlan> CreatePlanAsync(
        TaskPlanningRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TaskGraphPlan(
            "chessquest_task_plan_001",
            request.Request.Objective,
            [PhaseTask(1, "opening", "continue_development", "Start by developing pieces and contesting the center.")],
            [],
            DateTimeOffset.UtcNow));

    public Task<TaskGraphRefinement> RefinePlanAsync(
        TaskRefinementRequest request,
        CancellationToken cancellationToken = default)
    {
        var nextIndex = request.State.CompletedTaskIds.Count + 1;
        var host = request.WorkingContext.HostStateProjection;
        var phase = ChoosePhase(host);
        var direction = phase switch
        {
            "tactical" => "verify_tactical_opportunity",
            "conversion" => "convert_advantage",
            "defense" => "stabilize_position",
            _ => "continue_development"
        };
        var rationale = $"Continue with {phase} phase after receipt-backed phase report {nextIndex - 1}.";
        var previousTaskId = request.State.CompletedTaskIds.LastOrDefault() ?? request.ActiveTask.TaskId;
        var task = PhaseTask(nextIndex, phase, direction, rationale, previousTaskId);

        return Task.FromResult(new TaskGraphRefinement(
            $"add_{phase}_phase_{nextIndex:000}",
            [new TaskGraphMutation(TaskGraphMutationKind.AddTask, task.TaskId, Task: task)],
            [],
            RequiresUserInput: false));
    }

    private TaskNode PhaseTask(
        int index,
        string phase,
        string direction,
        string rationale,
        string? previousTaskId = null) =>
        new(
            TaskId: $"chessquest_phase_{index:000}",
            Objective: $"Execute ChessQuest {phase} phase task {index} under a bounded strategic envelope.",
            DependsOn: index <= 1 ? [] : [previousTaskId ?? $"chessquest_phase_{index - 1:000}"],
            Optional: false,
            Priority: index,
            MaxRuns: 1,
            ContextProjection: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chessquest.taskKind"] = "phase_run",
                ["chessquest.phase"] = phase,
                ["chessquest.taskDirection"] = direction,
                ["chessquest.publicRationale"] = rationale,
                ["chessquest.phaseGoal"] = PhaseGoal(phase),
                ["chessquest.activeObjectives"] = PhaseObjectives(phase),
                ["chessquest.successSignals"] = PhaseSuccessSignals(phase),
                ["chessquest.claimDiscipline"] = PhaseClaimDiscipline(phase),
                ["chessquest.maxAgentTurns"] = _maxAgentTurns,
                ["chessquest.replanTriggers"] = new[]
                {
                    "terminal game state",
                    "agent king in check",
                    "major material swing",
                    "checkmate claim appears",
                    "phase goal no longer fits legal board state"
                }
            },
            AcceptanceRequirements:
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.Artifact,
                    ArtifactKind: "chessquest.phase_report")
            ]);

    private static string ChoosePhase(IReadOnlyDictionary<string, object?> host)
    {
        if (TryBool(host, "chessquest.terminal"))
        {
            return "terminal";
        }

        var materialDelta = TryInt(host, "chessquest.latestPhaseReport.materialDelta") ?? 0;
        return materialDelta switch
        {
            >= 3 => "conversion",
            <= -3 => "defense",
            _ => "opening"
        };
    }

    private static string PhaseGoal(string phase) =>
        phase switch
        {
            "tactical" => "Verify forcing ideas and convert only tactics supported by public projection.",
            "conversion" => "Convert a material or positional advantage toward a win while avoiding draw outcomes.",
            "defense" => "Stabilize the position, preserve the king, and recover from material or tactical pressure.",
            _ => "Develop pieces, contest central squares, improve king safety, and avoid immediate material loss."
        };

    private static string[] PhaseObjectives(string phase) =>
        phase switch
        {
            "tactical" =>
            [
                "verify candidate tactics with public projections",
                "model opponent replies before claiming safety",
                "avoid material loss without compensation"
            ],
            "conversion" =>
            [
                "convert advantage only when evidence supports advantage",
                "preserve material",
                "avoid draw outcomes"
            ],
            "defense" =>
            [
                "preserve king safety",
                "avoid further material loss",
                "seek counterplay only after checking public consequences"
            ],
            _ =>
            [
                "develop pieces",
                "contest central squares",
                "improve king safety"
            ]
        };

    private static string[] PhaseSuccessSignals(string phase) =>
        phase switch
        {
            "tactical" =>
            [
                "opponent replies are modeled before safety claims",
                "material or terminal claims are evidence-backed",
                "phase does not worsen material balance"
            ],
            "defense" =>
            [
                "king is not in check after opponent reply",
                "material balance does not worsen",
                "immediate threats are reduced or acknowledged"
            ],
            "conversion" =>
            [
                "conversion condition is evidence-backed",
                "simplification does not worsen the position",
                "terminal claims are verifier-backed"
            ],
            _ =>
            [
                "legal move committed",
                "king safety improves or remains stable",
                "piece activity improves without unsupported claims"
            ]
        };

    private static string[] PhaseClaimDiscipline(string phase) =>
        phase switch
        {
            "tactical" =>
            [
                "legal projection does not prove safety",
                "material claims require projected line or receipt evidence",
                "treat tactical ideas as hypotheses until opponent replies are modeled"
            ],
            "conversion" =>
            [
                "conversion claims require real material, positional, promotion, or terminal evidence",
                "do not claim simplification helps unless evidence supports it",
                "do not claim checkmate unless terminal state is verified"
            ],
            _ =>
            [
                "state what is verified, hypothesized, and unmodeled",
                "legal does not mean safe",
                "do not claim checkmate unless terminal state is verified"
            ]
        };

    private static bool TryBool(
        IReadOnlyDictionary<string, object?> host,
        string key) =>
        host.TryGetValue(key, out var value) && value is bool boolValue && boolValue;

    private static int? TryInt(
        IReadOnlyDictionary<string, object?> host,
        string key)
    {
        if (!host.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }
}

public sealed class ChessQuestConsoleTaskPlanner : ITaskPlanner
{
    private readonly ITaskPlanner _inner;
    private readonly ITaskPlanner? _fallback;

    public ChessQuestConsoleTaskPlanner(
        ITaskPlanner inner,
        ITaskPlanner? fallback = null)
    {
        _inner = inner;
        _fallback = fallback;
    }

    public async Task<TaskGraphPlan> CreatePlanAsync(
        TaskPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await RunWithFallbackAsync(
                () => _inner.CreatePlanAsync(request, cancellationToken),
                () => _fallback?.CreatePlanAsync(request, cancellationToken),
                "initial task plan")
            .ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("=== ChessQuest Orchestration Tier | Task Plan ===");
        foreach (var task in plan.Tasks.OrderBy(task => task.Priority))
        {
            PrintTask(task);
        }

        return plan;
    }

    public async Task<TaskGraphRefinement> RefinePlanAsync(
        TaskRefinementRequest request,
        CancellationToken cancellationToken = default)
    {
        var refinement = await RunWithFallbackAsync(
                () => _inner.RefinePlanAsync(request, cancellationToken),
                () => _fallback?.RefinePlanAsync(request, cancellationToken),
                "task refinement")
            .ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("=== ChessQuest Orchestration Tier | Refinement ===");
        Console.WriteLine($"Reason: {refinement.Reason}");
        if (refinement.Mutations.Count == 0)
        {
            Console.WriteLine("Mutations: none");
        }
        else
        {
            foreach (var mutation in refinement.Mutations)
            {
                Console.WriteLine($"Mutation: {mutation.Kind} task={mutation.TaskId}");
                if (mutation.Task is not null)
                {
                    PrintTask(mutation.Task);
                }
            }
        }

        if (refinement.Blockers.Count > 0)
        {
            Console.WriteLine($"Blockers: {string.Join("; ", refinement.Blockers)}");
        }

        return refinement;
    }

    private static void PrintTask(TaskNode task)
    {
        Console.WriteLine($"Task: {task.TaskId}");
        Console.WriteLine($"  Objective: {task.Objective}");
        Console.WriteLine($"  Priority: {task.Priority}; MaxRuns: {task.MaxRuns}");
        Console.WriteLine($"  Phase: {Read(task, "chessquest.phase") ?? Read(task, "phase") ?? "unspecified"}");
        Console.WriteLine($"  Direction: {Read(task, "chessquest.taskDirection") ?? Read(task, "taskDirection") ?? "unspecified"}");
        var rationale = Read(task, "chessquest.publicRationale") ?? Read(task, "publicRationale");
        if (!string.IsNullOrWhiteSpace(rationale))
        {
            Console.WriteLine($"  Rationale: {rationale}");
        }

        PrintTaskList(task, "Objectives", "chessquest.activeObjectives");
        PrintTaskList(task, "Success", "chessquest.successSignals");
        PrintTaskList(task, "Claim discipline", "chessquest.claimDiscipline");
    }

    private static string? Read(TaskNode task, string key) =>
        task.ContextProjection.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;

    private static void PrintTaskList(TaskNode task, string label, string key)
    {
        if (!task.ContextProjection.TryGetValue(key, out var value) || value is null)
        {
            return;
        }

        var items = value switch
        {
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object> objects => objects
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray(),
            _ => []
        };

        if (items.Length == 0)
        {
            return;
        }

        Console.WriteLine($"  {label}:");
        foreach (var item in items)
        {
            Console.WriteLine($"    - {item}");
        }
    }

    private static async Task<T> RunWithFallbackAsync<T>(
        Func<Task<T>> action,
        Func<Task<T>?> fallback,
        string operation)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverablePlannerFailure(exception))
        {
            var fallbackTask = fallback();
            if (fallbackTask is null)
            {
                throw;
            }

            Console.WriteLine();
            Console.WriteLine("=== ChessQuest Orchestration Tier | Planner Repair ===");
            Console.WriteLine($"Recovered from invalid {operation}: {exception.Message}");
            Console.WriteLine("Using deterministic ChessQuest phase task fallback for this orchestration step.");
            return await fallbackTask.ConfigureAwait(false);
        }
    }

    private static bool IsRecoverablePlannerFailure(Exception exception) =>
        exception is LlmTaskPlannerException or TaskGraphValidationException;
}

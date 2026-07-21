using Agentica.Artifacts;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Orchestration;
using Agentica.Observations;
using Agentica.Orchestration;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class HiddenWorldOrchestrationHarnessTests
{
    private const string PlannerModeVariable = "AGENTICA_HIDDEN_WORLD_PLANNER";

    [Fact]
    public async Task Hidden_world_orchestration_completes_with_receipt_backed_unlocks()
    {
        LoadSolutionRootEnvironmentFile();

        var world = HiddenWorld.CreateSeeded(seed: 42);
        var planner = CreateTaskPlanner(world);
        var executor = new HiddenWorldRunExecutor(world);
        var orchestrator = new TaskOrchestrator(
            planner,
            executor,
            new HiddenWorldAcceptanceEvaluator(world),
            new DeterministicWorkContextCompiler(),
            world.PublicProjection,
            new OrchestrationPolicy(MaxRuns: 12, MaxRefinements: 8, MaxGraphMutationsPerRefinement: 8));

        var outcome = await orchestrator.RunAsync(new LargeTaskRequest(
            "Save the kingdom by resolving the sealed castle crisis.",
            RequestOrigin.User,
            new Dictionary<string, object?>
            {
                ["hiddenWorld.publicProjection"] = world.PublicProjection()
            }));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.True(world.WorldSaved);
        Assert.Equal(
            world.ExpectedCompletionOrder.Select(taskId => $"expedition_{taskId}"),
            outcome.State.CompletedTaskIds);
        Assert.Empty(executor.LockedEarlyAttempts);
        Assert.Contains(outcome.EvidenceRefs, evidence => evidence.Kind == "artifact");
    }

    [Fact]
    public void Hidden_world_public_projection_does_not_leak_hidden_unlocks()
    {
        var world = HiddenWorld.CreateSeeded(seed: 42);
        var projection = world.PublicProjection();
        var text = string.Join(
            ' ',
            projection.Select(pair => $"{pair.Key}:{pair.Value}"));

        foreach (var capability in world.HiddenCapabilityNames)
        {
            Assert.DoesNotContain(capability, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Hidden_world_is_seed_deterministic()
    {
        var first = HiddenWorld.CreateSeeded(seed: 42);
        var second = HiddenWorld.CreateSeeded(seed: 42);

        Assert.Equal(first.PublicProjection(), second.PublicProjection());
        Assert.Equal(first.HiddenTaskOrder, second.HiddenTaskOrder);
        Assert.Equal(first.HiddenCapabilityNames, second.HiddenCapabilityNames);
    }

    [Fact]
    public void Hidden_world_seed_changes_generated_world_surface()
    {
        var first = HiddenWorld.CreateSeeded(seed: 42);
        var second = HiddenWorld.CreateSeeded(seed: 99);

        Assert.NotEqual(first.HiddenTaskOrder, second.HiddenTaskOrder);
        Assert.NotEqual(first.PublicProjection(), second.PublicProjection());
    }

    [Fact]
    public void Hidden_world_public_projection_exposes_locations_not_task_objectives()
    {
        var world = HiddenWorld.CreateSeeded(seed: 42);
        var projection = world.PublicProjection();

        Assert.True(projection.ContainsKey("publicLocations"));
        Assert.True(projection.ContainsKey("capabilities"));
        Assert.False(projection.ContainsKey("visibleTasks"));

        var text = string.Join(' ', projection.Select(pair => $"{pair.Key}:{pair.Value}"));
        foreach (var taskId in world.HiddenTaskOrder)
        {
            Assert.DoesNotContain($"Complete {taskId}", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ITaskPlanner CreateTaskPlanner(HiddenWorld world)
    {
        var mode = Environment.GetEnvironmentVariable(PlannerModeVariable);
        if (!string.Equals(mode, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            return new DeterministicHiddenWorldTaskPlanner(world);
        }

        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        Assert.False(
            string.IsNullOrWhiteSpace(apiKey),
            $"{PlannerModeVariable}=gemini but no Gemini API key was found. Set GEMINI_API_KEY or GOOGLE_API_KEY.");

        var client = new GeminiLlmClient(new GeminiClientOptions(
            ApiKey: apiKey,
            DefaultModelId: GeminiModelId.Flash25,
            UseVertexAi: false));
        return new LlmTaskPlanner(
            new RetryingLlmClient(client, new LlmRetryOptions(CallTimeout: TimeSpan.FromMinutes(10))),
            new LlmTaskPlannerOptions(
                GeminiModelId.Flash25,
                new LlmGenerationOptions(
                    Temperature: 0,
                    MaxOutputTokens: 4096,
                    Thinking: LlmThinkingOptions.Off())));
    }

    private sealed class DeterministicHiddenWorldTaskPlanner : ITaskPlanner
    {
        private readonly HiddenWorld _world;

        public DeterministicHiddenWorldTaskPlanner(HiddenWorld world)
        {
            _world = world;
        }

        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskGraphPlan(
                "hidden_world_plan",
                request.Request.Objective,
                VisibleTasks().ToArray(),
                [
                    new TaskAcceptanceRequirement(
                        TaskAcceptanceRequirementKind.OutcomeStatus,
                        RunOutcomeStatus.Succeeded)
                ],
                DateTimeOffset.UtcNow));

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default)
        {
            var existing = request.CurrentPlan.Tasks
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal);
            var mutations = VisibleTasks()
                .Where(task => !existing.Contains(task.TaskId))
                .Select(task => new TaskGraphMutation(TaskGraphMutationKind.AddTask, task.TaskId, Task: task))
                .ToArray();

            return Task.FromResult(new TaskGraphRefinement(
                "receipt_backed_unlocks_changed_public_frontier",
                mutations,
                [],
                RequiresUserInput: false));
        }

        private IEnumerable<TaskNode> VisibleTasks() =>
            _world.ReachableLocations()
                .Select((location, index) => new TaskNode(
                    $"expedition_{location.LocationId}",
                    $"Undertake one bounded expedition at {location.PublicName} to look for useful evidence, routes, or proof.",
                    [],
                    Optional: false,
                    Priority: index + 1,
                    MaxRuns: 1,
                    new Dictionary<string, object?>
                    {
                        ["hiddenWorld.capabilityId"] = "undertake_expedition",
                        ["hiddenWorld.locationId"] = location.LocationId
                    },
                    [
                        new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded),
                        new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "hidden_world.task_completed")
                    ]));
    }

    private sealed class HiddenWorldRunExecutor : IRunExecutor
    {
        private readonly HiddenWorld _world;

        public HiddenWorldRunExecutor(HiddenWorld world)
        {
            _world = world;
        }

        public List<string> LockedEarlyAttempts { get; } = [];

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default)
        {
            var taskId = request.Context?.TryGetValue("hiddenWorld.taskId", out var value) == true
                ? value?.ToString()
                : null;
            if (string.IsNullOrWhiteSpace(taskId) &&
                request.Context?.TryGetValue("hiddenWorld.locationId", out var locationValue) == true)
            {
                taskId = locationValue?.ToString();
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                taskId = _world.MatchTaskId(request.Objective);
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                return Task.FromResult(Envelope(
                    "unknown",
                    RunOutcomeStatus.Blocked,
                    StopReason.ToolUnavailable,
                    "Could not map run objective to a hidden-world task.",
                    []));
            }

            var result = _world.Execute(taskId);
            if (!result.Accepted)
            {
                LockedEarlyAttempts.Add(taskId);
                return Task.FromResult(Envelope(
                    taskId,
                    RunOutcomeStatus.Blocked,
                    StopReason.ToolUnavailable,
                    result.Message,
                    []));
            }

            return Task.FromResult(Envelope(
                taskId,
                RunOutcomeStatus.Succeeded,
                StopReason.Complete,
                result.Message,
                result.Unlocked));
        }

        private static OutcomeEnvelope Envelope(
            string taskId,
            RunOutcomeStatus status,
            StopReason stopReason,
            string message,
            IReadOnlyList<string> unlocked)
        {
            var runId = AgenticaIds.New("run");
            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                $"step_{taskId}",
                "hidden_world.execute_task",
                status == RunOutcomeStatus.Succeeded ? ReceiptStatus.Succeeded : ReceiptStatus.Unavailable,
                message,
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["unlocked"] = unlocked
                });
            var artifact = status == RunOutcomeStatus.Succeeded
                ? new Artifact(
                    AgenticaIds.New("artifact"),
                    "hidden_world.task_completed",
                    new Dictionary<string, object?>
                    {
                        ["taskId"] = taskId,
                        ["unlocked"] = unlocked
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])
                : null;

            return new OutcomeEnvelope(
                new RunOutcome(
                    runId,
                    status,
                    stopReason,
                    status == RunOutcomeStatus.Succeeded ? [$"step_{taskId}"] : [],
                    status == RunOutcomeStatus.Succeeded ? [] : [message],
                    artifact is null ? [] : [new EvidenceRef("artifact", artifact.ArtifactId)]),
                new OutcomeReport($"report_{runId}", message, []),
                new ReceiptEnvelope([receipt]),
                new DetailEnvelope(
                    new RunRequest($"Complete hidden-world task {taskId}."),
                    [],
                    [],
                    [],
                    artifact is null ? [] : [artifact],
                    [],
                    [],
                    []));
        }
    }

    private sealed class HiddenWorldAcceptanceEvaluator : ITaskAcceptanceEvaluator
    {
        private readonly HiddenWorld _world;

        public HiddenWorldAcceptanceEvaluator(HiddenWorld world)
        {
            _world = world;
        }

        public Task<TaskAcceptanceResult> EvaluateAsync(
            TaskNode task,
            OutcomeEnvelope outcome,
            TaskAcceptanceContext context,
            CancellationToken cancellationToken = default)
        {
            if (outcome.Outcome.Status != RunOutcomeStatus.Succeeded)
            {
                return Task.FromResult(new TaskAcceptanceResult(
                    TaskAcceptanceStatus.Blocked,
                    outcome.Outcome.Blockers,
                    []));
            }

            var evidence = outcome.Details.Artifacts
                .Select(artifact => new EvidenceRef("artifact", artifact.ArtifactId))
                .Concat(outcome.Receipts.Items.Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId)))
                .ToArray();

            return Task.FromResult(new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                evidence,
                RequiresGraphRefinement: _world.HasNewVisibleTasks(context.Plan.Tasks.Select(item => item.TaskId))));
        }
    }

    private sealed class HiddenWorld
    {
        private readonly Dictionary<string, HiddenWorldTask> _tasks;
        private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
        private readonly string _finalTaskId;
        private readonly string _publicInitialState;

        private HiddenWorld(
            IReadOnlyList<HiddenWorldTask> tasks,
            string finalTaskId,
            string publicInitialState,
            IReadOnlyList<string> hiddenCapabilityNames,
            IReadOnlyList<string> expectedCompletionOrder)
        {
            _tasks = tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
            _finalTaskId = finalTaskId;
            _publicInitialState = publicInitialState;
            HiddenTaskOrder = tasks.Select(task => task.TaskId).ToArray();
            HiddenCapabilityNames = hiddenCapabilityNames;
            ExpectedCompletionOrder = expectedCompletionOrder;
        }

        public IReadOnlyList<string> HiddenTaskOrder { get; }

        public IReadOnlyList<string> HiddenCapabilityNames { get; }

        public IReadOnlyList<string> ExpectedCompletionOrder { get; }

        public bool WorldSaved => _completed.Contains(_finalTaskId);

        public static HiddenWorld CreateSeeded(int seed)
        {
            var random = new Random(seed);
            var startingSites = PickTwo(random,
            [
                new PublicLocation("forest_shrine", "forest shrine"),
                new PublicLocation("sunken_archive", "sunken archive"),
                new PublicLocation("old_watchtower", "old watchtower"),
                new PublicLocation("mirror_grove", "mirror grove")
            ]);
            var chainSites = PickTwo(random,
            [
                new PublicLocation("mine_depths", "mine depths"),
                new PublicLocation("storm_forge", "storm forge"),
                new PublicLocation("crystal_cistern", "crystal cistern"),
                new PublicLocation("ashen_lift", "ashen lift")
            ]);
            var finalSite = PickOne(random,
            [
                new PublicLocation("castle_gate", "sealed castle gate"),
                new PublicLocation("sky_bridge", "sealed sky bridge"),
                new PublicLocation("royal_vault", "sealed royal vault")
            ]);
            var traversal = PickOne(random, ["hookshot", "sun_lantern", "wind_boots", "echo_key"]);
            var material = PickOne(random, ["master_ore", "star_ember", "moon_glass", "storm_core"]);
            var seal = PickOne(random, ["water_sigil", "dawn_sigil", "mirror_sigil", "root_sigil"]);
            var forgedSeal = PickOne(random, ["fire_sigil", "forge_sigil", "crown_sigil", "thunder_sigil"]);
            var worldSaved = $"world_saved_{seed}";
            var tasks = new[]
            {
                new HiddenWorldTask(
                    startingSites[0].LocationId,
                    startingSites[0],
                    [],
                    [traversal],
                    $"{startingSites[0].PublicName} stabilized a traversal route.",
                    null),
                new HiddenWorldTask(
                    startingSites[1].LocationId,
                    startingSites[1],
                    [],
                    [seal],
                    $"{startingSites[1].PublicName} yielded royal proof.",
                    null),
                new HiddenWorldTask(
                    chainSites[0].LocationId,
                    chainSites[0],
                    [traversal],
                    [material],
                    $"{chainSites[0].PublicName} yielded the missing forge material.",
                    "A traversal gap blocks this location."),
                new HiddenWorldTask(
                    chainSites[1].LocationId,
                    chainSites[1],
                    [material],
                    [forgedSeal],
                    $"{chainSites[1].PublicName} produced final royal proof.",
                    "The location is dormant until missing material is recovered."),
                new HiddenWorldTask(
                    finalSite.LocationId,
                    finalSite,
                    [seal, forgedSeal],
                    [worldSaved],
                    $"{finalSite.PublicName} opened and the kingdom was saved.",
                    "The final route is sealed until enough royal proof is recovered.")
            };

            return new HiddenWorld(
                tasks,
                finalSite.LocationId,
                $"The kingdom is blocked by the {finalSite.PublicName}. {startingSites[0].PublicName} and {startingSites[1].PublicName} are reachable. Other routes are visibly blocked but the missing unlocks are unknown.",
                [traversal, material, seal, forgedSeal, worldSaved],
                tasks.Select(task => task.TaskId).ToArray());
        }

        public IReadOnlyDictionary<string, object?> PublicProjection() =>
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["goal"] = "Save the kingdom by opening the final sealed route.",
                ["publicLocations"] = _tasks.Values
                    .Select(task => new Dictionary<string, object?>
                    {
                        ["locationId"] = task.Location.LocationId,
                        ["publicName"] = task.Location.PublicName,
                        ["state"] = _completed.Contains(task.TaskId)
                            ? "completed"
                            : task.RequiredCapabilities.All(_capabilities.Contains)
                                ? "reachable"
                                : "blocked",
                        ["knownBlocker"] = task.RequiredCapabilities.All(_capabilities.Contains)
                            ? null
                            : task.PublicBlocker
                    })
                    .ToArray(),
                ["capabilities"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["capabilityId"] = "undertake_expedition",
                        ["description"] = "Attempt one bounded expedition at a reachable location. The hidden oracle emits receipts and unlocks from the attempt."
                    },
                    new Dictionary<string, object?>
                    {
                        ["capabilityId"] = "review_receipts",
                        ["description"] = "Use prior receipts to update the reachable frontier before proposing more expeditions."
                    }
                },
                ["knownRules"] = new[]
                {
                    "Only reachable locations can be attempted.",
                    "Receipts may reveal items, routes, or proof.",
                    "The hidden oracle determines expedition results.",
                    "Create task nodes from public locations and capabilities; do not assume hidden unlocks."
                },
                ["completedTasks"] = _completed.ToArray(),
                ["publicState"] = _completed.Count == 0
                    ? _publicInitialState
                    : "Recent receipts changed the reachable frontier."
            };

        public IEnumerable<string> VisibleTaskIds() =>
            _tasks.Values
                .Where(task => !_completed.Contains(task.TaskId) && task.RequiredCapabilities.All(_capabilities.Contains))
                .Select(task => task.TaskId);

        public IEnumerable<PublicLocation> ReachableLocations() =>
            _tasks.Values
                .Where(task => !_completed.Contains(task.TaskId) && task.RequiredCapabilities.All(_capabilities.Contains))
                .Select(task => task.Location);

        public bool HasNewVisibleTasks(IEnumerable<string> knownTaskIds)
        {
            var known = knownTaskIds.ToHashSet(StringComparer.Ordinal);
            return ReachableLocations().Any(location => !known.Contains($"expedition_{location.LocationId}"));
        }

        public string PublicObjective(string taskId) => _tasks[taskId].PublicObjective;

        public string? MatchTaskId(string objective) =>
            _tasks.Keys.FirstOrDefault(taskId => objective.Contains(taskId, StringComparison.OrdinalIgnoreCase)) ??
            _tasks.Values.FirstOrDefault(task =>
                objective.Contains(task.Location.LocationId, StringComparison.OrdinalIgnoreCase) ||
                objective.Contains(task.Location.PublicName, StringComparison.OrdinalIgnoreCase))?.TaskId;

        public HiddenWorldExecutionResult Execute(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new HiddenWorldExecutionResult(false, $"Unknown task '{taskId}'.", []);
            }

            if (_completed.Contains(taskId))
            {
                return new HiddenWorldExecutionResult(true, $"Task '{taskId}' already completed.", []);
            }

            var missing = task.RequiredCapabilities
                .Where(capability => !_capabilities.Contains(capability))
                .ToArray();
            if (missing.Length > 0)
            {
                return new HiddenWorldExecutionResult(false, $"Task '{taskId}' is locked.", []);
            }

            _completed.Add(taskId);
            foreach (var capability in task.GrantsCapabilities)
            {
                _capabilities.Add(capability);
            }

            return new HiddenWorldExecutionResult(true, task.SuccessMessage, task.GrantsCapabilities);
        }
    }

    private sealed record HiddenWorldTask(
        string TaskId,
        PublicLocation Location,
        IReadOnlyList<string> RequiredCapabilities,
        IReadOnlyList<string> GrantsCapabilities,
        string SuccessMessage,
        string? PublicBlocker)
    {
        public string PublicObjective => $"Undertake one bounded expedition at {Location.PublicName}.";
    }

    private sealed record PublicLocation(
        string LocationId,
        string PublicName);

    private sealed record HiddenWorldExecutionResult(
        bool Accepted,
        string Message,
        IReadOnlyList<string> Unlocked);

    private static T PickOne<T>(Random random, IReadOnlyList<T> items) =>
        items[random.Next(items.Count)];

    private static IReadOnlyList<T> PickTwo<T>(Random random, IReadOnlyList<T> items)
    {
        var firstIndex = random.Next(items.Count);
        var secondIndex = random.Next(items.Count - 1);
        if (secondIndex >= firstIndex)
        {
            secondIndex++;
        }

        return [items[firstIndex], items[secondIndex]];
    }

    private static void LoadSolutionRootEnvironmentFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "Agentica.slnx");
            var envPath = Path.Combine(directory.FullName, ".env");
            if (File.Exists(solutionPath))
            {
                if (File.Exists(envPath))
                {
                    foreach (var rawLine in File.ReadAllLines(envPath))
                    {
                        var line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith('#'))
                        {
                            continue;
                        }

                        var separator = line.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        Environment.SetEnvironmentVariable(
                            line[..separator].Trim(),
                            line[(separator + 1)..].Trim().Trim('"', '\''));
                    }
                }

                return;
            }

            directory = directory.Parent;
        }
    }
}

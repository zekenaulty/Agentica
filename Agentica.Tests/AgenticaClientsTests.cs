using System.Reflection;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Clients.Gemini;
using Agentica.Clients.Images;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class AgenticaClientsTests
{
    [Fact]
    public void Runtime_project_does_not_reference_clients_project()
    {
        var references = typeof(AgenticaRunner).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("Agentica.Clients", references);
    }

    [Fact]
    public void Clients_project_references_runtime_project()
    {
        var references = typeof(LlmWorkflowPlanner).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.Contains("Agentica", references);
    }

    [Fact]
    public void Provider_neutral_llm_contracts_do_not_expose_google_sdk_types()
    {
        var llmTypes = typeof(ILlmClient).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == "Agentica.Clients.Llm")
            .ToArray();

        foreach (var type in llmTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.False(IsGoogleType(property.PropertyType), $"{type.Name}.{property.Name} exposes Google SDK type.");
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.False(IsGoogleType(method.ReturnType), $"{type.Name}.{method.Name} returns Google SDK type.");
                foreach (var parameter in method.GetParameters())
                {
                    Assert.False(IsGoogleType(parameter.ParameterType), $"{type.Name}.{method.Name} parameter {parameter.Name} exposes Google SDK type.");
                }
            }
        }
    }

    [Fact]
    public async Task Llm_workflow_planner_maps_valid_initial_json_into_workflow_plan()
    {
        var planner = new LlmWorkflowPlanner(new FakeLlmClient(PlanJson("query_state", "Query", "ReadOnly")));

        var plan = await planner.CreatePlanAsync(CreatePlanningRequest());

        Assert.Equal("plan_model", plan.PlanId);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("query_state", step.ToolId);
        Assert.Equal(ToolKind.Query, step.Kind);
        Assert.Equal(ToolEffect.ReadOnly, step.Effect);
        Assert.Equal("current_state", step.Input["query"]);
        Assert.NotNull(step.Intent);
        Assert.Equal("Invoke query_state.", step.Intent!.Action);
        Assert.Equal("Use the supplied tool.", step.Intent.Rationale);
        Assert.Null(step.Intent.ExpectedOutcome);
    }

    [Fact]
    public async Task Llm_workflow_planner_maps_explicit_intent()
    {
        var planner = new LlmWorkflowPlanner(new FakeLlmClient(PlanJsonWithIntent()));

        var plan = await planner.CreatePlanAsync(CreatePlanningRequest());

        var step = Assert.Single(plan.Steps);
        Assert.NotNull(step.Intent);
        Assert.Equal("Inspect the current state.", step.Intent!.Action);
        Assert.Equal("The public objective requires state before choosing an action.", step.Intent.Rationale);
        Assert.Equal("A read-only observation describing current state.", step.Intent.ExpectedOutcome);
    }

    [Fact]
    public async Task Llm_workflow_planner_maps_batch_and_dependency_fields()
    {
        var planner = new LlmWorkflowPlanner(new FakeLlmClient(BatchedPlanJson()));

        var plan = await planner.CreatePlanAsync(CreatePlanningRequest());

        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal("evidence", plan.Steps[0].BatchId);
        Assert.Equal("evidence", plan.Steps[1].BatchId);
        Assert.Empty(plan.Steps[0].DependsOn);
        Assert.Equal(["read_a", "read_b"], plan.Steps[2].DependsOn);
        Assert.Null(plan.Steps[2].BatchId);
    }

    [Fact]
    public async Task Llm_workflow_planner_prompt_includes_completed_step_dependency_context()
    {
        var client = new FakeLlmClient(PlanJson("query_state", "Query", "ReadOnly"));
        var planner = new LlmWorkflowPlanner(client);
        var request = CreatePlanningRequest() with
        {
            ExecutionContext = new PlanningExecutionContext(
                ["step_004"],
                [
                    new CompletedStepContext(
                        "step_004",
                        "hexquest.validate_patch",
                        "plan_002",
                        2,
                        "receipt_004",
                        nameof(ReceiptStatus.Succeeded),
                        "observation_004",
                        null)
                ],
                CurrentPlanId: "plan_002",
                PlanVersionCount: 2)
        };

        await planner.CreatePlanAsync(request);

        var prompt = string.Join(
            Environment.NewLine,
            client.Requests[0].Messages.Select(message => message.Content));
        Assert.Contains("executionContext", prompt);
        Assert.Contains("completedStepIds", prompt);
        Assert.Contains("step_004", prompt);
        Assert.Contains("same submitted plan slice", prompt);
        Assert.Contains("public execution intent", prompt);
        Assert.Contains("\"intent\"", prompt);
    }

    [Fact]
    public async Task Llm_workflow_planner_prompt_guides_context_expansion_batches_and_serializes_tool_context_hints()
    {
        var client = new FakeLlmClient(PlanJson("query_a", "Query", "ReadOnly"));
        var planner = new LlmWorkflowPlanner(client);
        var request = new PlanningRequest(
            new RunRequest("Gather context before action."),
            [
                new ToolDescriptor(
                    "query_a",
                    "Query A",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    Description: "Reads public state.",
                    ContextHint: new ToolContextHint(
                        Produces: "public state",
                        Complements: ["query_b"],
                        CanBatchWith: ["query_b"],
                        ShouldPrecede: ["write_action"])
                    {
                        UseWhen = "State is uncertain.",
                        NotEnoughWhen = "Risk is unknown."
                    },
                    Cooldown: new ToolCooldownPolicy(
                        PlanStepCount: 3,
                        ScopeInputKeys: ["topic"],
                        Reason: "State is stale until host state changes.",
                        ResetOnMutation: true)),
                new ToolDescriptor(
                    "query_b",
                    "Query B",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    Description: "Reads public risk."),
                new ToolDescriptor(
                    "write_action",
                    "Write Action",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState)
            ],
            [],
            [])
        {
            ToolSurface = new ToolSurfaceSnapshot(
                "surface_pressure_test",
                DateTimeOffset.UtcNow,
                [],
                PlanningExecutionContext.Empty,
                [],
                [],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["remainingStepBudget"] = 1,
                    ["remainingTimeoutMs"] = 4500,
                    ["timePressure"] = "critical",
                    ["runPressure"] = "critical",
                    ["recommendedPlanningPosture"] = "Use existing public context and choose one bounded action."
                })
        };

        await planner.CreatePlanAsync(request);

        var prompt = string.Join(
            Environment.NewLine,
            client.Requests[0].Messages.Select(message => message.Content));
        Assert.Contains("context-expansion batch", prompt);
        Assert.Contains("maxBatchSize/maxParallelism", prompt);
        Assert.Contains("missing public preconditions, not reassurance", prompt);
        Assert.Contains("prefer the action", prompt);
        Assert.Contains("bounded action", prompt);
        Assert.Contains("contextHint", prompt);
        Assert.Contains("canBatchWith", prompt);
        Assert.Contains("query_b", prompt);
        Assert.Contains("public state", prompt);
        Assert.Contains("cooldown", prompt);
        Assert.Contains("treat that cooldown as part of the execution surface", prompt);
        Assert.Contains("planStepCount", prompt);
        Assert.Contains("scopeInputKeys", prompt);
        Assert.Contains("State is stale until host state changes.", prompt);
        Assert.Contains("Current planning constraints", prompt);
        Assert.Contains("remainingStepBudget", prompt);
        Assert.Contains("timePressure", prompt);
        Assert.Contains("critical", prompt);
        Assert.Contains("Use existing public context and choose one bounded action.", prompt);
    }

    [Fact]
    public async Task Llm_workflow_planner_maps_valid_refinement_json_into_refined_plan()
    {
        var planner = new LlmWorkflowPlanner(new FakeLlmClient(RefinementJson(
            "perform_action",
            "Action",
            "WritesLocalState",
            PlanRefinementReasons.AmbiguousAction)));

        var refinedPlan = await planner.RefinePlanAsync(
            CreatePlanningRequest(),
            new Observation(
                "observation_001",
                "step_001",
                ObservationKind.StateQuery,
                "State is ready.",
                new Dictionary<string, object?>(),
                []));

        Assert.Equal("plan_refined", refinedPlan.PlanId);
        Assert.Equal(2, refinedPlan.Version);
        Assert.Equal(PlanRefinementReasons.AmbiguousAction, refinedPlan.PlanningReason);
        var step = Assert.Single(refinedPlan.Steps);
        Assert.Equal("perform_action", step.ToolId);
        Assert.Equal(ToolKind.Action, step.Kind);
        Assert.Equal(ToolEffect.WritesLocalState, step.Effect);
        Assert.Equal("Use the observation.", step.Reason);
        Assert.NotNull(step.Intent);
        Assert.Equal("Invoke perform_action.", step.Intent!.Action);
        Assert.Equal("Use the observation.", step.Intent.Rationale);
    }

    [Fact]
    public async Task Llm_workflow_planner_repairs_invalid_refinement_json_with_last_reply_context()
    {
        const string invalidJson = "{\"fromPlanId\":\"plan_model\",";
        var repairedJson = RefinementJson(
            "perform_action",
            "Action",
            "WritesLocalState",
            PlanRefinementReasons.RetryUnblock);
        var client = new FakeLlmClient(
            new LlmResponse("fake", "fake-model", invalidJson, invalidJson),
            new LlmResponse("fake", "fake-model", repairedJson, repairedJson));
        var planner = new LlmWorkflowPlanner(
            client,
            new LlmPlannerOptions(
                ModelId: "fake-model",
                InvalidJsonRepairAttempts: 1));

        var refinedPlan = await planner.RefinePlanAsync(
            CreatePlanningRequest(),
            new Observation(
                "observation_001",
                "step_001",
                ObservationKind.StateQuery,
                "State is ready.",
                new Dictionary<string, object?>(),
                []));

        Assert.Equal("plan_refined", refinedPlan.PlanId);
        Assert.Equal(PlanRefinementReasons.RetryUnblock, refinedPlan.PlanningReason);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(LlmMessageRole.Assistant, client.Requests[1].Messages[^2].Role);
        Assert.Contains(invalidJson, client.Requests[1].Messages[^2].Content, StringComparison.Ordinal);
        Assert.Contains("previous Agentica planning response could not be parsed", client.Requests[1].Messages[^1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner returned invalid refinement JSON", client.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("fromPlanId, reason, evidence, refinedPlan", client.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal("refinement", client.Requests[1].Metadata?["agentica.planner.repairKind"]);
        Assert.Equal("1", client.Requests[1].Metadata?["agentica.planner.repairAttempt"]);
    }

    [Fact]
    public async Task Invalid_model_json_fails_before_tool_execution()
    {
        var tool = new CountingTool("known_tool");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known_tool", "Known", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(new LlmWorkflowPlanner(new FakeLlmClient("{not-json")), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Invalid JSON test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "planner.create.failed");
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Max_tokens_model_json_failure_is_reported_as_truncation()
    {
        var tool = new CountingTool("known_tool");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known_tool", "Known", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(
            new LlmWorkflowPlanner(
                new FakeLlmClient(new LlmResponse(
                    "fake",
                    "fake-model",
                    "{not-json",
                    StructuredJson: "{not-json",
                    FinishReason: LlmFinishReason.MaxTokens)),
                new LlmPlannerOptions(InvalidJsonRepairAttempts: 0)),
            catalog);

        var envelope = await runner.RunAsync(new RunRequest("Truncated JSON test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        var issue = Assert.Single(envelope.Details.ValidationIssues, item => item.Code == "planner.create.failed");
        Assert.Contains("MaxTokens", issue.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Unknown_model_tool_id_fails_before_execution()
    {
        var tool = new CountingTool("known_tool");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known_tool", "Known", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(new LlmWorkflowPlanner(new FakeLlmClient(PlanJson("missing_tool", "Query", "ReadOnly"))), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Unknown model tool test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.unknown_tool");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Provider_unavailable_blocks_run_without_inventing_success()
    {
        var tool = new CountingTool("known_tool");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known_tool", "Known", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(
            new LlmWorkflowPlanner(new ThrowingLlmClient(new LlmClientException("fake", "Provider unavailable."))),
            catalog);

        var envelope = await runner.RunAsync(new RunRequest("Provider unavailable test"));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.PlannerUnavailable, envelope.Outcome.StopReason);
        Assert.NotEmpty(envelope.Outcome.Blockers);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Model_hidden_mutation_step_remains_subject_to_runtime_validation()
    {
        var tool = new CountingTool("write_tool");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("write_tool", "Write Tool", ToolKind.Action, ToolEffect.WritesLocalState),
            tool));
        var runner = CreateRunner(new LlmWorkflowPlanner(new FakeLlmClient(PlanJson("write_tool", "Query", "WritesLocalState"))), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Hidden mutation test"));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.kind_mismatch");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.mutation_hidden");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public void Gemini_thinking_options_map_to_budget_and_include_thoughts()
    {
        Assert.Equal(new GeminiThinkingConfigSnapshot(-1, true), GeminiThinkingOptionsMapper.Map(LlmThinkingOptions.Dynamic(includeThoughts: true)));
        Assert.Equal(new GeminiThinkingConfigSnapshot(0, false), GeminiThinkingOptionsMapper.Map(LlmThinkingOptions.Off()));
        Assert.Equal(new GeminiThinkingConfigSnapshot(4096, true), GeminiThinkingOptionsMapper.Map(LlmThinkingOptions.Budget(4096, includeThoughts: true)));
    }

    [Fact]
    public void Gemini_config_maps_structured_output_json_schema()
    {
        var config = GeminiLlmClient.CreateConfig(new LlmRequest(
            GeminiModelId.Flash25,
            [new LlmMessage(LlmMessageRole.User, "Return JSON.")],
            StructuredOutput: new LlmStructuredOutputOptions(
                JsonSchema: """
                {
                  "type": "object",
                  "properties": {
                    "status": {
                      "type": "string"
                    }
                  },
                  "required": ["status"]
                }
                """)));

        Assert.Equal("application/json", config.ResponseMimeType);
        var schema = Assert.IsType<JsonElement>(config.ResponseJsonSchema);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("status", out _));
    }

    [Fact]
    public void Gemini_image_config_maps_generation_options()
    {
        var config = GeminiImageGenerationClient.CreateConfig(new ImageGenerationRequest(
            GeminiModelId.FlashImage31Preview,
            "Create a cover illustration.",
            AspectRatio: "16:9",
            ImageSize: "2K",
            OutputMimeType: "image/jpeg",
            OutputCompressionQuality: 82));

        Assert.Equal(["TEXT", "IMAGE"], config.ResponseModalities);
        Assert.NotNull(config.ImageConfig);
        Assert.Equal("16:9", config.ImageConfig.AspectRatio);
        Assert.Equal("2K", config.ImageConfig.ImageSize);
        Assert.Equal("image/jpeg", config.ImageConfig.OutputMimeType);
        Assert.Equal(82, config.ImageConfig.OutputCompressionQuality);
    }

    [Fact]
    public void Gemini_config_rejects_invalid_structured_output_json_schema()
    {
        var exception = Assert.Throws<LlmClientException>(() =>
            GeminiLlmClient.CreateConfig(new LlmRequest(
                GeminiModelId.Flash25,
                [new LlmMessage(LlmMessageRole.User, "Return JSON.")],
                StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: "{not-json"))));

        Assert.Equal(LlmClientErrorKind.BadRequest, exception.ErrorKind);
        Assert.Equal("invalid_json_schema", exception.ErrorClass);
    }

    [Fact]
    public async Task Thought_summaries_are_diagnostics_not_planning_proof()
    {
        var fakeClient = new FakeLlmClient(
            new LlmResponse(
                ProviderName: "fake",
                ModelId: "fake-model",
                Text: PlanJson("query_state", "Query", "ReadOnly"),
                StructuredJson: PlanJson("query_state", "Query", "ReadOnly"),
                ThoughtSummaries:
                [
                    new LlmThoughtSummary("diagnostic thinking summary", "fake")
                ]));
        var planner = new LlmWorkflowPlanner(fakeClient);

        var plan = await planner.CreatePlanAsync(CreatePlanningRequest());

        Assert.Single(plan.Steps);
        Assert.DoesNotContain("diagnostic thinking summary", plan.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic thinking summary", plan.Steps[0].Intent?.Rationale ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_gemini_api_key_produces_clear_provider_error_without_network()
    {
        var oldGeminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var oldGoogleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        var oldUseVertex = Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI");

        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI", null);

            var client = new GeminiLlmClient(new GeminiClientOptions(ApiKey: null, UseVertexAi: false));

            var exception = await Assert.ThrowsAsync<LlmClientException>(() =>
                client.GenerateAsync(new LlmRequest(
                    GeminiModelId.Flash25,
                    [new LlmMessage(LlmMessageRole.User, "Return JSON.")],
                    StructuredOutput: new LlmStructuredOutputOptions())));

            Assert.Equal(GeminiLlmClient.ProviderName, exception.ProviderName);
            Assert.Equal(LlmClientErrorKind.Authentication, exception.ErrorKind);
            Assert.Contains("no Gemini API key", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", oldGeminiKey);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", oldGoogleKey);
            Environment.SetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI", oldUseVertex);
        }
    }

    [Fact]
    public void Gemini_exception_classification_handles_ambiguous_status_code_members()
    {
        var exception = new AmbiguousProviderStatusException(
            "provider returned a temporary server error");

        var classification = GeminiExceptionClassifier.Classify(exception);

        Assert.Equal(LlmClientErrorKind.ServerError, classification.ErrorKind);
        Assert.Equal(503, classification.StatusCode);
        Assert.Equal("http_503", classification.ErrorClass);
    }

    [Fact]
    public async Task Retrying_llm_client_retries_server_error_then_returns_success()
    {
        var inner = new SequenceLlmClient(
            new LlmClientException(
                GeminiLlmClient.ProviderName,
                "Gemini generation failed. provider=Gemini; errorKind=ServerError; statusCode=503; errorClass=http_503; message=provider returned a temporary server error",
                errorKind: LlmClientErrorKind.ServerError,
                statusCode: 503,
                errorClass: "http_503"),
            new LlmResponse("fake", "fake-model", PlanJson("query_state", "Query", "ReadOnly")));
        var client = new RetryingLlmClient(inner, NoDelayRetries(maxAttempts: 3));

        var response = await client.GenerateAsync(SimpleLlmRequest());

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("2", response.Metadata?["llm.retry.attempts"]);
        Assert.Contains("servererror:http_503:503", response.Metadata?["llm.retry.reasons"]);
    }

    [Fact]
    public async Task Retrying_llm_client_retries_transient_failure_then_returns_success()
    {
        var inner = new SequenceLlmClient(
            new LlmClientException("fake", "Temporary provider failure.", errorKind: LlmClientErrorKind.Transient, errorClass: "temporary"),
            new LlmResponse("fake", "fake-model", PlanJson("query_state", "Query", "ReadOnly")));
        var client = new RetryingLlmClient(inner, NoDelayRetries(maxAttempts: 3));

        var response = await client.GenerateAsync(SimpleLlmRequest());

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("2", response.Metadata?["llm.retry.attempts"]);
        Assert.Contains("temporary", response.Metadata?["llm.retry.reasons"]);
    }

    [Fact]
    public async Task Retrying_llm_client_retries_provider_side_operation_cancellation_then_returns_success()
    {
        var inner = new SequenceLlmClient(
            new OperationCanceledException("Provider canceled before caller cancellation."),
            new LlmResponse("fake", "fake-model", PlanJson("query_state", "Query", "ReadOnly")));
        var client = new RetryingLlmClient(inner, NoDelayRetries(maxAttempts: 3));

        var response = await client.GenerateAsync(SimpleLlmRequest());

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("2", response.Metadata?["llm.retry.attempts"]);
        Assert.Contains("operation_canceled_without_caller_cancellation", response.Metadata?["llm.retry.reasons"]);
    }

    [Fact]
    public async Task Retrying_llm_client_does_not_retry_when_caller_token_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inner = new SequenceLlmClient(new OperationCanceledException(cancellation.Token));
        var client = new RetryingLlmClient(inner, NoDelayRetries(maxAttempts: 3));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GenerateAsync(SimpleLlmRequest(), cancellation.Token));

        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Retrying_llm_client_enforces_call_timeout_window()
    {
        var inner = new HangingLlmClient();
        var client = new RetryingLlmClient(
            inner,
            new LlmRetryOptions(
                MaxAttempts: 3,
                BaseDelay: TimeSpan.Zero,
                MaxDelay: TimeSpan.Zero,
                CallTimeout: TimeSpan.FromMilliseconds(25),
                UseJitter: false));

        var exception = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(SimpleLlmRequest()));

        Assert.Equal(LlmClientErrorKind.Transient, exception.ErrorKind);
        Assert.Equal("llm_call_timeout", exception.ErrorClass);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(1, inner.CallCount);
    }

    [Theory]
    [InlineData(LlmClientErrorKind.Authentication)]
    [InlineData(LlmClientErrorKind.BadRequest)]
    public async Task Retrying_llm_client_does_not_retry_non_transient_client_errors(
        LlmClientErrorKind errorKind)
    {
        var inner = new SequenceLlmClient(new LlmClientException(
            "fake",
            $"Non transient {errorKind}.",
            errorKind: errorKind,
            errorClass: errorKind.ToString()));
        var client = new RetryingLlmClient(inner, NoDelayRetries(maxAttempts: 3));

        var exception = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(SimpleLlmRequest()));

        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Retries_exhausted_surface_planner_unavailable_with_attempt_count()
    {
        var tool = new CountingTool("known_tool");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known_tool", "Known", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var llmClient = new RetryingLlmClient(
            new AlwaysThrowingLlmClient(new LlmClientException(
                "fake",
                "Temporary outage.",
                errorKind: LlmClientErrorKind.Transient,
                errorClass: "temporary_outage")),
            NoDelayRetries(maxAttempts: 3));
        var runner = CreateRunner(
            new LlmWorkflowPlanner(llmClient),
            catalog,
            new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2, MaxBlockedRetries: 0));

        var envelope = await runner.RunAsync(new RunRequest("Provider retry exhaustion test"));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.PlannerUnavailable, envelope.Outcome.StopReason);
        Assert.Contains(envelope.Outcome.Blockers, blocker => blocker.Contains("after 3 attempt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(envelope.Outcome.Blockers, blocker => blocker.Contains("provider=fake", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, tool.ExecutionCount);
    }

    private static bool IsGoogleType(Type type)
    {
        if (type.Namespace?.StartsWith("Google.", StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsGoogleType);
        }

        return type.HasElementType && type.GetElementType() is { } elementType && IsGoogleType(elementType);
    }

    private static PlanningRequest CreatePlanningRequest() =>
        new(
            new RunRequest("Create a two-step workflow that queries state and then acts"),
            DemoTools.CreateCatalog().Descriptors,
            [],
            []);

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ExecutionPolicy? policy = null) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            policy ?? new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2));

    private static LlmRequest SimpleLlmRequest() =>
        new(
            "fake-model",
            [new LlmMessage(LlmMessageRole.User, "Return JSON.")],
            StructuredOutput: new LlmStructuredOutputOptions());

    private static LlmRetryOptions NoDelayRetries(int maxAttempts) =>
        new(
            MaxAttempts: maxAttempts,
            BaseDelay: TimeSpan.Zero,
            MaxDelay: TimeSpan.Zero,
            CallTimeout: TimeSpan.FromMinutes(10),
            UseJitter: false);

    private static string PlanJson(string toolId, string kind, string effect) =>
        $$"""
        {
          "planId": "plan_model",
          "description": "Model produced plan.",
          "steps": [
            {
              "stepId": "step_model",
              "toolId": "{{toolId}}",
              "kind": "{{kind}}",
              "effect": "{{effect}}",
              "input": {
                "query": "current_state",
                "action": "write_marker"
              },
              "reason": "Use the supplied tool."
            }
          ],
          "completionCondition": "The selected tool completes."
        }
        """;

    private static string PlanJsonWithIntent() =>
        """
        {
          "planId": "plan_model",
          "description": "Model produced plan.",
          "steps": [
            {
              "stepId": "step_model",
              "toolId": "query_state",
              "kind": "Query",
              "effect": "ReadOnly",
              "input": {
                "query": "current_state"
              },
              "reason": "Use the supplied tool.",
              "intent": {
                "action": "Inspect the current state.",
                "rationale": "The public objective requires state before choosing an action.",
                "expectedOutcome": "A read-only observation describing current state."
              }
            }
          ],
          "completionCondition": "The selected tool completes."
        }
        """;

    private static string BatchedPlanJson() =>
        """
        {
          "planId": "plan_model",
          "description": "Model produced a batched evidence plan.",
          "steps": [
            {
              "stepId": "read_a",
              "toolId": "query_state",
              "kind": "Query",
              "effect": "ReadOnly",
              "input": {
                "query": "a"
              },
              "dependsOn": [],
              "batchId": "evidence",
              "reason": "Read independent evidence."
            },
            {
              "stepId": "read_b",
              "toolId": "query_state",
              "kind": "Query",
              "effect": "ReadOnly",
              "input": {
                "query": "b"
              },
              "dependsOn": [],
              "batchId": "evidence",
              "reason": "Read independent evidence."
            },
            {
              "stepId": "patch_after_reads",
              "toolId": "perform_action",
              "kind": "Action",
              "effect": "WritesLocalState",
              "input": {
                "action": "write_marker"
              },
              "dependsOn": [
                "read_a",
                "read_b"
              ],
              "batchId": null,
              "reason": "Mutate only after evidence."
            }
          ],
          "completionCondition": "The selected tool completes."
        }
        """;

    private static string RefinementJson(
        string toolId,
        string kind,
        string effect,
        string reason = PlanRefinementReasons.Observation) =>
        $$"""
        {
          "fromPlanId": "plan_model",
          "reason": "{{reason}}",
          "evidence": [
            {
              "kind": "observation",
              "refId": "observation_001"
            }
          ],
          "refinedPlan": {
            "planId": "plan_refined",
            "description": "Model refined plan.",
            "steps": [
              {
                "stepId": "step_refined",
                "toolId": "{{toolId}}",
                "kind": "{{kind}}",
                "effect": "{{effect}}",
                "input": {
                  "action": "write_marker"
                },
                "reason": "Use the observation."
              }
            ],
            "completionCondition": "The action completes."
          }
        }
        """;

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly Queue<LlmResponse> _responses;

        public FakeLlmClient(string structuredJson)
            : this(new LlmResponse("fake", "fake-model", structuredJson, structuredJson))
        {
        }

        public FakeLlmClient(params LlmResponse[] responses)
        {
            _responses = new Queue<LlmResponse>(responses);
        }

        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingLlmClient : ILlmClient
    {
        private readonly Exception _exception;

        public ThrowingLlmClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<LlmResponse>(_exception);
    }

    private sealed class AlwaysThrowingLlmClient : ILlmClient
    {
        private readonly Exception _exception;

        public AlwaysThrowingLlmClient(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromException<LlmResponse>(_exception);
        }
    }

    private sealed class SequenceLlmClient : ILlmClient
    {
        private readonly Queue<object> _results;

        public SequenceLlmClient(params object[] results)
        {
            _results = new Queue<object>(results);
        }

        public int CallCount { get; private set; }

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = _results.Dequeue();
            return result switch
            {
                LlmResponse response => Task.FromResult(response),
                Exception exception => Task.FromException<LlmResponse>(exception),
                _ => Task.FromException<LlmResponse>(new InvalidOperationException("Unexpected fake LLM result."))
            };
        }
    }

    private sealed class HangingLlmClient : ILlmClient
    {
        public int CallCount { get; private set; }

        public async Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private class BaseProviderStatusException : Exception
    {
        public BaseProviderStatusException(string message)
            : base(message)
        {
        }

        public int StatusCode => 500;
    }

    private sealed class AmbiguousProviderStatusException : BaseProviderStatusException
    {
        public AmbiguousProviderStatusException(string message)
            : base(message)
        {
        }

        public new int StatusCode => 503;
    }

    private sealed class CountingTool : ITool
    {
        private readonly string _toolId;

        public CountingTool(string toolId)
        {
            _toolId = toolId;
        }

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                _toolId,
                ReceiptStatus.Succeeded,
                "Executed.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>())));
        }
    }
}

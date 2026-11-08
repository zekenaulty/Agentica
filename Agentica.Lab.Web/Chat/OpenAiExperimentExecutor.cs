using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Lab.Web.Providers;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Lab.Web.Chat;

/// <summary>Lab-owned direct experiment transport and opt-in real Agentica harness host.</summary>
public sealed class OpenAiExperimentExecutor : IExperimentExecutor, IExperimentInputCounterSource, IExperimentInputCountJournalConsumer, IDisposable
{
    public const string ResponseToolId = "experiment.response.emit";
    public const string ResponseArtifactKind = "experiment.response";
    private readonly OpenAiProviderConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly ILabPlannerFactory? _plannerFactory;
    private readonly bool _ownsHttp;
    private IExperimentInputCountJournal? _countJournal;

    public OpenAiExperimentExecutor(OpenAiProviderConfiguration configuration, HttpClient? httpClient = null,
        ILabPlannerFactory? plannerFactory = null, IExperimentInputCountJournal? inputCountJournal = null)
    {
        _configuration = configuration;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _plannerFactory = plannerFactory;
        _countJournal = inputCountJournal;
    }

    public async Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = context.Turn.Settings;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var state = new DirectState(context, settings.AmbientWebSearch, "assistant");
        using var transport = new ExperimentProviderTransport(_http, context);
        using var observed = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var snapshot = _configuration.Capture();
            var key = snapshot.ApiKey ?? throw new ExperimentException("provider.not_configured", "Configure the existing OpenAI provider credential before sending a turn.");
            transport.AttachCounter(context.InputTokenCounter ?? new OpenAiExperimentInputTokenCounter(_http, key, settings.MaxProviderCalls + 32, _countJournal),
                ownsCounter: context.InputTokenCounter is null, key);
            if (settings.MemoryEnabled)
                throw new ExperimentException("memory.disabled", "The experiment memory adapter is not enabled.");
            if (settings.Mode == "harness" && settings.AmbientWebSearch)
                throw new ExperimentException("search.ambient_mode", "Ambient provider search is available in Clean Room mode. Bind web.search explicitly for Harness mode.");
            return settings.Mode == "harness"
                ? await HarnessAsync(context, observed, transport, key, deadline.Token).ConfigureAwait(false)
                : await DirectAsync(context, state, observed, transport, key, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested ? "cancelled" : "failed", state.Text.ToString(),
                new { providerCalls = transport.Calls, attempts = transport.Attempts, webSearchOutcomes = transport.SearchOutcomes, inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts, telemetryDeliveryFailed = transport.TelemetryDeliveryFailed, provider = state.Usage }, cancellationToken.IsCancellationRequested ? "turn.cancelled" : "turn.timeout",
                cancellationToken.IsCancellationRequested ? "Turn cancelled. Partial output is retained." : "The turn time limit expired. No automatic retry was made.");
        }
        catch (ExperimentException exception)
        {
            return new("failed", state.Text.ToString(), new { providerCalls = transport.Calls, attempts = transport.Attempts, webSearchOutcomes = transport.SearchOutcomes, inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts, telemetryDeliveryFailed = transport.TelemetryDeliveryFailed, provider = state.Usage }, exception.Code, exception.Message);
        }
        catch (LlmClientException exception)
        {
            return new("failed", state.Text.ToString(), new { providerCalls = transport.Calls, attempts = transport.Attempts, webSearchOutcomes = transport.SearchOutcomes, inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts, telemetryDeliveryFailed = transport.TelemetryDeliveryFailed, provider = state.Usage },
                transport.AdmissionFailure ?? transport.ProviderErrorCode ?? "provider." + (exception.ErrorClass switch
                { "stream_incomplete" => "incomplete_stream", "unsupported_output_item" => "unsupported_item", _ => exception.ErrorClass ?? "failure" }),
                transport.ProviderErrorMessage ?? "The provider could not complete the configured request. No automatic retry was made.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return new("failed", state.Text.ToString(), new { providerCalls = transport.Calls, attempts = transport.Attempts, webSearchOutcomes = transport.SearchOutcomes, inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts, telemetryDeliveryFailed = transport.TelemetryDeliveryFailed, provider = state.Usage }, "provider.stream_failed",
                "The provider stream did not complete. Partial output is retained; no automatic retry was made.");
        }
    }

    public void BindInputCountJournal(IExperimentInputCountJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (_countJournal is not null && !ReferenceEquals(_countJournal, journal))
            throw new InvalidOperationException("The executor already owns a different input-count journal.");
        _countJournal = journal;
    }

    public IExperimentInputTokenCounter CreateInputTokenCounter(ExperimentSettings settings)
    {
        var key = _configuration.Capture().ApiKey ?? throw new ExperimentException("provider.not_configured",
            "Configure the existing OpenAI credential before counting this conversation's input.");
        return new OpenAiExperimentInputTokenCounter(_http, key, settings.MaxProviderCalls + 32, _countJournal);
    }

    /// <summary>Provider-owned accessible IDs; this makes no generation request.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var key = _configuration.Capture().ApiKey;
        if (key is null) throw new ExperimentException("provider.not_configured", "Configure the existing OpenAI credential before listing available models.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.openai.com/v1/models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ExperimentException("provider.models_unavailable", "OpenAI could not list accessible model IDs (HTTP " + (int)response.StatusCode + "). A model ID may still be entered manually.", 502);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > 1_048_576)
                throw new ExperimentException("provider.models_limit", "The accessible model list exceeded the retained response limit.", 502);
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ExperimentException("provider.models_invalid", "OpenAI returned an invalid model list.", 502);
        return data.EnumerateArray().Select(item => ExperimentProviderTransport.String(item, "id"))
            .Where(id => id is { Length: > 0 and <= 200 } && !id.Any(char.IsControl))
            .Select(id => id!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(4096).ToArray();
    }

    private async Task<ExperimentExecutionResult> DirectAsync(ExperimentExecutionContext context, DirectState state,
        HttpClient client, ExperimentProviderTransport transport, string key, CancellationToken cancellationToken)
    {
        var settings = context.Turn.Settings;
        if (settings.EnabledTools.Count != 0)
            throw new ExperimentException("tools.require_harness", "Enable Harness mode to bind experiment tools.");
        var request = ExperimentDirectRequest.Create(settings, context.History, context.Turn.UserText,
            settings.ThreadRecallEnabled ? context.Recall : null);
        var shared = new ExperimentPreparedClient(new OpenAiResponsesClientOptions(key, settings.Model) { IncludeEncryptedReasoning = false },
            client, transport, context, "clean-room");
        await foreach (var item in shared.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            await state.ObserveAsync(item, transport.LatestCallId, transport.RedactText).ConfigureAwait(false);
        await context.Trace(new("provider.continuity", new
        {
            mode = "transcript-only",
            nativeContinuation = "not-retained",
            reasoningSummary = state.SummaryEmitted ? "emitted" : "not-emitted",
            responseId = state.ResponseId,
            providerCalls = transport.Calls
        }, transport.LatestCallId)).ConfigureAwait(false);
        return new("completed", state.FinalText ?? state.Text.ToString(), new
        { providerCalls = transport.Calls, attempts = transport.Attempts, webSearchOutcomes = transport.SearchOutcomes, inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts, telemetryDeliveryFailed = transport.TelemetryDeliveryFailed, webSearchCalls = state.WebSearchCalls, provider = state.Usage });
    }

    private async Task<ExperimentExecutionResult> HarnessAsync(ExperimentExecutionContext context, HttpClient observed,
        ExperimentProviderTransport transport, string key, CancellationToken cancellationToken)
    {
        var settings = context.Turn.Settings;
        using var ownedFactory = _plannerFactory is null ? new LabPlannerFactory(observed,
            openAiConfiguration: _configuration, useStatelessPlanRepair: true, inputBudgetOwnedByChatTransport: true,
            compiledInputCharacterLimit: settings.MaxInputCharacters,
            decorateClient: _ => new ExperimentPreparedClient(new(key, settings.Model),
                observed, transport, context, "harness")) : null;
        var factory = _plannerFactory ?? ownedFactory!;
        void Stream(LlmStreamEvent item) => context.Trace(new("planner.stream", new
        {
            kind = item.Kind.ToString(),
            text = transport.RedactText(item.Text),
            source = item.Source is null ? null : item.Source with { ItemId = transport.RedactText(item.Source.ItemId) },
            block = item.Block is null ? null : item.Block with { TextSnapshot = transport.RedactText(item.Block.TextSnapshot) },
            usage = item.Response?.Usage
        }, transport.LatestCallId)).GetAwaiter().GetResult();
        if (_plannerFactory is not null)
            await context.Trace(new("provider.inspection", new { coverage = "injected-planner-fixture", wireCapture = false })).ConfigureAwait(false);
        var planner = new ExperimentEvidencePlanner(factory.Create(new ProviderSettings("openai", settings.Model, settings.ReasoningEffort,
            settings.MaxOutputTokens, settings.ContextWindowTokens, settings.ReasoningSummary, ThinkingEffortResolved: true), Stream), context);
        var registrations = ExperimentTools.CreateRegistrations(context).ToList();
        var webBudget = new WebSearchBudget(settings.MaxWebSearchCalls);
        if (settings.EnabledTools.Contains("web.search", StringComparer.Ordinal))
            registrations.Add(ExperimentWebSearch.CreateRegistration(context,
                (query, token) => SearchAsync(context, query, observed, transport, webBudget, key, token)));
        registrations.Add(ResponseRegistration());
        var runner = new AgenticaRunner(planner, ToolCatalog.Create(registrations.ToArray()), new TraceSink(context),
            new DeterministicOutcomeReporter(), new ExecutionPolicy(MaxSteps: settings.MaxSteps,
                MaxRefinements: settings.MaxSteps, Timeout: TimeSpan.FromSeconds(settings.TimeoutSeconds),
                MaxPlanContinuations: settings.MaxContinuations, MaxBlockedRetries: 0, MaxParallelism: 1, MaxBatchSize: 1,
                AllowReadOnlyParallelBatches: false, EvaluateCompletionAfterEachBatch: true,
                EffectPolicy: new ToolEffectPolicy(registrations.Select(registration => registration.Descriptor.Effect)),
                SecurityPolicy: new ToolSecurityPolicy(
                    InitialBoundaries: [ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent, ToolDataBoundary.HostState],
                    ExternalPlannerAllowedBoundaries: [ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent,
                        ToolDataBoundary.HostState, ToolDataBoundary.WorkspaceContent, ToolDataBoundary.Public, ToolDataBoundary.ExternalUntrusted])),
            EvidenceCompletionEvaluator.ForArtifactKind(ResponseArtifactKind), new ExperimentFrameProjector());
        var runContext = new Dictionary<string, object?>
        {
            ["conversationId"] = context.ConversationId,
            ["transcript"] = context.History.Select(message => new { message.Id, message.Role, message.Content }).ToArray(),
            ["customInstructions"] = settings.Instructions,
            ["experimentCondition"] = "Harness mode: Agentica planning, tool schemas, goal spine and execution policy are enabled.",
            ["completionContract"] = "Answer the current user turn. Use bound utilities as needed, then call " + ResponseToolId + " with the final answer."
        };
        if (settings.ThreadRecallEnabled && context.Recall is { Evidence.Count: > 0 } recall)
            runContext["historicalThreadEvidence"] = ExperimentDirectRequest.FormatEvidence(recall);
        var envelope = await runner.RunAsync(new RunRequest(context.Turn.UserText, RequestOrigin.User, runContext), cancellationToken).ConfigureAwait(false);
        await context.Trace(new("harness.outcome", new
        {
            status = envelope.Outcome.Status.ToString(),
            stopReason = envelope.Outcome.StopReason.ToString(),
            envelope.Outcome.CompletionEvidence,
            residualObligations = envelope.Outcome.Status == RunOutcomeStatus.Succeeded
                ? Array.Empty<string>() : new[] { "Produce a host-accepted response artifact for the current user turn." },
            blockers = envelope.Outcome.Blockers,
            receipts = envelope.Receipts.Items,
            artifacts = envelope.Details.Artifacts,
            plans = envelope.Details.PlanVersions,
            refinements = envelope.Details.PlanRefinements,
            frames = envelope.Details.PlanningFrames,
            toolSurfaces = envelope.Details.ToolSurfaces,
            envelope.Details.ValidationIssues,
            eventDeliveryFailure = envelope.Details.EventDeliveryFailure,
            providerCalls = transport.Calls,
            attempts = transport.Attempts,
            webSearchOutcomes = transport.SearchOutcomes,
            inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts,
            telemetryDeliveryFailed = transport.TelemetryDeliveryFailed,
            webSearchCalls = webBudget.CompletedCalls,
            unresolvedWebSearchReservation = webBudget.UnresolvedReservation,
            nativeContinuation = "run-session-only; transcript replay between turns"
        })).ConfigureAwait(false);
        var artifact = envelope.Details.Artifacts.LastOrDefault(item => item.Kind == ResponseArtifactKind);
        var answer = artifact?.Payload.GetValueOrDefault("content")?.ToString();
        if (envelope.Outcome.Status == RunOutcomeStatus.Succeeded && answer is not null)
        {
            await context.Output(answer).ConfigureAwait(false);
            return new("completed", answer, new
            {
                providerCalls = transport.Calls,
                attempts = transport.Attempts,
                webSearchOutcomes = transport.SearchOutcomes,
                inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts,
                telemetryDeliveryFailed = transport.TelemetryDeliveryFailed,
                webSearchCalls = webBudget.CompletedCalls,
                unresolvedWebSearchReservation = webBudget.UnresolvedReservation,
                runnerRunId = envelope.Outcome.RunId
            });
        }
        return new(envelope.Outcome.Status == RunOutcomeStatus.Cancelled ? "cancelled" : "failed", answer,
            new
            {
                providerCalls = transport.Calls,
                attempts = transport.Attempts,
                webSearchOutcomes = transport.SearchOutcomes,
                inputCountAttempts = (context.InputTokenCounter as IExperimentInputCountEvidenceSource)?.CountAttempts,
                telemetryDeliveryFailed = transport.TelemetryDeliveryFailed,
                webSearchCalls = webBudget.CompletedCalls,
                unresolvedWebSearchReservation = webBudget.UnresolvedReservation,
                runnerRunId = envelope.Outcome.RunId
            }, transport.AdmissionFailure ?? planner.FailureCode ?? transport.ProviderErrorCode ?? "harness." + envelope.Outcome.StopReason,
            transport.ProviderErrorMessage ?? (transport.AdmissionFailure is not null ? "The configured provider-call or complete-input limit stopped dispatch. Inspect the exact request and admission trace."
                : planner.FailureCode is not null ? "The current tool observation exceeds the cumulative evidence allowance. Increase the tool evidence setting before starting a new turn."
                : "The Agentica harness did not produce a completed response artifact. Inspect the plan, receipts and outcome."));
    }

    private async Task<object> SearchAsync(ExperimentExecutionContext context, string query, HttpClient observed,
        ExperimentProviderTransport transport, WebSearchBudget budget, string key, CancellationToken cancellationToken)
    {
        var allowance = budget.Reserve();
        var settings = context.Turn.Settings;
        var request = OpenAiWebSearch.CreateRequest(settings.Model, query, ExperimentDirectRequest.Generation(settings), allowance)
            with
        {
            OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly,
            WebSearch = new(LlmWebSearchMode.Required, allowance, LlmCitationIndexUnit.Utf16CodeUnits)
        };
        var state = new DirectState(context with { Output = _ => Task.CompletedTask }, allowWebSearch: true, target: "tool");
        var shared = new ExperimentPreparedClient(new OpenAiResponsesClientOptions(key, settings.Model) { IncludeEncryptedReasoning = false },
            observed, transport, context, "web-search-query");
        try
        {
            await foreach (var item in shared.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                await state.ObserveAsync(item, transport.LatestCallId, transport.RedactText).ConfigureAwait(false);
        }
        catch (LlmClientException error)
        {
            throw new ExperimentException(transport.AdmissionFailure ?? transport.ProviderErrorCode ?? "provider." + error.ErrorClass,
                "The search did not complete. Its reserved allowance remains unavailable; no automatic retry was made.");
        }
        budget.Complete(allowance, state.WebSearchCalls);
        return new
        {
            query,
            answer = state.FinalText ?? state.Text.ToString(),
            provider = "openai",
            model = settings.Model,
            citations = state.Citations,
            sources = state.Sources,
            responseId = state.ResponseId,
            webSearchCalls = state.WebSearchCalls,
            referencesTruncated = state.ReferencesTruncated,
            usage = state.Usage,
            trust = "external-untrusted; citations identify sources, not truth",
            context = "explicit query only; conversation history and custom instructions were not sent"
        };
    }

    private sealed class WebSearchBudget(int maximum)
    {
        private int _remaining = maximum;
        public int CompletedCalls { get; private set; }
        public int UnresolvedReservation { get; private set; }
        public int Reserve()
        {
            var available = _remaining;
            if (available <= 0) throw new ExperimentException("web.search.call_limit", "The turn's web-search allowance is exhausted or reserved by an unresolved search.");
            _remaining = 0;
            UnresolvedReservation = available;
            return available;
        }
        public void Complete(int reserved, int calls)
        {
            if (calls > reserved) throw new ExperimentException("web.search.provider_limit", "The provider returned more built-in search calls than the requested ceiling.");
            CompletedCalls += calls;
            _remaining = reserved - calls;
            UnresolvedReservation = 0;
        }
    }

    private static ToolRegistration ResponseRegistration() => new(
        new ToolDescriptor(ResponseToolId, "Emit experiment response", ToolKind.Action, ToolEffect.WritesLocalState,
            InputSchema: ToolInputSchema.Create(new ToolInputField("content", ToolInputValueType.String, Required: true)),
            Description: "Emit the final assistant answer and its completion artifact.", RetrySafety: ToolRetrySafety.MutationUnsafe),
        new ResponseTool(), new ToolSecurityDeclaration(ToolEffect.WritesLocalState,
            [ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent], [ToolDataBoundary.ConversationContent],
            ToolExternalOutputClassification.None, ToolApprovalRequirement.None, ToolRetrySafety.MutationUnsafe,
            new ToolProvenance(ToolProvenanceKind.BuiltIn, "Agentica.Lab.Web.Experiment", "1")));

    private sealed class ResponseTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = invocation.Input.GetValueOrDefault("content");
            var content = raw is JsonElement json && json.ValueKind == JsonValueKind.String ? json.GetString() : raw as string;
            if (string.IsNullOrWhiteSpace(content) || content.Length > 200_000)
                throw new ArgumentException("A nonempty bounded final response is required.");
            var data = new Dictionary<string, object?> { ["content"] = content };
            var receipt = new Receipt(AgenticaIds.New("receipt"), invocation.StepId, invocation.ToolId,
                ReceiptStatus.Succeeded, "Experiment response emitted.", DateTimeOffset.UtcNow, data);
            return Task.FromResult(new ToolResult(receipt, Artifact: new Artifact(AgenticaIds.New("artifact"), ResponseArtifactKind,
                data, [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class TraceSink(ExperimentExecutionContext context) : IEventSink
    {
        public void Emit(ExecutionEvent executionEvent) =>
            context.Trace(new("harness.event", new { executionEvent })).GetAwaiter().GetResult();
    }

    private sealed class ExperimentFrameProjector : IPlanningFrameProjector
    {
        public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request) =>
            [new PlanningFrame(AgenticaIds.New("frame"), "lab.experiment.chat", "1", DateTimeOffset.UtcNow,
                new Dictionary<string, object?>
                {
                    ["conversation"] = request.Request.Context,
                    ["requiredFinalTool"] = ResponseToolId, ["requiredFinalArtifactKind"] = ResponseArtifactKind,
                    ["memoryEnabled"] = false
                }, []) { ToolSurfaceId = request.ToolSurface?.SurfaceId }];
    }

    private sealed class DirectState(ExperimentExecutionContext context, bool allowWebSearch = false, string target = "assistant")
    {
        public StringBuilder Text { get; } = new();
        public bool Completed { get; private set; }
        public bool SummaryEmitted { get; private set; }
        public string? FinalText { get; private set; }
        public string? ResponseId { get; private set; }
        public object? Usage { get; private set; }
        public string? ErrorCode { get; private set; }
        public string? ErrorMessage { get; private set; }
        public int WebSearchCalls { get; private set; }
        public int CompletedWebSearchCalls { get; private set; }
        public bool ReferencesTruncated { get; private set; }
        public List<Dictionary<string, object?>> Citations { get; } = [];
        public List<Dictionary<string, object?>> Sources { get; } = [];
        public async Task ObserveAsync(LlmStreamEvent item, string? callId, Func<string?, string?> redact)
        {
            if (item.Kind == LlmStreamEventKind.TextDelta && redact(item.Text) is { } delta)
            {
                if (Text.Length + delta.Length > 200_000)
                    throw new ExperimentException("provider.output_limit", "Provider output exceeded the retained text limit.");
                Text.Append(delta);
                await context.Output(delta).ConfigureAwait(false);
            }
            if (item.Kind == LlmStreamEventKind.ThoughtSummaryDelta) SummaryEmitted = true;
            if (item.Kind != LlmStreamEventKind.Completed || item.Response is not { } response) return;
            if (response.Text.Length is < 1 or > 200_000)
                throw new ExperimentException("provider.output_limit", "Provider completed without bounded assistant text.");
            FinalText = redact(response.Text)!;
            ResponseId = redact(response.AttemptEvidence?.ProviderResponseId);
            Usage = response.Usage;
            Completed = true;
            if (Text.Length == 0)
            {
                Text.Append(FinalText);
                await context.Output(FinalText).ConfigureAwait(false);
            }
            if (!allowWebSearch || response.WebSearch is not { } search) return;
            WebSearchCalls = search.Calls.Count;
            CompletedWebSearchCalls = search.CompletedCalls;
            ReferencesTruncated = search.ReferencesTruncated;
            Citations.AddRange(search.Citations.Select(citation => new Dictionary<string, object?>
            {
                ["url"] = redact(citation.Source.Url),
                ["title"] = redact(citation.Source.Title),
                ["startIndex"] = citation.AnswerStartIndex,
                ["endIndex"] = citation.AnswerEndIndex,
                ["providerStartIndex"] = citation.StartIndex,
                ["providerEndIndex"] = citation.EndIndex,
                ["outputIndex"] = citation.OutputIndex,
                ["contentIndex"] = citation.ContentIndex
            }));
            Sources.AddRange(search.Sources.Select(source => new Dictionary<string, object?>
            { ["url"] = redact(source.Url), ["title"] = redact(source.Title) }));
            await context.Trace(new("provider.citations", new
            {
                target,
                citations = Citations,
                sources = Sources,
                responseId = ResponseId,
                webSearchCalls = WebSearchCalls,
                referencesTruncated = ReferencesTruncated
            }, callId)).ConfigureAwait(false);
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

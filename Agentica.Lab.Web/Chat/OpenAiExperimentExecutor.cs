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
public sealed class OpenAiExperimentExecutor : IExperimentExecutor, IDisposable
{
    public const string ResponseToolId = "experiment.response.emit";
    public const string ResponseArtifactKind = "experiment.response";
    private readonly OpenAiProviderConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly ILabPlannerFactory? _plannerFactory;
    private readonly bool _ownsHttp;

    public OpenAiExperimentExecutor(OpenAiProviderConfiguration configuration, HttpClient? httpClient = null,
        ILabPlannerFactory? plannerFactory = null)
    {
        _configuration = configuration;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _plannerFactory = plannerFactory;
    }

    public async Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken)
    {
        var settings = context.Turn.Settings;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var state = new DirectState(context);
        using var transport = new ExperimentProviderTransport(_http, context,
            settings.Mode == "clean-room" ? state.ObserveAsync : null);
        using var observed = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            if (settings.MemoryEnabled)
                throw new ExperimentException("memory.disabled", "The experiment memory adapter is not enabled.");
            return settings.Mode == "harness"
                ? await HarnessAsync(context, observed, transport, deadline.Token).ConfigureAwait(false)
                : await DirectAsync(context, state, observed, transport, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested ? "cancelled" : "failed", state.Text.ToString(),
                new { providerCalls = transport.Calls }, cancellationToken.IsCancellationRequested ? "turn.cancelled" : "turn.timeout",
                cancellationToken.IsCancellationRequested ? "Turn cancelled. Partial output is retained." : "The turn time limit expired. No automatic retry was made.");
        }
        catch (ExperimentException exception)
        {
            return new("failed", state.Text.ToString(), new { providerCalls = transport.Calls }, exception.Code, exception.Message);
        }
        catch (LlmClientException exception)
        {
            return new("failed", state.Text.ToString(), new { providerCalls = transport.Calls },
                "provider." + (exception.ErrorClass ?? "failure"), "The provider could not complete the configured request. No automatic retry was made.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return new("failed", state.Text.ToString(), new { providerCalls = transport.Calls }, "provider.stream_failed",
                "The provider stream did not complete. Partial output is retained; no automatic retry was made.");
        }
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
        HttpClient client, ExperimentProviderTransport transport, CancellationToken cancellationToken)
    {
        var settings = context.Turn.Settings;
        if (settings.EnabledTools.Count != 0)
            throw new ExperimentException("tools.require_harness", "Enable Harness mode to bind experiment tools.");
        var snapshot = _configuration.Capture();
        if (snapshot.ApiKey is null)
            throw new ExperimentException("provider.not_configured", "Configure the existing OpenAI provider credential before sending a turn.");
        var input = context.History.Select(message =>
        {
            if (message.Role is not ("user" or "assistant"))
                throw new ExperimentException("context.role", "Only this conversation's visible user and assistant transcript may be replayed.");
            return new { role = message.Role, content = message.Content };
        }).Append(new { role = "user", content = context.Turn.UserText }).ToArray();
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model, ["input"] = input, ["stream"] = true, ["store"] = false,
            ["max_output_tokens"] = settings.MaxOutputTokens
        };
        // Empty means omission. Every nonempty character, including whitespace, is exact.
        if (settings.Instructions.Length != 0) body["instructions"] = settings.Instructions;
        var reasoning = new Dictionary<string, object?>();
        if (settings.ReasoningEffort is not null) reasoning["effort"] = settings.ReasoningEffort;
        if (settings.ReasoningSummary) reasoning["summary"] = "auto";
        if (reasoning.Count != 0) body["reasoning"] = reasoning;
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiResponsesClientOptions.DefaultEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.ApiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new("failed", ErrorCode: transport.ProviderErrorCode ?? "provider.http_" + (int)response.StatusCode,
                ErrorMessage: transport.ProviderErrorMessage ?? "OpenAI rejected the configured request. No fallback or retry was made.",
                Usage: new { providerCalls = transport.Calls });
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new ExperimentException("provider.content_type", "The provider did not return the requested event stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
        if (!state.Completed)
            return new("failed", state.Text.ToString(), state.Usage,
                state.ErrorCode ?? "provider.incomplete_stream", state.ErrorMessage ?? "The provider stream ended without a completed response. No retry was made.");
        if (state.FinalText is { } final && state.Text.Length == 0)
        {
            state.Text.Append(final);
            await context.Output(final).ConfigureAwait(false);
        }
        else if (state.FinalText is { } authoritative && authoritative != state.Text.ToString())
            await context.Trace(new("provider.output.reconciled", new
            {
                source = "completed provider output items", streamedCharacters = state.Text.Length,
                finalCharacters = authoritative.Length, note = "Completed output differs from deltas; terminal transcript uses the provider's completed text."
            }, transport.LatestCallId)).ConfigureAwait(false);
        await context.Trace(new("provider.continuity", new
        {
            mode = "transcript-only", nativeContinuation = "not-retained", reasoningSummary = state.SummaryEmitted ? "emitted" : "not-emitted",
            responseId = state.ResponseId, providerCalls = transport.Calls
        }, transport.LatestCallId)).ConfigureAwait(false);
        return new("completed", state.FinalText ?? state.Text.ToString(), new { providerCalls = transport.Calls, provider = state.Usage });
    }

    private async Task<ExperimentExecutionResult> HarnessAsync(ExperimentExecutionContext context, HttpClient observed,
        ExperimentProviderTransport transport, CancellationToken cancellationToken)
    {
        var settings = context.Turn.Settings;
        using var ownedFactory = _plannerFactory is null ? new LabPlannerFactory(observed,
            openAiConfiguration: _configuration, useStatelessPlanRepair: true) : null;
        var factory = _plannerFactory ?? ownedFactory!;
        void Stream(LlmStreamEvent item) => context.Trace(new("planner.stream", new
        {
            kind = item.Kind.ToString(), text = transport.RedactText(item.Text),
            usage = item.Response?.Usage
        }, transport.LatestCallId)).GetAwaiter().GetResult();
        if (_plannerFactory is not null)
            await context.Trace(new("provider.inspection", new { coverage = "injected-planner-fixture", wireCapture = false })).ConfigureAwait(false);
        var planner = new ExperimentEvidencePlanner(factory.Create(new ProviderSettings("openai", settings.Model, settings.ReasoningEffort,
            settings.MaxOutputTokens, settings.ContextWindowTokens, settings.ReasoningSummary, ThinkingEffortResolved: true), Stream), context);
        var registrations = ExperimentTools.CreateRegistrations(context).Append(ResponseRegistration()).ToArray();
        var runner = new AgenticaRunner(planner, ToolCatalog.Create(registrations), new TraceSink(context),
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
        var envelope = await runner.RunAsync(new RunRequest(context.Turn.UserText, RequestOrigin.User,
            new Dictionary<string, object?>
            {
                ["conversationId"] = context.ConversationId,
                ["transcript"] = context.History.Select(message => new { message.Id, message.Role, message.Content }).ToArray(),
                ["customInstructions"] = settings.Instructions,
                ["experimentCondition"] = "Harness mode: Agentica planning, tool schemas, goal spine and execution policy are enabled.",
                ["completionContract"] = "Answer the current user turn. Use bound utilities as needed, then call " + ResponseToolId + " with the final answer."
            }), cancellationToken).ConfigureAwait(false);
        await context.Trace(new("harness.outcome", new
        {
            status = envelope.Outcome.Status.ToString(), stopReason = envelope.Outcome.StopReason.ToString(),
            envelope.Outcome.CompletionEvidence, receipts = envelope.Receipts.Items, artifacts = envelope.Details.Artifacts,
            plans = envelope.Details.PlanVersions, refinements = envelope.Details.PlanRefinements,
            frames = envelope.Details.PlanningFrames, toolSurfaces = envelope.Details.ToolSurfaces,
            envelope.Details.ValidationIssues, eventDeliveryFailure = envelope.Details.EventDeliveryFailure,
            providerCalls = transport.Calls, nativeContinuation = "run-session-only; transcript replay between turns"
        })).ConfigureAwait(false);
        var artifact = envelope.Details.Artifacts.LastOrDefault(item => item.Kind == ResponseArtifactKind);
        var answer = artifact?.Payload.GetValueOrDefault("content")?.ToString();
        if (envelope.Outcome.Status == RunOutcomeStatus.Succeeded && answer is not null)
        {
            await context.Output(answer).ConfigureAwait(false);
            return new("completed", answer, new { providerCalls = transport.Calls, runnerRunId = envelope.Outcome.RunId });
        }
        return new(envelope.Outcome.Status == RunOutcomeStatus.Cancelled ? "cancelled" : "failed", answer,
            new { providerCalls = transport.Calls, runnerRunId = envelope.Outcome.RunId }, transport.AdmissionFailure ?? planner.FailureCode ?? transport.ProviderErrorCode ?? "harness." + envelope.Outcome.StopReason,
            transport.ProviderErrorMessage ?? (transport.AdmissionFailure is not null ? "The configured provider-call or complete-input limit stopped dispatch. Inspect the exact request and admission trace."
                : planner.FailureCode is not null ? "The current tool observation exceeds the cumulative evidence allowance. Increase the tool evidence setting before starting a new turn."
                : "The Agentica harness did not produce a completed response artifact. Inspect the plan, receipts and outcome."));
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

    private sealed class DirectState(ExperimentExecutionContext context)
    {
        public StringBuilder Text { get; } = new();
        public bool Completed { get; private set; }
        public bool SummaryEmitted { get; private set; }
        public string? FinalText { get; private set; }
        public string? ResponseId { get; private set; }
        public object? Usage { get; private set; }
        public string? ErrorCode { get; private set; }
        public string? ErrorMessage { get; private set; }
        public async Task ObserveAsync(string callId, JsonElement item)
        {
            var type = ExperimentProviderTransport.String(item, "type");
            if (Completed || ErrorCode is not null) throw new ExperimentException("provider.after_terminal", "Provider sent events after a terminal response.");
            if (type == "response.output_text.delta")
            {
                var delta = ExperimentProviderTransport.String(item, "delta") ?? "";
                if (Text.Length + delta.Length > 200_000)
                    throw new ExperimentException("provider.output_limit", "Provider output exceeded the retained text limit.");
                Text.Append(delta);
                await context.Output(delta).ConfigureAwait(false);
            }
            else if (type == "response.reasoning_summary_text.delta") SummaryEmitted = true;
            else if (type == "response.completed" && item.TryGetProperty("response", out var response))
            {
                ResponseId = ExperimentProviderTransport.String(response, "id");
                var status = ExperimentProviderTransport.String(response, "status");
                if (status is not (null or "completed")) { ErrorCode = "provider." + status; return; }
                if (response.TryGetProperty("usage", out var usage)) Usage = ExperimentProviderTransport.Sanitize(usage);
                var final = new StringBuilder();
                if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                    throw new ExperimentException("provider.missing_output", "The provider completed without output items.");
                foreach (var native in output.EnumerateArray())
                {
                    var nativeType = ExperimentProviderTransport.String(native, "type");
                    if (nativeType == "reasoning") continue;
                    if (nativeType != "message")
                        throw new ExperimentException("provider.unsupported_item", "Clean Room received an unsupported non-message output item; no tool was executed.");
                    if (!native.TryGetProperty("content", out var contents) || contents.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in contents.EnumerateArray())
                    {
                        if (ExperimentProviderTransport.String(part, "type") == "refusal")
                            throw new ExperimentException("provider.refusal", "The provider refused this request.");
                        if (ExperimentProviderTransport.String(part, "type") == "output_text")
                            final.Append(ExperimentProviderTransport.String(part, "text"));
                    }
                }
                if (final.Length > 200_000) throw new ExperimentException("provider.output_limit", "Provider output exceeded the retained text limit.");
                if (final.Length == 0) throw new ExperimentException("provider.empty_output", "The provider completed without assistant text.");
                FinalText = final.ToString(); Completed = true;
            }
            else if (type is "response.failed" or "response.incomplete" or "error")
            {
                ErrorCode = type.Replace("response.", "provider.", StringComparison.Ordinal);
                ErrorMessage = "The provider reported a failed or incomplete response. No automatic retry was made.";
            }
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

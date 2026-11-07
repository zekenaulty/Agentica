using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class LlmPreparedCallTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("gemini")]
    [InlineData("gemini-content")]
    public async Task Exact_preparation_requires_admission_then_sends_once_and_retains_unknown_usage(string mode)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = Prepare(mode, Request(), References(), http);
        var report = Assert.IsType<LlmConfigurationReport>(prepared.ConfigurationReport);
        Assert.Equal(prepared.Descriptor.ConfigurationIdentity, report.ConfigurationIdentity);
        Assert.Equal(prepared.Descriptor.ModelId, report.EffectiveModelId);
        var admission = new Admission(descriptor =>
        {
            Assert.Equal(0, handler.Sends);
            Assert.Equal(prepared.Descriptor, descriptor);
        });
        var expected = prepared.CopySensitiveRequestBody();

        var error = await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(admission));

        Assert.Equal(1, admission.Calls);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(expected, handler.Bodies.Single());
        var evidence = Assert.IsType<LlmAttemptEvidence>(error.AttemptEvidence);
        Assert.Same(evidence, prepared.Capture.Latest);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, evidence.DispatchCertainty);
        Assert.Equal(503, evidence.HttpStatusCode);
        Assert.Equal("physical-request", evidence.ProviderRequestId);
        Assert.Equal(LlmAttemptOutcome.Failed, evidence.Outcome);
        Assert.Null(evidence.Usage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.GenerateAsync(admission));
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, admission.Calls);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("gemini")]
    [InlineData("gemini-content")]
    public async Task Host_denial_prevents_every_transport_and_retains_rejection(string mode)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = Prepare(mode, Request(), References(), http);
        var admission = new Admission(_ => throw new InvalidOperationException("private host explanation"));

        var error = await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(admission));

        Assert.Equal(0, handler.Sends);
        Assert.Equal(1, admission.Calls);
        Assert.Equal("host_admission_failed", error.ErrorClass);
        Assert.DoesNotContain("private host explanation", error.Message, StringComparison.Ordinal);
        Assert.Equal(LlmAttemptOutcome.Rejected, prepared.Capture.Latest!.Outcome);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, prepared.Capture.Latest.DispatchCertainty);
        Assert.Null(prepared.Capture.Latest.Usage);
    }

    [Fact]
    public async Task Original_message_list_and_sensitive_copy_mutations_cannot_change_admitted_wire()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var messages = new List<LlmMessage> { new(LlmMessageRole.User, "original private input") };
        using var prepared = Prepare("openai", Request() with { Messages = messages }, References(), http);
        var expected = prepared.CopySensitiveRequestBody();
        messages[0] = new(LlmMessageRole.User, "replacement private input");
        messages.Add(new(LlmMessageRole.User, "extra input"));
        Array.Fill(prepared.CopySensitiveRequestBody(), (byte)'x');

        await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(new Admission()));

        Assert.Equal(expected, handler.Bodies.Single());
        Assert.Contains("original private input", Encoding.UTF8.GetString(handler.Bodies.Single()), StringComparison.Ordinal);
        Assert.DoesNotContain("replacement private input", Encoding.UTF8.GetString(handler.Bodies.Single()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_consumers_do_not_admit_or_overwrite_the_first_attempt_twice()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = Prepare("openai", Request(), References(), http);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new AsyncAdmission(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var first = prepared.GenerateAsync(admission);
        await entered.Task;
        var before = prepared.Capture.Latest;

        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.GenerateAsync(admission));

        Assert.Same(before, prepared.Capture.Latest);
        Assert.Equal(1, admission.Calls);
        Assert.Equal(0, handler.Sends);
        release.SetResult();
        await Assert.ThrowsAsync<LlmClientException>(() => first);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(LlmAttemptOutcome.Failed, prepared.Capture.Latest!.Outcome);
    }

    [Fact]
    public void Identities_bind_scope_route_bundle_phase_operation_and_exact_input_independently()
    {
        using var http = new HttpClient(new CaptureHandler());
        using var original = Prepare("openai", Request(), References(), http);
        using var newAttempt = Prepare("openai", Request(), References("attempt-2"), http);
        Assert.Equal(original.Descriptor.RequestIdentity, newAttempt.Descriptor.RequestIdentity);
        Assert.NotEqual(original.Descriptor.References.AttemptId, newAttempt.Descriptor.References.AttemptId);
        var variants = new[]
        {
            Prepare("openai", Request(), References(route: "route-2"), http),
            Prepare("openai", Request(), References(bundle: "bundle-2"), http),
            Prepare("openai", Request(), new("attempt-1", "other.operation", "plan", "route-1", "bundle-1"), http),
            Prepare("openai", Request(), new("attempt-1", "chat.respond", "evaluate", "route-1", "bundle-1"), http),
            Prepare("openai", Request(), References(), http, "different-scope"),
            Prepare("openai", Request() with { Messages = [new(LlmMessageRole.User, "changed input")] }, References(), http)
        };
        try
        {
            Assert.All(variants, variant => Assert.NotEqual(original.Descriptor.RequestIdentity, variant.Descriptor.RequestIdentity));
            Assert.All(variants.Take(5), variant => Assert.Equal(original.Descriptor.BodyHash, variant.Descriptor.BodyHash));
        }
        finally { foreach (var variant in variants) variant.Dispose(); }
    }

    [Fact]
    public void Ordinary_json_and_string_do_not_export_payload_credentials_or_native_material()
    {
        using var native = new LlmNativeContinuation("openai", "fixture-model", "private system",
            """[{"type":"reasoning","encrypted_content":"private-native-cipher"}]""");
        using var http = new HttpClient(new CaptureHandler());
        var request = Request() with
        {
            Messages = [new(LlmMessageRole.System, "private system"), new(LlmMessageRole.User, "private message")],
            NativeContinuation = native
        };
        using var prepared = Prepare("openai", request, References(), http);
        var ordinary = JsonSerializer.Serialize(prepared) + prepared;
        foreach (var secret in new[] { "private system", "private message", "private-native-cipher", "fixture-secret-key" })
            Assert.DoesNotContain(secret, ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("Capture", JsonSerializer.Serialize(prepared), StringComparison.Ordinal);
        Assert.Contains("private-native-cipher", Encoding.UTF8.GetString(prepared.CopySensitiveRequestBody()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_snapshot_owns_its_clone_and_not_the_callers_capsule()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var native = new LlmNativeContinuation("openai", "fixture-model", "",
            """[{"type":"reasoning","encrypted_content":"retained-cipher"}]""");
        var request = Request() with { NativeContinuation = native };
        using var prepared = Prepare("openai", request, References(), http);
        native.Dispose();
        await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(new Admission()));
        Assert.Contains("retained-cipher", Encoding.UTF8.GetString(handler.Bodies.Single()), StringComparison.Ordinal);

        using var secondNative = new LlmNativeContinuation("openai", "fixture-model", "",
            """[{"type":"reasoning","encrypted_content":"caller-owned-cipher"}]""");
        var second = Prepare("openai", Request() with { NativeContinuation = secondNative }, References("second"), http);
        second.Dispose();
        Assert.Contains("caller-owned-cipher", secondNative.HistoryStepsJson, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => second.CopySensitiveRequestBody());
    }

    [Fact]
    public async Task Configuration_bound_native_snapshot_retains_export_binding_after_caller_disposal()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var native = new LlmNativeContinuation("openai", "fixture-model", "",
            """[{"type":"reasoning","encrypted_content":"bound-private-cipher"}]""");
        var request = Request() with
        {
            NativeContinuation = native,
            GenerationOptions = new(MaxOutputTokens: 128, Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High))
        };
        var options = new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: Endpoint);
        var report = LlmConfigurationReports.Describe(request, options);
        using var binding = LlmNativeContinuationBinding.FromRequest(report, request);
        var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding);
        using var payload = exported.SensitivePayload;
        Assert.True(exported.Succeeded, exported.Rejection.ToString());
        Assert.True(LlmNativeContinuationCodec.ValidateBinding(native, binding).ConfigurationBound);
        using var prepared = LlmCallPreparation.Prepare(request, options, References(), "scope-1", http);
        native.Dispose();
        var admission = new Admission();

        await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(admission));

        Assert.Equal(1, admission.Calls);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(report.ConfigurationIdentity, prepared.Descriptor.ConfigurationIdentity);
        Assert.Contains("bound-private-cipher", Encoding.UTF8.GetString(handler.Bodies.Single()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("output")]
    public void Changed_native_configuration_is_rejected_during_preparation_before_admission_or_transport(string changed)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        using var native = new LlmNativeContinuation("openai", "fixture-model", "",
            """[{"type":"reasoning","encrypted_content":"bound-private-cipher"}]""");
        var request = Request() with
        {
            NativeContinuation = native,
            GenerationOptions = new(MaxOutputTokens: 128, Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High))
        };
        var options = new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: Endpoint);
        var report = LlmConfigurationReports.Describe(request, options);
        using var binding = LlmNativeContinuationBinding.FromRequest(report, request);
        var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding);
        using var payload = exported.SensitivePayload;
        Assert.True(exported.Succeeded, exported.Rejection.ToString());
        var changedRequest = request with
        {
            GenerationOptions = changed == "reasoning"
                ? request.GenerationOptions! with { Thinking = LlmThinkingOptions.AtEffort(LlmReasoningEffort.Low) }
                : request.GenerationOptions! with { MaxOutputTokens = 256 }
        };
        var admission = new Admission();

        var error = Assert.Throws<LlmClientException>(() =>
            LlmCallPreparation.Prepare(changedRequest, options, References(), "scope-1", http));

        Assert.Equal("continuation_configurationmismatch", error.ErrorClass);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, handler.Sends);
        Assert.True(LlmNativeContinuationCodec.ValidateBinding(native, binding).IsCompatible);
    }

    [Fact]
    public async Task Defaulted_model_remains_effective_without_being_reported_as_a_caller_requested_model()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var options = new OpenAiResponsesClientOptions("fixture-secret-key", "fixture-default", Endpoint);
        using var prepared = LlmCallPreparation.Prepare(Request() with { ModelId = "" }, options,
            References(), "scope-1", http);
        Assert.Equal("fixture-default", prepared.Descriptor.ModelId);
        var report = Assert.IsType<LlmConfigurationReport>(prepared.ConfigurationReport);
        Assert.Equal("", report.RequestedModelId);
        Assert.Equal("fixture-default", report.EffectiveModelId);
        Assert.Equal(prepared.Descriptor.ConfigurationIdentity, report.ConfigurationIdentity);

        var error = await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(new Admission()));

        Assert.Equal("fixture-default", prepared.Capture.Latest!.EffectiveModelId);
        Assert.Null(prepared.Capture.Latest.RequestedModelId);
        Assert.Same(prepared.Capture.Latest, error.AttemptEvidence);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("fixture-default", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Display_endpoint_omits_query_secrets_while_exact_identity_retains_the_actual_route()
    {
        using var http = new HttpClient(new CaptureHandler());
        using var first = LlmCallPreparation.Prepare(Request(),
            new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: new("https://fixture.invalid/v1/responses?route=secret-one")),
            References(), "scope-1", http);
        using var second = LlmCallPreparation.Prepare(Request(),
            new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: new("https://fixture.invalid/v1/responses?route=secret-two")),
            References(), "scope-1", http);

        Assert.Equal("https://fixture.invalid/v1/responses", first.Descriptor.TransportEndpoint);
        Assert.Equal(first.Descriptor.TransportEndpoint, second.Descriptor.TransportEndpoint);
        Assert.Equal(first.Descriptor.ConfigurationIdentity, second.Descriptor.ConfigurationIdentity);
        Assert.NotEqual(first.Descriptor.RequestIdentity, second.Descriptor.RequestIdentity);
        Assert.DoesNotContain("secret-one", JsonSerializer.Serialize(first), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-two", JsonSerializer.Serialize(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Borrowed_project_header_change_before_dispatch_rejects_before_host_reservation()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("OpenAI-Project", "private-original-project");
        using var prepared = Prepare("openai", Request(), References(), http);
        http.DefaultRequestHeaders.Remove("OpenAI-Project");
        http.DefaultRequestHeaders.Add("OpenAI-Project", "private-replacement-project");
        var admission = new Admission();

        var error = await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(admission));

        Assert.Equal("prepared_transport_changed", error.ErrorClass);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, prepared.Capture.Latest!.DispatchCertainty);
        Assert.Equal(LlmAttemptOutcome.Rejected, prepared.Capture.Latest.Outcome);
        Assert.Null(prepared.Capture.Latest.Usage);
    }

    [Fact]
    public async Task Borrowed_project_header_change_while_admission_awaits_keeps_reservation_but_prevents_dispatch()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("OpenAI-Project", "private-original-project");
        using var prepared = Prepare("openai", Request(), References(), http);
        var reservations = new List<LlmPreparedRequestDescriptor>();
        var admission = new AsyncAdmission(async (descriptor, _) =>
        {
            reservations.Add(descriptor);
            await Task.Yield();
            http.DefaultRequestHeaders.Remove("OpenAI-Project");
            http.DefaultRequestHeaders.Add("OpenAI-Project", "private-replacement-project");
        });

        var error = await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(admission));

        Assert.Equal("prepared_transport_changed", error.ErrorClass);
        Assert.Equal(prepared.Descriptor, Assert.Single(reservations));
        Assert.Equal(1, admission.Calls);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, prepared.Capture.Latest!.DispatchCertainty);
        Assert.Equal(LlmAttemptOutcome.Rejected, prepared.Capture.Latest.Outcome);
        Assert.Null(prepared.Capture.Latest.Usage);
    }

    [Fact]
    public async Task Stable_borrowed_defaults_reach_wire_and_change_only_a_nonsecret_transport_fingerprint()
    {
        using var handler = new CaptureHandler((request, _) =>
        {
            Assert.Equal("private-project-marker", Assert.Single(request.Headers.GetValues("OpenAI-Project")));
            Assert.Equal("private-header-key-marker", Assert.Single(request.Headers.GetValues("X-Private-Test-Key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(42) };
        http.DefaultRequestHeaders.Add("OpenAI-Project", "private-project-marker");
        http.DefaultRequestHeaders.Add("X-Private-Test-Key", "private-header-key-marker");
        using var prepared = Prepare("openai", Request(), References(), http);
        using var otherHttp = new HttpClient(new CaptureHandler()) { Timeout = http.Timeout };
        otherHttp.DefaultRequestHeaders.Add("OpenAI-Project", "private-other-project-marker");
        otherHttp.DefaultRequestHeaders.Add("X-Private-Test-Key", "private-header-key-marker");
        using var other = Prepare("openai", Request(), References(), otherHttp);
        var admission = new Admission();

        await Assert.ThrowsAsync<LlmClientException>(() => prepared.GenerateAsync(admission));

        Assert.Equal(1, admission.Calls);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(prepared.Descriptor.BodyHash, other.Descriptor.BodyHash);
        Assert.Equal(prepared.Descriptor.ConfigurationIdentity, other.Descriptor.ConfigurationIdentity);
        Assert.NotNull(prepared.Descriptor.TransportConfigurationIdentity);
        Assert.StartsWith("sha256:", prepared.Descriptor.TransportConfigurationIdentity, StringComparison.Ordinal);
        Assert.NotEqual(prepared.Descriptor.TransportConfigurationIdentity, other.Descriptor.TransportConfigurationIdentity);
        Assert.NotEqual(prepared.Descriptor.RequestIdentity, other.Descriptor.RequestIdentity);
        var ordinaryJson = JsonSerializer.Serialize(prepared);
        Assert.DoesNotContain("private-project-marker", ordinaryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-header-key-marker", ordinaryJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("endpoint")]
    [InlineData("protocol")]
    public async Task Serializer_or_route_mismatch_rejects_before_host_admission_or_send(string mismatch)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var options = new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: Endpoint);
        using var original = LlmCallPreparation.Prepare(Request(), options, References(), "scope-1", http);
        var body = original.CopySensitiveRequestBody();
        if (mismatch == "body") body[^1] = (byte)' ';
        using var changed = new LlmPreparedCall(new OpenAiResponsesLlmClient(options, http), Request(),
            original.Descriptor, body, mismatch == "endpoint" ? new Uri("https://fixture.invalid/other") : Endpoint,
            mismatch == "protocol" ? "application/json|" : "text/event-stream|");
        var admission = new Admission();

        var error = await Assert.ThrowsAsync<LlmClientException>(() => changed.GenerateAsync(admission));

        Assert.Equal("prepared_request_mismatch", error.ErrorClass);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, changed.Capture.Latest!.DispatchCertainty);
    }

    [Fact]
    public async Task Reopened_host_preserves_attempt_custody_cumulative_budget_and_live_revocation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "agentica-admission-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var handler = new CaptureHandler();
            using var http = new HttpClient(handler);
            using var first = Prepare("openai", Request(), References(), http);
            await Assert.ThrowsAsync<LlmClientException>(() => first.GenerateAsync(new DurableAdmission(path, 2)));
            var originalEvidence = first.Capture.Latest;
            using var duplicate = Prepare("openai", Request(), References(), http);
            await Assert.ThrowsAsync<LlmClientException>(() => duplicate.GenerateAsync(new DurableAdmission(path, 2)));
            Assert.Equal(1, handler.Sends);
            Assert.Same(originalEvidence, first.Capture.Latest);
            using var revoked = Prepare("openai", Request(), References("attempt-revoked"), http);
            Assert.Equal(first.Descriptor.RequestIdentity, revoked.Descriptor.RequestIdentity);
            await Assert.ThrowsAsync<LlmClientException>(() => revoked.GenerateAsync(new DurableAdmission(path, 2, authorized: false)));
            Assert.Equal(1, handler.Sends);
            using var second = Prepare("openai", Request(), References("attempt-2"), http);
            await Assert.ThrowsAsync<LlmClientException>(() => second.GenerateAsync(new DurableAdmission(path, 2)));
            using var overBudget = Prepare("openai", Request(), References("attempt-3"), http);
            await Assert.ThrowsAsync<LlmClientException>(() => overBudget.GenerateAsync(new DurableAdmission(path, 2)));
            Assert.Equal(2, handler.Sends);
            var persisted = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(2, persisted.Count);
            Assert.Equal(first.Descriptor.RequestIdentity, persisted["attempt-1"]);
            Assert.Equal(second.Descriptor.RequestIdentity, persisted["attempt-2"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Preparation_rejects_an_already_supplied_attempt_capture_instead_of_silently_rebinding_it()
    {
        using var http = new HttpClient(new CaptureHandler());
        var capture = new LlmAttemptCapture(References("original-custody"));
        var recorder = LlmAttemptRecorder.Start(Request() with { AttemptCapture = capture }, "openai", "fixture-model");
        var original = recorder.Finish(LlmAttemptOutcome.Rejected, "host_rejected");

        Assert.Throws<ArgumentException>(() => Prepare("openai", Request() with { AttemptCapture = capture }, References(), http));

        Assert.Same(original, capture.Latest);
    }

    internal static readonly Uri Endpoint = new("https://fixture.invalid/v1/responses");
    internal static LlmRequest Request() => new("fixture-model", [new(LlmMessageRole.User, "private input")]);
    internal static LlmAttemptReferences References(string attempt = "attempt-1", string route = "route-1", string bundle = "bundle-1") =>
        new(attempt, "chat.respond", "plan", route, bundle);

    internal static LlmPreparedCall Prepare(string mode, LlmRequest request, LlmAttemptReferences references,
        HttpClient http, string scope = "scope-1") => mode switch
        {
            "openai" => LlmCallPreparation.Prepare(request, new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: Endpoint), references, scope, http),
            "xai" => LlmCallPreparation.Prepare(request, new XaiResponsesClientOptions("fixture-secret-key", Endpoint: Endpoint), references, scope, http),
            "anthropic" => LlmCallPreparation.Prepare(request, new AnthropicMessagesClientOptions("fixture-secret-key", Endpoint: Endpoint), references, scope, http),
            "ollama" => LlmCallPreparation.Prepare(request, new OllamaClientOptions(Endpoint: Endpoint), references, scope, http),
            "gemini" => LlmCallPreparation.Prepare(request, new GeminiInteractionsClientOptions("fixture-secret-key", Endpoint: Endpoint), references, scope, http),
            "gemini-content" => LlmCallPreparation.Prepare(request, new GeminiGenerateContentClientOptions("fixture-secret-key", Endpoint: Endpoint), references, scope, http),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    internal sealed class Admission(Action<LlmPreparedRequestDescriptor>? inspect = null) : ILlmCallAdmission
    {
        internal int Calls { get; private set; }
        public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            inspect?.Invoke(request);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncAdmission(Func<LlmPreparedRequestDescriptor, CancellationToken, Task> admit) : ILlmCallAdmission
    {
        internal int Calls { get; private set; }
        public async ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
        {
            Calls++;
            await admit(request, cancellationToken);
        }
    }

    // A minimal consumer fixture, not an Agentica persistence implementation. Reservations
    // remain spent after uncertain/provider failure and reopening never resets the budget.
    private sealed class DurableAdmission(string path, int maximumAttempts, bool authorized = true) : ILlmCallAdmission
    {
        public async ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
        {
            if (!authorized) throw new InvalidOperationException("live authorization revoked");
            var rows = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, cancellationToken))!
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (rows.ContainsKey(request.References.AttemptId)) throw new InvalidOperationException("duplicate attempt");
            if (rows.Count >= maximumAttempts) throw new InvalidOperationException("cumulative budget spent");
            rows.Add(request.References.AttemptId, request.RequestIdentity);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows), cancellationToken);
        }
    }

    internal sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal List<byte[]> Bodies { get; } = [];
        internal List<Uri> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Uris.Add(request.RequestUri!);
            if (respond is not null) return await respond(request, cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Headers.Add("x-request-id", "physical-request");
            return response;
        }
    }
}

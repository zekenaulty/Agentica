using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class LlmAttemptEvidenceTests
{
    [Fact]
    public void Existing_positional_contracts_and_default_serialization_remain_compatible()
    {
        var request = Request();
        var (model, messages, generation, structured, metadata, native) = request;
        Assert.Equal("model", model);
        Assert.Single(messages);
        Assert.Null(generation);
        Assert.Null(structured);
        Assert.Null(metadata);
        Assert.Null(native);
        var response = new LlmResponse("fixture", "model", "answer");
        var (provider, resolvedModel, text, json, thoughts, usage, finish, responseMetadata, continuation) = response;
        Assert.Equal(("fixture", "model", "answer"), (provider, resolvedModel, text));
        Assert.Null(json);
        Assert.Null(thoughts);
        Assert.Null(usage);
        Assert.Equal(LlmFinishReason.Unknown, finish);
        Assert.Null(responseMetadata);
        Assert.Null(continuation);
        var item = new LlmStreamEvent(LlmStreamEventKind.Completed, Response: response);
        var (kind, delta, completed) = item;
        Assert.Equal(LlmStreamEventKind.Completed, kind);
        Assert.Null(delta);
        Assert.Same(response, completed);
        Assert.DoesNotContain("AttemptEvidence", JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.DoesNotContain("AttemptEvidence", JsonSerializer.Serialize(item), StringComparison.Ordinal);
    }

    [Fact]
    public void Request_capture_is_local_and_evidence_has_only_bounded_structural_facts()
    {
        var capture = Capture();
        var request = Request(capture);
        var recorder = LlmAttemptRecorder.Start(request, "fixture", "model");
        recorder.Observe(providerRequestId: "request-1", providerResponseId: "response-1",
            providerStatus: "completed", usage: new LlmUsage(OutputTokens: 20, ThinkingTokens: 5)
            { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking });
        var evidence = recorder.Finish(LlmAttemptOutcome.Succeeded);
        var requestJson = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("AttemptCapture", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("host-attempt-1", requestJson, StringComparison.Ordinal);
        var evidenceJson = JsonSerializer.Serialize(evidence);
        var restored = JsonSerializer.Deserialize<LlmAttemptEvidence>(evidenceJson);
        Assert.Equal(evidence, restored);
        using var parsed = JsonDocument.Parse(evidenceJson);
        Assert.False(parsed.RootElement.TryGetProperty("Text", out _));
        Assert.False(parsed.RootElement.TryGetProperty("NativeContinuation", out _));
        Assert.Equal(20, restored!.Usage!.GeneratedOutputTokens);
        Assert.Equal(15, restored.Usage.NonThinkingOutputTokens);
        Assert.Equal("book.inspect", restored.References.OperationId);
    }

    [Fact]
    public void Completed_provider_facts_survive_late_client_failure_and_cleanup()
    {
        var capture = Capture();
        var recorder = LlmAttemptRecorder.Start(Request(capture), "fixture", "model");
        var first = recorder.Evidence;
        recorder.Observe(providerResponseId: "response-1", httpStatusCode: 200,
            providerStatus: "completed", finishReason: LlmFinishReason.Stop,
            dispatchCertainty: LlmDispatchCertainty.ResponseObserved,
            usage: new LlmUsage(10, 20, 5, 30)
            { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking });
        var delivered = recorder.Finish(LlmAttemptOutcome.Succeeded);
        var failed = recorder.Finish(LlmAttemptOutcome.Failed, "output_mismatch");
        Assert.Equal(LlmAttemptOutcome.InProgress, first.Outcome);
        Assert.Null(first.Usage);
        Assert.Equal(LlmAttemptOutcome.Succeeded, delivered.Outcome);
        Assert.Equal(LlmAttemptOutcome.Failed, failed.Outcome);
        Assert.Equal("completed", failed.ProviderStatus);
        Assert.Equal("response-1", failed.ProviderResponseId);
        Assert.Equal(30, failed.Usage!.TotalTokens);
        Assert.Equal("output_mismatch", failed.ReasonCode);
        Assert.Same(failed, recorder.Finish(LlmAttemptOutcome.Abandoned, "stream_disposed"));
        Assert.Same(failed, capture.Latest);
        Assert.True(failed.Sequence > delivered.Sequence);
    }

    [Fact]
    public void Cumulative_usage_updates_replace_counts_and_absent_fields_preserve_prior_facts()
    {
        var recorder = LlmAttemptRecorder.Start(Request(), "fixture", "model");
        var first = recorder.Observe(providerRequestId: "request-1",
            dispatchCertainty: LlmDispatchCertainty.ResponseObserved,
            usage: new LlmUsage(10, 2, CachedPromptTokens: 3)
            { OutputTokenSemantics = LlmOutputTokenSemantics.ExcludesThinking });
        var second = recorder.Observe(usage: new LlmUsage(OutputTokens: 7, ThinkingTokens: 4),
            dispatchCertainty: LlmDispatchCertainty.MayHaveDispatched);
        Assert.Equal(2, first.Usage!.OutputTokens);
        Assert.Equal(7, second.Usage!.OutputTokens);
        Assert.Equal(10, second.Usage.PromptTokens);
        Assert.Equal(3, second.Usage.CachedPromptTokens);
        Assert.Equal(11, second.Usage.GeneratedOutputTokens);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, second.DispatchCertainty);
        Assert.Equal("request-1", second.ProviderRequestId);
        Assert.Same(second.Usage, recorder.Observe().Usage);
    }

    [Fact]
    public void Conflicting_token_semantics_remain_unknown_in_later_partial_updates()
    {
        var recorder = LlmAttemptRecorder.Start(Request(), "fixture", "model");
        recorder.Observe(usage: new LlmUsage(OutputTokens: 5, ThinkingTokens: 2)
        { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking });
        recorder.Observe(usage: new LlmUsage(OutputTokens: 8)
        { OutputTokenSemantics = LlmOutputTokenSemantics.ExcludesThinking });
        var evidence = recorder.Observe(usage: new LlmUsage(TotalTokens: 15)
        { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking });
        Assert.Equal(LlmOutputTokenSemantics.Unknown, evidence.Usage!.OutputTokenSemantics);
        Assert.Null(evidence.Usage.GeneratedOutputTokens);
        Assert.Equal(8, evidence.Usage.OutputTokens);
        Assert.Equal(2, evidence.Usage.ThinkingTokens);
    }

    [Fact]
    public async Task Concurrent_capture_reuse_rejects_second_owner_without_overwriting_first_evidence()
    {
        var capture = Capture();
        var request = Request(capture);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { return LlmAttemptRecorder.Start(request, "fixture", "model"); }
            catch (InvalidOperationException) { return null; }
        })));
        var owner = Assert.Single(attempts, attempt => attempt is not null)!;
        var terminal = owner.Finish(LlmAttemptOutcome.Succeeded);
        Assert.Throws<InvalidOperationException>(() => LlmAttemptRecorder.Start(request, "fixture", "model"));
        Assert.Same(terminal, capture.Latest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_cancellation_keeps_the_caller_token_and_available_attempt_facts(bool afterDispatch)
    {
        using var cancellation = new CancellationTokenSource();
        var capture = Capture();
        var request = Request(capture);
        var client = new EvidenceClient(afterDispatch ? Mode.CancelAfterResponse : Mode.CancelBeforeDispatch,
            cancellation.Cancel);
        var events = new List<LlmStreamEvent>();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LlmStreamCompletion.GenerateAsync(client, request, events.Add, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var evidence = capture.Latest!;
        Assert.Equal(LlmAttemptOutcome.Cancelled, evidence.Outcome);
        Assert.Equal(afterDispatch ? LlmDispatchCertainty.ResponseObserved : LlmDispatchCertainty.NotDispatched,
            evidence.DispatchCertainty);
        Assert.Equal(afterDispatch ? 9 : null, evidence.Usage?.PromptTokens);
        Assert.Same(evidence, events.Last().AttemptEvidence);
        Assert.Equal(LlmStreamEventKind.Cancelled, events.Last().Kind);
    }

    [Fact]
    public async Task Broken_ui_observer_cannot_change_retained_completion_or_turn_into_admission()
    {
        var capture = Capture();
        var calls = 0;
        var response = await LlmStreamCompletion.GenerateAsync(new EvidenceClient(Mode.Success),
            Request(capture), _ => { calls++; throw new InvalidOperationException("UI unavailable"); },
            CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("answer", response.Text);
        Assert.Same(capture.Latest, response.AttemptEvidence);
        Assert.Equal(LlmAttemptOutcome.Succeeded, response.AttemptEvidence!.Outcome);
    }

    [Fact]
    public async Task Exception_and_failed_event_retain_known_usage_without_returning_a_successful_response()
    {
        var capture = Capture();
        var events = new List<LlmStreamEvent>();
        var exception = await Assert.ThrowsAsync<LlmClientException>(() => LlmStreamCompletion.GenerateAsync(
            new EvidenceClient(Mode.Failure), Request(capture), events.Add, CancellationToken.None));
        Assert.Equal("invalid_output", exception.ErrorClass);
        Assert.Same(capture.Latest, exception.AttemptEvidence);
        Assert.Equal(9, exception.AttemptEvidence!.Usage!.PromptTokens);
        Assert.Equal("completed", exception.AttemptEvidence.ProviderStatus);
        Assert.Equal(LlmAttemptOutcome.Failed, exception.AttemptEvidence.Outcome);
        Assert.DoesNotContain(events, item => item.Kind == LlmStreamEventKind.Completed);
        Assert.Same(exception.AttemptEvidence, events.Last().AttemptEvidence);
    }

    [Fact]
    public async Task Early_stream_disposal_retains_facts_and_marks_abandonment_without_inventing_success()
    {
        var capture = Capture();
        var enumerator = new EvidenceClient(Mode.Success).StreamAsync(Request(capture)).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
        Assert.Equal(LlmAttemptOutcome.Abandoned, capture.Latest!.Outcome);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, capture.Latest.DispatchCertainty);
        Assert.Equal(9, capture.Latest.Usage!.PromptTokens);
    }

    [Fact]
    public void Evidence_rejects_unbounded_or_log_injecting_identifiers_and_undefined_states()
    {
        Assert.Throws<ArgumentException>(() => new LlmAttemptReferences(new string('a', 257)));
        Assert.Throws<ArgumentException>(() => new LlmAttemptReferences("attempt\nforged"));
        Assert.Throws<ArgumentException>(() => new LlmAttemptEvidence(Capture().References, "fixture", "model",
            reasonCode: "raw provider error body"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAttemptEvidence(Capture().References,
            "fixture", "model", dispatchCertainty: (LlmDispatchCertainty)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAttemptEvidence(Capture().References,
            "fixture", "model", httpStatusCode: 999));
    }

    private static LlmAttemptCapture Capture() => new(new LlmAttemptReferences("host-attempt-1",
        "book.inspect", "read", "route-v1", "bundle-v1"));

    private static LlmRequest Request(LlmAttemptCapture? capture = null) =>
        new("model", [new LlmMessage(LlmMessageRole.User, "input")]) { AttemptCapture = capture };

    private enum Mode { Success, Failure, CancelBeforeDispatch, CancelAfterResponse }

    private sealed class EvidenceClient(Mode mode, Action? cancel = null) : ILlmStreamingClient
    {
        public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            LlmResponse? result = null;
            await foreach (var item in StreamAsync(request, cancellationToken))
                if (item.Kind == LlmStreamEventKind.Completed) result = item.Response;
            return result ?? throw new InvalidOperationException("Fixture response missing.");
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var recorder = LlmAttemptRecorder.Start(request, "fixture", "model");
            try
            {
                if (mode == Mode.CancelBeforeDispatch)
                {
                    cancel!();
                    recorder.Finish(LlmAttemptOutcome.Cancelled, "caller_cancelled");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                recorder.Observe(dispatchCertainty: LlmDispatchCertainty.MayHaveDispatched);
                recorder.Observe(providerResponseId: "response-1", httpStatusCode: 200,
                    usage: new LlmUsage(PromptTokens: 9), providerStatus: "completed",
                    dispatchCertainty: LlmDispatchCertainty.ResponseObserved);
                yield return new LlmStreamEvent(LlmStreamEventKind.Activity, "fixture.response")
                { AttemptEvidence = recorder.Evidence };
                await Task.Yield();
                if (mode == Mode.CancelAfterResponse)
                {
                    cancel!();
                    recorder.Finish(LlmAttemptOutcome.Cancelled, "caller_cancelled");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (mode == Mode.Failure)
                    throw new LlmClientException("fixture", "Fixture output rejected.", errorClass: "invalid_output")
                    { AttemptEvidence = recorder.Finish(LlmAttemptOutcome.Failed, "invalid_output") };
                var evidence = recorder.Finish(LlmAttemptOutcome.Succeeded);
                yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                    Response: new LlmResponse("fixture", "model", "answer") { AttemptEvidence = evidence })
                { AttemptEvidence = evidence };
            }
            finally
            {
                if (recorder.Evidence.Outcome == LlmAttemptOutcome.InProgress)
                    recorder.Finish(LlmAttemptOutcome.Abandoned, "stream_disposed");
            }
        }
    }
}

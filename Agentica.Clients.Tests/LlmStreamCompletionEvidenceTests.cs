using System.Runtime.CompilerServices;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class LlmStreamCompletionEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Item_after_completed_retains_failure_evidence_with_or_without_supplied_capture(bool suppliedCapture)
    {
        var capture = suppliedCapture ? Capture() : null;
        var events = new List<LlmStreamEvent>();
        var error = await Assert.ThrowsAsync<LlmClientException>(() => LlmStreamCompletion.GenerateAsync(
            new CompletedThenFaultClient(FaultMode.ExtraItem), Request(capture), events.Add, CancellationToken.None));
        Assert.Equal("data_after_completion", error.ErrorClass);
        AssertFacts(error.AttemptEvidence!, LlmAttemptOutcome.Failed);
        Assert.Equal("data_after_completion", error.AttemptEvidence!.ReasonCode);
        Assert.Same(error.AttemptEvidence, events.Last().AttemptEvidence);
        Assert.DoesNotContain(events, item => item.Kind == LlmStreamEventKind.Completed);
        if (capture is not null) Assert.Same(error.AttemptEvidence, capture.Latest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_client_error_keeps_its_identity_and_retains_completed_provider_facts(bool suppliedCapture)
    {
        var capture = suppliedCapture ? Capture() : null;
        var original = new LlmClientException("fixture", "synthetic late error", errorClass: "late_stream_failure");
        var events = new List<LlmStreamEvent>();
        var error = await Assert.ThrowsAsync<LlmClientException>(() => LlmStreamCompletion.GenerateAsync(
            new CompletedThenFaultClient(FaultMode.Throw, original), Request(capture), events.Add, CancellationToken.None));
        Assert.Same(original, error);
        AssertFacts(error.AttemptEvidence!, LlmAttemptOutcome.Failed);
        Assert.Same(error.AttemptEvidence, events.Last().AttemptEvidence);
        if (capture is not null) Assert.Same(error.AttemptEvidence, capture.Latest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_cancellation_keeps_exception_and_token_while_completion_stays_observed(bool suppliedCapture)
    {
        using var cancellation = new CancellationTokenSource();
        var capture = suppliedCapture ? Capture() : null;
        var original = new OperationCanceledException(cancellation.Token);
        var events = new List<LlmStreamEvent>();
        var client = new CompletedThenFaultClient(FaultMode.Throw, original, cancellation.Cancel);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LlmStreamCompletion.GenerateAsync(client, Request(capture), events.Add, cancellation.Token));
        Assert.Same(original, error);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(LlmStreamEventKind.Cancelled, events.Last().Kind);
        AssertFacts(events.Last().AttemptEvidence!, LlmAttemptOutcome.Cancelled);
        if (capture is not null) Assert.Same(events.Last().AttemptEvidence, capture.Latest);
    }

    [Fact]
    public async Task Generic_late_error_keeps_its_type_and_identity_and_corrects_owned_capture()
    {
        var capture = Capture();
        var original = new InvalidOperationException("synthetic late failure");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LlmStreamCompletion.GenerateAsync(
            new CompletedThenFaultClient(FaultMode.Throw, original), Request(capture), _ => { }, CancellationToken.None));
        Assert.Same(original, error);
        AssertFacts(capture.Latest!, LlmAttemptOutcome.Failed);
    }

    [Fact]
    public async Task Broken_observer_does_not_erase_no_capture_late_error_evidence()
    {
        var original = new LlmClientException("fixture", "synthetic late error");
        var error = await Assert.ThrowsAsync<LlmClientException>(() => LlmStreamCompletion.GenerateAsync(
            new CompletedThenFaultClient(FaultMode.Throw, original), Request(),
            _ => throw new InvalidOperationException("synthetic UI failure"), CancellationToken.None));
        Assert.Same(original, error);
        AssertFacts(error.AttemptEvidence!, LlmAttemptOutcome.Failed);
    }

    [Fact]
    public async Task Reused_capture_rejection_cannot_rewrite_the_original_success()
    {
        var capture = Capture();
        var request = Request(capture);
        var response = await LlmStreamCompletion.GenerateAsync(new CompletedThenFaultClient(FaultMode.None),
            request, _ => { }, CancellationToken.None);
        var original = capture.Latest;
        var events = new List<LlmStreamEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LlmStreamCompletion.GenerateAsync(
            new CompletedThenFaultClient(FaultMode.ExtraItem), request, events.Add, CancellationToken.None));
        Assert.Same(original, response.AttemptEvidence);
        Assert.Same(original, capture.Latest);
        Assert.Equal(LlmAttemptOutcome.Succeeded, capture.Latest!.Outcome);
        Assert.Null(events.Last().AttemptEvidence);
    }

    [Fact]
    public void Compare_exchange_does_not_replace_a_newer_capture_snapshot()
    {
        var capture = Capture();
        var recorder = LlmAttemptRecorder.Start(Request(capture), "fixture", "model");
        var completed = recorder.Finish(LlmAttemptOutcome.Succeeded);
        var newer = recorder.Observe(providerRequestId: "newer-observation");
        Assert.Null(capture.TryCorrectOwnedCompletion(completed, LlmAttemptOutcome.Failed, "stream_failure"));
        Assert.Same(newer, capture.Latest);
    }

    [Fact]
    public async Task Existing_matching_exception_snapshot_is_corrected_without_replacing_exception()
    {
        var capture = Capture();
        var original = new LlmClientException("fixture", "synthetic late error");
        var client = new CompletedThenFaultClient(FaultMode.ThrowWithCompletedEvidence, original);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => LlmStreamCompletion.GenerateAsync(
            client, Request(capture), _ => { }, CancellationToken.None));
        Assert.Same(original, error);
        AssertFacts(error.AttemptEvidence!, LlmAttemptOutcome.Failed);
        Assert.Same(capture.Latest, error.AttemptEvidence);
    }

    private static void AssertFacts(LlmAttemptEvidence evidence, LlmAttemptOutcome outcome)
    {
        Assert.NotNull(evidence);
        Assert.Equal(outcome, evidence.Outcome);
        Assert.Equal("completed", evidence.ProviderStatus);
        Assert.Equal("provider-response", evidence.ProviderResponseId);
        Assert.Equal("provider-request", evidence.ProviderRequestId);
        Assert.Equal(200, evidence.HttpStatusCode);
        Assert.Equal(11, evidence.Usage!.PromptTokens);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, evidence.DispatchCertainty);
    }

    private static LlmAttemptCapture Capture() => new(new LlmAttemptReferences("stream-wrapper-attempt"));
    private static LlmRequest Request(LlmAttemptCapture? capture = null) =>
        new("model", [new LlmMessage(LlmMessageRole.User, "synthetic input")]) { AttemptCapture = capture };

    private enum FaultMode { None, ExtraItem, Throw, ThrowWithCompletedEvidence }

    private sealed class CompletedThenFaultClient(FaultMode mode, Exception? fault = null, Action? beforeFault = null) : ILlmStreamingClient
    {
        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The wrapper must enumerate this fixture.");

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var recorder = LlmAttemptRecorder.Start(request, "fixture", "model");
            recorder.Observe(providerRequestId: "provider-request", providerResponseId: "provider-response",
                httpStatusCode: 200, usage: new LlmUsage(PromptTokens: 11), providerStatus: "completed",
                dispatchCertainty: LlmDispatchCertainty.ResponseObserved);
            var evidence = recorder.Finish(LlmAttemptOutcome.Succeeded);
            yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                Response: new LlmResponse("fixture", "model", "synthetic answer") { AttemptEvidence = evidence });
            await Task.Yield();
            beforeFault?.Invoke();
            if (mode == FaultMode.ExtraItem)
                yield return new LlmStreamEvent(LlmStreamEventKind.Activity, "unexpected late event");
            if (mode == FaultMode.ThrowWithCompletedEvidence)
                ((LlmClientException)fault!).RetainAttemptEvidence(evidence);
            if (mode is FaultMode.Throw or FaultMode.ThrowWithCompletedEvidence) throw fault!;
        }
    }
}

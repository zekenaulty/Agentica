namespace Agentica.Clients.Llm;

internal static class LlmStreamCompletion
{
    public static async Task<LlmResponse> GenerateAsync(
        ILlmClient client,
        LlmRequest request,
        Action<LlmStreamEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        if (onEvent is null)
        {
            return await client.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (client is not ILlmStreamingClient streaming)
        {
            throw new ArgumentException(
                "A streaming observer requires an ILlmStreamingClient.", nameof(client));
        }

        LlmResponse? completed = null;
        var evidenceAtEntry = request.AttemptCapture?.Latest;
        LlmAttemptEvidence? observedEvidence = null;
        LlmWebSearchEvidence? observedSearch = null;
        LlmWebSearchEvidence? FailureSearch(LlmWebSearchEvidence? exceptionSearch = null) =>
            exceptionSearch ?? (evidenceAtEntry is null ? request.AttemptCapture?.LatestWebSearch : null) ??
            observedSearch ?? completed?.WebSearch;
        var observerHealthy = true;
        void Report(LlmStreamEvent item)
        {
            if (!observerHealthy) return;
            try
            {
                onEvent(item);
            }
            catch (Exception exception) when (ClientExceptionBoundary.IsRecoverable(exception))
            {
                // Telemetry is observational. A broken callback must not change provider
                // completion or mask the provider failure; stop using it for this call.
                observerHealthy = false;
            }
        }

        LlmAttemptEvidence? FailureEvidence(LlmAttemptOutcome outcome, string reasonCode,
            LlmAttemptEvidence? exceptionEvidence = null)
        {
            // A capture already populated at entry can belong to a previous invocation.
            var captureEvidence = evidenceAtEntry is null ? request.AttemptCapture?.Latest : null;
            var evidence = exceptionEvidence ?? captureEvidence ?? observedEvidence ?? completed?.AttemptEvidence;
            if (completed?.AttemptEvidence is not { } completionEvidence) return evidence;
            var corrected = request.AttemptCapture?.TryCorrectOwnedCompletion(completionEvidence, outcome, reasonCode);
            if (exceptionEvidence is { Outcome: not LlmAttemptOutcome.Succeeded }) return exceptionEvidence;
            if (corrected is not null) return corrected;
            // A producer may already have published a later terminal failure during disposal.
            if (captureEvidence is not null && ReferenceEquals(captureEvidence.References, completionEvidence.References) &&
                captureEvidence.Outcome is not (LlmAttemptOutcome.InProgress or LlmAttemptOutcome.Succeeded))
                return captureEvidence;
            // No supplied capture is necessary to retain a completed response's facts
            // on a wrapper-generated LlmClientException. This snapshot is detached.
            return LlmAttemptCapture.CorrectCompletionSnapshot(completionEvidence, outcome, reasonCode);
        }

        Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        var delivered = false;
        try
        {
            await foreach (var item in streaming.StreamAsync(request, cancellationToken)
                .ConfigureAwait(false))
            {
                observedSearch = item.WebSearch ?? item.Response?.WebSearch ?? observedSearch;
                if (completed is not null)
                {
                    item.Response?.NativeContinuation?.Dispose();
                    throw Incomplete("data_after_completion");
                }
                if (item.Kind == LlmStreamEventKind.Completed)
                {
                    completed = item.Response ?? throw Incomplete("missing_terminal_response");
                    observedEvidence = completed.AttemptEvidence ?? item.AttemptEvidence ?? observedEvidence;
                }
                else
                {
                    observedEvidence = item.AttemptEvidence ?? observedEvidence;
                    Report(item);
                }
            }
            if (completed is null) throw Incomplete("stream_incomplete");
            Report(new LlmStreamEvent(LlmStreamEventKind.Completed,
                Response: completed)
            { AttemptEvidence = completed.AttemptEvidence, WebSearch = completed.WebSearch });
            delivered = true;
            return completed;
        }
        catch (OperationCanceledException)
        {
            Report(new LlmStreamEvent(LlmStreamEventKind.Cancelled)
            {
                AttemptEvidence = FailureEvidence(LlmAttemptOutcome.Cancelled,
                cancellationToken.IsCancellationRequested ? "caller_cancelled" : "operation_cancelled"),
                WebSearch = FailureSearch()
            });
            throw;
        }
        catch (LlmClientException exception)
        {
            var reason = exception.ErrorClass is "data_after_completion" or "missing_terminal_response" or "stream_incomplete"
                ? exception.ErrorClass : "stream_failure";
            var evidence = FailureEvidence(LlmAttemptOutcome.Failed, reason, exception.AttemptEvidence);
            if (evidence is not null) exception.RetainAttemptEvidence(evidence, completed?.AttemptEvidence);
            exception.RetainWebSearch(FailureSearch(exception.WebSearch));
            Report(new LlmStreamEvent(LlmStreamEventKind.Failed,
                exception.ErrorClass ?? "provider_failure")
            { AttemptEvidence = evidence, WebSearch = FailureSearch(exception.WebSearch) });
            throw;
        }
        catch
        {
            Report(new LlmStreamEvent(LlmStreamEventKind.Failed,
                "stream_failure")
            { AttemptEvidence = FailureEvidence(LlmAttemptOutcome.Failed, "stream_failure"), WebSearch = FailureSearch() });
            throw;
        }
        finally
        {
            if (!delivered) completed?.NativeContinuation?.Dispose();
        }
    }

    private static LlmClientException Incomplete(string code) =>
        new("unknown", $"Provider stream failed: {code}.",
            errorKind: LlmClientErrorKind.Transient, errorClass: code);
}

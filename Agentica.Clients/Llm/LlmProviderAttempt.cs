using System.Runtime.CompilerServices;

namespace Agentica.Clients.Llm;

/// <summary>Separates observed provider facts from successful client delivery.</summary>
internal static class LlmProviderAttempt
{
    internal static async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        string providerName, string modelId,
        Func<LlmAttemptRecorder, IAsyncEnumerable<LlmStreamEvent>> streamFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recorder = LlmAttemptRecorder.Start(request, providerName, modelId);
        if (request.WebSearch is not null && providerName != "openai")
            throw Failure(recorder, providerName, "Provider-native web search is not implemented for this route.", "unsupported_web_search");
        IAsyncEnumerator<LlmStreamEvent>? enumerator = null;
        LlmStreamEvent? completed = null;
        var delivered = false;
        try
        {
            enumerator = streamFactory(recorder).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (ClientExceptionBoundary.IsRecoverable(exception))
                {
                    throw RetainedException(recorder, exception);
                }
                if (!moved) break;
                var item = enumerator.Current;
                if (item.Kind == LlmStreamEventKind.Completed)
                {
                    if (completed is not null || item.Response is null)
                    {
                        if (!ReferenceEquals(item.Response?.NativeContinuation, completed?.Response?.NativeContinuation))
                            item.Response?.NativeContinuation?.Dispose();
                        throw Failure(recorder, providerName, "Invalid terminal stream event.", "invalid_completion");
                    }
                    completed = item;
                    recorder.Observe(usage: item.Response.Usage, finishReason: item.Response.FinishReason);
                    continue;
                }
                if (completed is not null)
                    throw Failure(recorder, providerName, "Stream data followed completion.", "data_after_completion");
                yield return item with { AttemptEvidence = recorder.Evidence, WebSearch = recorder.WebSearch };
            }
            try
            {
                var finished = enumerator;
                enumerator = null;
                await finished.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (ClientExceptionBoundary.IsRecoverable(exception))
            {
                throw RetainedException(recorder, exception);
            }
            if (completed?.Response is not { } response)
            {
                var failure = new LlmClientException(providerName, "Stream ended without completion.",
                    errorKind: LlmClientErrorKind.Transient, errorClass: "stream_incomplete");
                RetainFailure(recorder, failure);
                throw failure;
            }
            var evidence = recorder.Finish(LlmAttemptOutcome.Succeeded);
            delivered = true;
            yield return completed with
            {
                Response = response with { AttemptEvidence = evidence, WebSearch = recorder.WebSearch },
                AttemptEvidence = evidence,
                WebSearch = recorder.WebSearch
            };
        }
        finally
        {
            try
            {
                if (enumerator is not null)
                    await DisposeAsync(enumerator, recorder).ConfigureAwait(false);
            }
            finally
            {
                if (!delivered) completed?.Response?.NativeContinuation?.Dispose();
                if (recorder.Evidence.Outcome == LlmAttemptOutcome.InProgress)
                    recorder.Finish(cancellationToken.IsCancellationRequested ? LlmAttemptOutcome.Cancelled :
                        LlmAttemptOutcome.Abandoned, cancellationToken.IsCancellationRequested ? "cancelled" : "stream_disposed");
            }
        }
    }

    private static async ValueTask DisposeAsync(IAsyncEnumerator<LlmStreamEvent> enumerator,
        LlmAttemptRecorder recorder)
    {
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (ClientExceptionBoundary.IsRecoverable(exception))
        {
            var alreadyFailed = recorder.Evidence.Outcome is LlmAttemptOutcome.Failed or
                LlmAttemptOutcome.Cancelled or LlmAttemptOutcome.Rejected;
            var retained = RetainedException(recorder, exception);
            if (!alreadyFailed) throw retained;
        }
    }

    private static LlmClientException Failure(LlmAttemptRecorder recorder, string provider,
        string message, string code)
    {
        var exception = new LlmClientException(provider, message, errorClass: code);
        RetainFailure(recorder, exception);
        return exception;
    }

    private static Exception RetainedException(LlmAttemptRecorder recorder, Exception exception)
    {
        RetainFailure(recorder, exception);
        if (exception is OperationCanceledException or LlmClientException) return exception;
        var failure = new LlmClientException(recorder.Evidence.ProviderName,
            "Provider stream could not be delivered completely.", exception,
            exception is IOException ? LlmClientErrorKind.Network : LlmClientErrorKind.Unknown,
            errorClass: "stream_delivery_failed")
        { AttemptEvidence = recorder.Evidence };
        failure.RetainWebSearch(recorder.WebSearch);
        return failure;
    }

    internal static void RetainFailure(LlmAttemptRecorder recorder, Exception exception)
    {
        var evidence = recorder.Finish(exception is OperationCanceledException ? LlmAttemptOutcome.Cancelled :
            recorder.Evidence.DispatchCertainty == LlmDispatchCertainty.NotDispatched ?
                LlmAttemptOutcome.Rejected : LlmAttemptOutcome.Failed,
            exception is OperationCanceledException ? "cancelled" : "client_failure");
        if (exception is LlmClientException failure)
        {
            failure.RetainAttemptEvidence(evidence);
            failure.RetainWebSearch(recorder.WebSearch);
        }
    }
}

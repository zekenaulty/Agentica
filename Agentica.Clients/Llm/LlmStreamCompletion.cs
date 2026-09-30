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

        Report(new LlmStreamEvent(LlmStreamEventKind.Started));
        try
        {
            await foreach (var item in streaming.StreamAsync(request, cancellationToken)
                .ConfigureAwait(false))
            {
                if (completed is not null)
                {
                    throw Incomplete("data_after_completion");
                }
                if (item.Kind == LlmStreamEventKind.Completed)
                {
                    completed = item.Response ?? throw Incomplete("missing_terminal_response");
                }
                else
                {
                    Report(item);
                }
            }
            if (completed is null) throw Incomplete("stream_incomplete");
        }
        catch (OperationCanceledException)
        {
            Report(new LlmStreamEvent(LlmStreamEventKind.Cancelled));
            throw;
        }
        catch (LlmClientException exception)
        {
            Report(new LlmStreamEvent(LlmStreamEventKind.Failed,
                exception.ErrorClass ?? "provider_failure"));
            throw;
        }
        catch
        {
            Report(new LlmStreamEvent(LlmStreamEventKind.Failed,
                "stream_failure"));
            throw;
        }
        Report(new LlmStreamEvent(LlmStreamEventKind.Completed,
            Response: completed));
        return completed;
    }

    private static LlmClientException Incomplete(string code) =>
        new("unknown", $"Provider stream failed: {code}.",
            errorKind: LlmClientErrorKind.Transient, errorClass: code);
}

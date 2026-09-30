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
        await foreach (var item in streaming.StreamAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            if (completed is not null)
            {
                throw Incomplete("data_after_completion");
            }
            onEvent(item);
            if (item.Kind == LlmStreamEventKind.Completed)
            {
                completed = item.Response ?? throw Incomplete("missing_terminal_response");
            }
        }

        return completed ?? throw Incomplete("stream_incomplete");
    }

    private static LlmClientException Incomplete(string code) =>
        new("unknown", $"Provider stream failed: {code}.",
            errorKind: LlmClientErrorKind.Transient, errorClass: code);
}

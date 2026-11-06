namespace Agentica.Clients.Llm;

/// <summary>One transport send. Injected handler chains remain the host's responsibility.</summary>
internal static class LlmProviderTransport
{
    internal static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    });

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        HttpRequestMessage message, LlmRequest request, LlmAttemptRecorder recorder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        recorder.Observe(dispatchCertainty: LlmDispatchCertainty.MayHaveDispatched);
        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        recorder.Observe(httpStatusCode: (int)response.StatusCode,
            providerRequestId: ReadIdentifier(response, "x-request-id") ??
                ReadIdentifier(response, "request-id") ?? ReadIdentifier(response, "x-goog-request-id"),
            dispatchCertainty: LlmDispatchCertainty.ResponseObserved);
        return response;
    }

    private static string? ReadIdentifier(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var value = values.FirstOrDefault();
        return value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl) ? value : null;
    }
}

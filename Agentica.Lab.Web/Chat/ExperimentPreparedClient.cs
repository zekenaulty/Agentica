using System.Runtime.CompilerServices;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.Lab.Web.Chat;

/// <summary>Every chat and planner/repair call freezes, counts and admits the shared request.
/// The planner still owns its run-local native session; Lab owns turn custody and storage.</summary>
internal sealed class ExperimentPreparedClient(OpenAiResponsesClientOptions options, HttpClient observed,
    ExperimentProviderTransport transport, ExperimentExecutionContext context, string purpose) : ILlmStreamingClient
{
    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        LlmResponse? completed = null;
        await foreach (var item in StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (item.Kind == LlmStreamEventKind.Completed) completed = item.Response;
        return completed ?? throw new LlmClientException("openai", "No completed response.", errorClass: "stream_incomplete");
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var prepared = LlmCallPreparation.Prepare(request, options, transport.NextReferences(purpose),
            context.ConversationId, observed);
        try
        {
            await context.Trace(new("provider.configuration", new
            {
                prepared.Descriptor.ConfigurationIdentity,
                prepared.Descriptor.RequestIdentity,
                requestedModel = request.ModelId,
                effectiveModel = prepared.Descriptor.ModelId,
                transcriptOnly = request.OpenAiHistoryMode == OpenAiHistoryMode.TranscriptOnly
            }, prepared.Descriptor.References.AttemptId)).ConfigureAwait(false);
            await foreach (var item in prepared.StreamAsync(transport.Admission(prepared, purpose), cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally { await transport.RecordOutcomeAsync(prepared.Capture.Latest, prepared.Capture.LatestWebSearch).ConfigureAwait(false); }
    }
}

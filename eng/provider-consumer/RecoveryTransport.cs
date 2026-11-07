using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.ProviderConsumer;

internal enum FixtureBehavior { ModelDecision, LostResponse, HttpFailure, Refusal, InvalidHostJson, MissingUsage, CancelAfterSuccess, MemoryDecision, DocumentDecision, Search }

// The complete handler chain is this terminal counted fixture. It cannot access a network,
// redirect or retry. The durable host admission has already executed when SendAsync enters.
internal sealed class RecoveryTransport(EpisodeStore store, EpisodeState state, string attemptId,
    FixtureBehavior behavior, CancellationTokenSource? caller = null, LlmRequest? expectedRequest = null) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check.That(request.Method == HttpMethod.Post && request.RequestUri == RecoveryHost.Options.Endpoint, "offline fixture endpoint");
        Check.That(state.Attempts.ContainsKey(attemptId), "durable reservation exists before POST");
        Check.That(!state.PhysicalSends.ContainsKey(attemptId), "one physical send per attempt");
        var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        using var body = JsonDocument.Parse(bytes);
        var root = body.RootElement;
        Check.Equal(state.Attempts[attemptId].Descriptor.ModelId, root.GetProperty("model").GetString());
        Check.That(root.GetProperty("stream").GetBoolean() && !root.GetProperty("store").GetBoolean() &&
            !root.TryGetProperty("previous_response_id", out _), "fully stateless streaming");
        var input = root.GetProperty("input");
        if (behavior != FixtureBehavior.Search)
        {
            var schema = root.GetProperty("text").GetProperty("format");
            Check.Equal("json_schema", schema.GetProperty("type").GetString());
            Check.That(schema.GetProperty("strict").GetBoolean(), "supplied strict schema");
            if (expectedRequest?.StructuredOutput?.JsonSchema is { } suppliedSchema)
            {
                using var expected = JsonDocument.Parse(suppliedSchema);
                Check.That(JsonElement.DeepEquals(expected.RootElement, schema.GetProperty("schema")), "exact supplied schema reaches physical wire");
            }
        }
        if (expectedRequest is { NativeContinuation: null })
        {
            var authored = expectedRequest.Messages.Where(message => message.Role is not (LlmMessageRole.System or LlmMessageRole.Developer)).ToArray();
            Check.Equal(authored.Length, input.GetArrayLength());
            for (var index = 0; index < authored.Length; index++)
            {
                Check.Equal(authored[index].Role.ToString().ToLowerInvariant(), input[index].GetProperty("role").GetString());
                Check.Equal(authored[index].Content, input[index].GetProperty("content").GetString());
            }
            if (state.AcceptedTranscript.Count > 0) state.ReconstructedEvidenceSends++;
            if (state.AcceptedTranscript.Count > 0 && expectedRequest.StructuredOutput?.JsonSchema == BridgeContractFixtures.Foreground)
            {
                using var projection = JsonDocument.Parse(authored[0].Content);
                var accepted = projection.RootElement.GetProperty("accepted");
                Check.Equal(state.AcceptedTranscript.Count, accepted.GetArrayLength());
                for (var index = 0; index < state.AcceptedTranscript.Count; index++)
                {
                    var original = state.AcceptedTranscript[index];
                    Check.Equal(original.Author.ToString(), accepted[index].GetProperty("author").GetString());
                    Check.Equal(original.ProviderRole.ToString(), accepted[index].GetProperty("provider_role").GetString());
                    Check.Equal(original.Id, accepted[index].GetProperty("Id").GetString());
                    Check.Equal(original.Revision, accepted[index].GetProperty("Revision").GetString());
                    Check.Equal(original.SourceAttemptId, accepted[index].GetProperty("SourceAttemptId").GetString());
                }
                Check.That(accepted.EnumerateArray().Any(item => item.GetProperty("author").GetString() == "Host" &&
                    item.GetProperty("provider_role").GetString() == "User"), "Host semantic author survives user envelope");
            }
        }
        else if (expectedRequest?.NativeContinuation is not null)
        {
            Check.That(input.EnumerateArray().Any(item => item.TryGetProperty("encrypted_content", out _)), "private native replay reaches actual wire");
            state.NativeReplaySends++;
        }
        state.PhysicalSends.Add(attemptId, 1);
        store.Save(state); // Models lost acknowledgement after an actual counted physical send.
        if (behavior == FixtureBehavior.LostResponse) throw new HttpRequestException("synthetic_lost_response");
        if (behavior == FixtureBehavior.HttpFailure)
        {
            var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("{\"error\":{\"code\":\"rate_limit\"},\"usage\":{\"input_tokens\":11,\"output_tokens\":5,\"total_tokens\":16}}", Encoding.UTF8, "application/json") };
            failure.Headers.Add("x-request-id", "http-failed-request");
            return failure;
        }
        var last = input.EnumerateArray().Last(item => item.TryGetProperty("role", out var role) && role.GetString() == "user");
        var text = last.GetProperty("content").GetString()!;
        string answer;
        switch (behavior)
        {
            case FixtureBehavior.MemoryDecision:
                answer = "{\"actions\":[],\"abstention_reason\":\"Synthetic evidence is insufficient.\",\"summary\":\"No adopted memory.\"}";
                break;
            case FixtureBehavior.DocumentDecision:
                answer = "{\"answer\":\"Synthetic source says blue.\",\"cited_fragment_ids\":[\"fragment-1\"],\"exact_quotes\":[{\"fragment_id\":\"fragment-1\",\"quote\":\"blue\"}]}";
                break;
            case FixtureBehavior.Search:
                answer = "**source**";
                break;
            case FixtureBehavior.InvalidHostJson:
                answer = "{\"decision\":\"action\",\"candidate\":null,\"action\":null,\"unexpected\":true}";
                break;
            default:
                // The synthetic model chooses from its supplied observation. The host never
                // hardcodes a tool schedule and independently checks every proposal/effect.
                using (var context = JsonDocument.Parse(text))
                {
                    var observation = context.RootElement;
                    var pending = observation.GetProperty("pending");
                    answer = pending.GetArrayLength() > 0
                        ? JsonSerializer.Serialize(new { decision = "action", candidate = (object?)null,
                            action = new { capability_ref = "accept-item", input = new { id = pending[0].GetString(), scope = observation.GetProperty("scope").GetString(), version = observation.GetProperty("version").GetInt32() } } })
                        : "{\"decision\":\"reply\",\"candidate\":{\"reply\":\"Both accepted.\"},\"action\":null}";
                }
                break;
        }
        object content = behavior == FixtureBehavior.Refusal
            ? new { type = "refusal", refusal = "synthetic refusal" }
            : new { type = "output_text", text = answer, annotations = behavior == FixtureBehavior.Search
                ? new object[] { new { type = "url_citation", url = "https://example.com/fixture", title = "Synthetic source", start_index = 0, end_index = 10 } }
                : [] };
        var output = new List<object>
        {
            new { type = "reasoning", id = "reasoning-" + attemptId, encrypted_content = "synthetic-private-native-" + attemptId, summary = Array.Empty<object>() }
        };
        if (behavior == FixtureBehavior.Search) output.Add(new
        {
            type = "web_search_call", id = "search-" + attemptId, status = "completed",
            action = new { type = "search", query = "synthetic query", sources = new[] { new { type = "url", url = "https://example.com/fixture", title = "Synthetic source" } } }
        });
        output.Add(new { type = "message", id = "message-" + attemptId, role = "assistant", status = "completed", content = new[] { content } });
        var terminal = new Dictionary<string, object?>
        {
            ["id"] = "response-" + attemptId, ["model"] = root.GetProperty("model").GetString(), ["status"] = "completed", ["output"] = output,
            ["usage"] = behavior == FixtureBehavior.MissingUsage ? null : new { input_tokens = 11, output_tokens = 7, total_tokens = 18, output_tokens_details = new { reasoning_tokens = 3 } }
        };
        var stream = Event(new { type = "response.created", response = new { id = "response-" + attemptId } }) +
            Event(new { type = "response.reasoning_summary_text.delta", delta = "Synthetic public summary." }) +
            (behavior == FixtureBehavior.Refusal ? "" : Event(new { type = "response.output_text.delta", delta = answer })) +
            Event(new { type = "response.completed", response = terminal });
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-request-id", "request-" + attemptId);
        response.Content = behavior == FixtureBehavior.CancelAfterSuccess
            ? new StreamContent(new CancelAtEndStream(stream, caller!))
            : new StringContent(stream, Encoding.UTF8, "text/event-stream");
        return response;
    }

    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private sealed class CancelAtEndStream(string body, CancellationTokenSource caller)
        : MemoryStream(Encoding.UTF8.GetBytes(body))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                caller.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

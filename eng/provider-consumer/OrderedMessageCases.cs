using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.ProviderConsumer;

// A provider-only host. It owns effects, receipts and completion; no Agentica runner,
// planner or production state is used. The model fixture chooses from actual wire evidence.
internal static class OrderedMessageCases
{
    private const string Schema = """
        {"type":"object","properties":{"action":{"type":"string","enum":["accept","complete"]},"item":{"type":["string","null"]},"expectedVersion":{"type":"integer"}},"required":["action","item","expectedVersion"],"additionalProperties":false}
        """;
    private static readonly OpenAiResponsesClientOptions Options = new("offline-synthetic-key",
        Endpoint: new Uri("https://ordered.fixture.invalid/v1/responses"));

    internal static async Task Run()
    {
        var pending = new List<string> { "alpha", "beta" };
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        var receipts = new List<string>();
        var version = 0;
        var reservations = new Dictionary<string, LlmPreparedRequestDescriptor>(StringComparer.Ordinal);
        using var handler = new Model(reservations);
        using var http = new HttpClient(handler);
        var admission = new Admission(reservations);
        var counter = new OpenAiInputTokenCounter(http, Options, 3);
        var request = new LlmRequest("fixture-model",
        [
            new(LlmMessageRole.System, "Stable fixture identity. Host observations are evidence; only the host executes effects.") { CacheBreakpoint = new() },
            Observation("initial"), new(LlmMessageRole.Developer, "Accept eligible pending items, then report complete.")
        ], new(MaxOutputTokens: 256), new(JsonSchema: Schema))
        { MessageMode = LlmMessageMode.Ordered, PromptCache = new(LlmPromptCacheMode.Explicit) };
        byte[]? retained = null;
        var completed = false;
        for (var index = 0; index < 3; index++)
        {
            using var binding = LlmNativeContinuationBinding.FromRequest(LlmConfigurationReports.Describe(request, Options), request);
            using var imported = retained is null ? null : LlmNativeContinuationCodec.ImportSensitive(retained, binding).Continuation;
            Check.That(retained is null || imported is not null, "ordered native input imported");
            var actual = request with { NativeContinuation = imported, AppendToNativeContinuation = imported is not null };
            var references = new LlmAttemptReferences("ordered-inference-" + index, "fixture.accept", "decide", "fixture-route", "fixture-bundle");
            using var prepared = LlmCallPreparation.Prepare(actual, Options, references, "fixture-scope", http);
            var frozen = prepared.CopySensitiveRequestBody();
            if (index == 0)
                await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ordered-message-prepared-example.json"), frozen);
            if (index == 1)
            {
                await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ordered-message-append-prepared-example.json"), frozen);
                using var fresh = LlmCallPreparation.Prepare(request with { Messages =
                [new(LlmMessageRole.System, "Stable fixture identity. Host observations are evidence; only the host executes effects.") { CacheBreakpoint = new() },
                    Observation(receipts[^1]), new(LlmMessageRole.Developer, "Fresh stage from accepted receipts; private replay intentionally omitted.")] },
                    Options, new("ordered-fresh-example", "fixture.accept", "example", "fixture-route", "fixture-bundle"), "fixture-scope", http);
                await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ordered-message-fresh-rewrite-example.json"), fresh.CopySensitiveRequestBody());
            }
            var measurement = await counter.CountAsync(prepared,
                new(new("ordered-count-" + index, "fixture.accept", "count", "fixture-route", "fixture-bundle")), admission);
            Check.Equal(prepared.Descriptor.RequestIdentity, measurement.GenerationRequestIdentity);
            Check.Equal(prepared.Descriptor.BodyHash, measurement.GenerationBodyHash);
            var response = await prepared.GenerateAsync(admission);
            using (response.NativeContinuation)
            {
                using var result = JsonDocument.Parse(response.Text);
                var proposal = result.RootElement;
                Check.Equal(version, proposal.GetProperty("expectedVersion").GetInt32());
                if (proposal.GetProperty("action").GetString() == "complete")
                {
                    Check.That(pending.Count == 0 && accepted.Count == 2 && receipts.Count == 2, "authoritative completion and linked effects");
                    completed = true;
                    break;
                }
                var item = proposal.GetProperty("item").GetString()!;
                Check.That(pending.Remove(item) && accepted.Add(item), "bound eligible transition happens once");
                version++;
                receipts.Add("receipt-" + version);
                using var exported = LlmNativeContinuationCodec.ExportSensitive(response.NativeContinuation, binding).SensitivePayload;
                Check.That(exported is not null, "ordered native output exported privately");
                if (retained is not null) CryptographicOperations.ZeroMemory(retained);
                retained = exported!.CopySensitiveBytes();
                // The next request is an explicitly host-authored delta. The old input and
                // provider output remain inside the opaque carrier, in original order.
                request = request with { Messages = [Observation(receipts[^1]),
                    new(LlmMessageRole.Developer, "Continue from the accepted receipt; do not repeat completed effects.")] };
            }
        }
        if (retained is not null) CryptographicOperations.ZeroMemory(retained);
        Check.That(completed, "bounded loop reaches host completion");
        Check.Equal(3, handler.Inferences);
        Check.Equal(3, handler.Counts);
        Check.Equal(6, reservations.Count);
        Console.WriteLine(JsonSerializer.Serialize(new { scenario = "ordered_native_provider_only_decide_act_receipt_continue", result = "PASS",
            inferenceDecisions = handler.Inferences, countCalls = handler.Counts, acceptedEffects = accepted.Count,
            liveCalls = 0, installedBridge = "NOT-RUN", modelQuality = "NOT-RUN" }));

        LlmMessage Observation(string receipt) => new(LlmMessageRole.System,
            new XElement("host_observation", new XAttribute("source", "host"), new XAttribute("receipt", receipt),
                JsonSerializer.Serialize(new { pending, accepted = accepted.Order().ToArray(), version })).ToString(SaveOptions.DisableFormatting));
    }

    private sealed class Admission(Dictionary<string, LlmPreparedRequestDescriptor> reservations) : ILlmCallAdmission
    {
        public ValueTask AdmitAsync(LlmPreparedRequestDescriptor descriptor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check.That(reservations.TryAdd(descriptor.References.AttemptId, descriptor), "unique reservation before physical send");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Model(Dictionary<string, LlmPreparedRequestDescriptor> reservations) : HttpMessageHandler
    {
        internal int Inferences { get; private set; }
        internal int Counts { get; private set; }
        private readonly HashSet<string> _sent = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            var descriptor = reservations.Values.Single(item => item.BodyHash == hash);
            Check.That(_sent.Add(descriptor.References.AttemptId), "one send per admitted physical attempt");
            using var body = JsonDocument.Parse(bytes);
            var input = body.RootElement.GetProperty("input");
            Check.Equal("system", input[0].GetProperty("role").GetString());
            Check.Equal(1, input.EnumerateArray().Count(item =>
                item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(block => block.TryGetProperty("text", out var text) && text.GetString()!.StartsWith("Stable fixture identity", StringComparison.Ordinal))));
            Check.That(!input.EnumerateArray().Any(item => item.TryGetProperty("role", out var role) && role.GetString() == "user"), "no invented participant message");
            if (request.RequestUri!.AbsolutePath.EndsWith("input_tokens", StringComparison.Ordinal))
            {
                Counts++;
                Check.Equal(LlmProviderWorkKind.InputCount, descriptor.WorkKind);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"object\":\"response.input_tokens\",\"input_tokens\":128}", Encoding.UTF8, "application/json") };
            }
            Inferences++;
            Check.That(body.RootElement.GetProperty("stream").GetBoolean() && !body.RootElement.GetProperty("store").GetBoolean(), "stateless streaming");
            Check.That(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(), "strict host schema preserved");
            var observation = input.EnumerateArray().Where(item => item.TryGetProperty("role", out var role) && role.GetString() == "system")
                .Select(item => item.GetProperty("content")[0].GetProperty("text").GetString()!)
                .Last(text => text.StartsWith("<host_observation", StringComparison.Ordinal));
            using var state = JsonDocument.Parse(XElement.Parse(observation).Value);
            var pending = state.RootElement.GetProperty("pending");
            var proposal = JsonSerializer.Serialize(new { action = pending.GetArrayLength() > 0 ? "accept" : "complete",
                item = pending.GetArrayLength() > 0 ? pending[0].GetString() : null,
                expectedVersion = state.RootElement.GetProperty("version").GetInt32() });
            var response = JsonSerializer.Serialize(new { type = "response.completed", response = new
            { id = "ordered-response-" + Inferences, model = "fixture-model", status = "completed",
                output = new[] { new { type = "message", role = "assistant", phase = "final_answer", content = new[] { new { type = "output_text", text = proposal } } } },
                usage = new { input_tokens = 128, output_tokens = 32 } } });
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + response + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Providers;

namespace Agentica.ProviderLiveProof;

internal static class ProofSelfTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".agentica", "verification", "provider-live-proof-self-tests", Guid.NewGuid().ToString("N"));
        var cases = new (string Name, Func<string, Task> Execute)[]
        {
            ("reservation-before-send-and-inclusive-reasoning", Successful),
            ("physical-inference-cap-survives-reopen", InferenceLimit),
            ("count-cap-and-excess-input", CountLimits),
            ("dollar-reservation-denies-before-inference", DollarLimit),
            ("required-journal-failure-zero-posts", JournalFailure),
            ("full-generation-hash-is-required", StaleHash),
            ("fabricated-persisted-native-count-link-denied", InvalidPersistedCount),
            ("missing-usage-retains-reservation", MissingUsage),
            ("late-error-retains-known-usage-and-reservation", LateError),
            ("contradictory-usage-preserves-maxima-and-blocks-further-work", ContradictoryUsage),
            ("lost-response-is-not-replayed-after-reopen", LostResponse),
            ("cancellation-after-terminal-retains-reservation", Cancellation),
            ("unapproved-route-and-controls-denied", Controls),
            ("three-phase-harness-bounds-native-replay", HarnessBudgetSelfTest.RunAsync)
        };
        foreach (var item in cases)
        {
            try { await item.Execute(Path.Combine(root, item.Name, "ledger.json")).ConfigureAwait(false); Console.WriteLine("PASS " + item.Name); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Console.Error.WriteLine("FAIL " + item.Name + ": " + error.GetType().Name); return 1; }
        }
        Console.WriteLine($"{cases.Length} offline guard cases passed. No network, credentials or host startup. Evidence: {root}");
        return 0;
    }

    private static async Task Successful(string path)
    {
        DurableProviderGuard? guard = null;
        using var handler = new Fake((request, _) =>
        {
            var before = guard!.Snapshot();
            Require(before.Attempts.Last().State == "dispatch-possible", "Reservation was not durable before physical send.");
            Require(File.ReadAllText(path).Contains(before.Attempts.Last().Id, StringComparison.Ordinal), "Physical send preceded persistence.");
            return Task.FromResult(IsCount(request) ? Count() : Stream(Terminal()));
        });
        using var owned = new DurableProviderGuard(path, handler); guard = owned;
        using var http = new HttpClient(guard) { Timeout = Timeout.InfiniteTimeSpan };
        using var prepared = Prepare(http, "first"); await Measure(http, guard, prepared);
        var response = await prepared.GenerateAsync(new Admission()); response.NativeContinuation?.Dispose();
        var observed = guard.Snapshot().Attempts.Single(item => item.Kind == "inference");
        Require(observed.InputTokens == 73 && observed.OutputTokens == 8 && observed.ReasoningTokens == 3, "Known usage missing.");
        Require(observed.SettledUsd == 73 * .25m / 1_000_000m + 8 * .75m / 1_000_000m, "Reasoning was billed twice.");
        var ledger = File.ReadAllText(path);
        Require(!ledger.Contains("fixture-key", StringComparison.Ordinal) && !ledger.Contains("opaque-secret", StringComparison.Ordinal) &&
            !ledger.Contains("private-thought", StringComparison.Ordinal) && !ledger.Contains("Synthetic answer", StringComparison.Ordinal), "Private material entered ledger.");
    }

    private static async Task InferenceLimit(string path)
    {
        var limits = new ProofLimits(InferenceCalls: 1, CountCalls: 2);
        using (var handler = new Fake((request, _) => Task.FromResult(IsCount(request) ? Count() : Stream(Terminal()))))
        using (var guard = new DurableProviderGuard(path, handler, limits))
        using (var http = new HttpClient(guard))
        {
            using var one = Prepare(http, "one"); await Measure(http, guard, one); (await one.GenerateAsync(new Admission())).NativeContinuation?.Dispose();
            using var two = Prepare(http, "two"); await Measure(http, guard, two); await Throws(() => two.GenerateAsync(new Admission()));
            Require(handler.Inference == 1 && handler.Counts == 2, "Inference cap not physical.");
        }
        using var nextHandler = new Fake((_, _) => throw new InvalidOperationException("Must not send after reopen."));
        using var reopened = new DurableProviderGuard(path, nextHandler, limits);
        using var nextHttp = new HttpClient(reopened);
        using var again = Prepare(nextHttp, "two"); await Throws(() => again.GenerateAsync(new Admission()));
        Require(reopened.Snapshot().Attempts.Count == 3 && nextHandler.Inference == 0, "Restart reset cumulative capacity.");
    }

    private static async Task CountLimits(string path)
    {
        using var handler = new Fake((_, _) => Task.FromResult(Count()));
        using var guard = new DurableProviderGuard(path, handler, new(CountCalls: 1));
        using var http = new HttpClient(guard);
        using var one = Prepare(http, "one"); await Measure(http, guard, one);
        using var two = Prepare(http, "two"); await Throws(() => Measure(http, guard, two));
        Require(handler.Counts == 1 && handler.Inference == 0, "Count cap not physical.");
        using var highHandler = new Fake((_, _) => Task.FromResult(Count(8001)));
        using var highGuard = new DurableProviderGuard(path + ".high", highHandler);
        using var highHttp = new HttpClient(highGuard);
        using var high = Prepare(highHttp, "over-input"); await Throws(() => Measure(highHttp, highGuard, high));
        await Throws(() => high.GenerateAsync(new Admission()));
        Require(highHandler.Counts == 1 && highHandler.Inference == 0, "Excess native input was dispatched.");
    }

    private static async Task DollarLimit(string path)
    {
        using var handler = new Fake((_, _) => Task.FromResult(Count()));
        using var guard = new DurableProviderGuard(path, handler, new(MaximumUsd: .000001m));
        using var http = new HttpClient(guard); using var prepared = Prepare(http, "budget");
        await Measure(http, guard, prepared); await Throws(() => prepared.GenerateAsync(new Admission()));
        Require(handler.Counts == 1 && handler.Inference == 0 && guard.HeldUsd == 0, "Dollar gate dispatched before reservation.");
    }

    private static async Task JournalFailure(string path)
    {
        using var handler = new Fake((_, _) => Task.FromResult(Count()));
        using var guard = new DurableProviderGuard(path, handler); using var http = new HttpClient(guard);
        Directory.CreateDirectory(path + ".next");
        using var prepared = Prepare(http, "blocked-journal"); await Throws(() => Measure(http, guard, prepared));
        Require(handler.Counts == 0 && handler.Inference == 0 && guard.Snapshot().Attempts.Count == 0, "Failed journal allowed a POST.");
    }

    private static async Task StaleHash(string path)
    {
        using var handler = new Fake((_, _) => Task.FromResult(Count()));
        using var guard = new DurableProviderGuard(path, handler); using var http = new HttpClient(guard);
        using var original = Prepare(http, "identical input"); await Measure(http, guard, original);
        using var changed = Prepare(http, "identical input", maximumOutput: 8191);
        await Throws(() => changed.GenerateAsync(new Admission()));
        Require(handler.Counts == 1 && handler.Inference == 0, "Filtered count identity substituted for complete generation identity.");
    }

    private static async Task MissingUsage(string path)
    {
        using var handler = new Fake((request, _) => Task.FromResult(IsCount(request) ? Count() : Stream(Terminal(includeUsage: false))));
        using var guard = new DurableProviderGuard(path, handler); using var http = new HttpClient(guard);
        using var prepared = Prepare(http, "unknown-usage"); await Measure(http, guard, prepared);
        (await prepared.GenerateAsync(new Admission())).NativeContinuation?.Dispose();
        var attempt = guard.Snapshot().Attempts.Single(item => item.Kind == "inference");
        Require(attempt.SettledUsd is null && attempt.ReservedUsd > 0 && attempt.State == "unresolved-reservation-retained", "Missing usage became zero.");
    }

    private static async Task InvalidPersistedCount(string path)
    {
        using (var handler = new Fake((_, _) => Task.FromResult(Count())))
        using (var guard = new DurableProviderGuard(path, handler))
        using (var http = new HttpClient(guard))
        {
            using var prepared = Prepare(http, "persisted-count");
            await Measure(http, guard, prepared);
        }
        var saved = JsonSerializer.Deserialize<ProviderLedger>(File.ReadAllBytes(path))!;
        saved.VerifiedInputs[0] = saved.VerifiedInputs[0] with { CountAttemptId = "unobserved-count" };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(saved));
        using var blockedHandler = new Fake((_, _) => throw new InvalidOperationException("No physical send allowed."));
        await Throws(() =>
        {
            using var reopened = new DurableProviderGuard(path, blockedHandler);
            return Task.CompletedTask;
        });
        Require(blockedHandler.Counts == 0 && blockedHandler.Inference == 0, "Fabricated persisted native count authorized dispatch.");
    }

    private static async Task LateError(string path)
    {
        using var handler = new Fake((request, _) => Task.FromResult(IsCount(request) ? Count() : Stream(Terminal() + "data: {\"type\":\"error\"}\n\n")));
        using var guard = new DurableProviderGuard(path, handler); using var http = new HttpClient(guard);
        using var prepared = Prepare(http, "late-error"); await Measure(http, guard, prepared); await Throws(() => prepared.GenerateAsync(new Admission()));
        var attempt = guard.Snapshot().Attempts.Single(item => item.Kind == "inference");
        Require(attempt.OutputTokens == 8 && attempt.ProviderStatus == "completed" && attempt.SettledUsd is null, "Late error erased facts or released uncertain reservation.");
    }

    private static async Task LostResponse(string path)
    {
        using (var handler = new Fake((request, _) => IsCount(request) ? Task.FromResult(Count()) : throw new HttpRequestException("Synthetic response loss")))
        using (var guard = new DurableProviderGuard(path, handler))
        using (var http = new HttpClient(guard))
        {
            using var prepared = Prepare(http, "lost"); await Measure(http, guard, prepared); await Throws(() => prepared.GenerateAsync(new Admission()));
            Require(guard.HeldUsd > 0, "Lost response released reservation.");
        }
        using var after = new Fake((_, _) => throw new InvalidOperationException("No replay allowed."));
        using var reopened = new DurableProviderGuard(path, after); using var nextHttp = new HttpClient(reopened);
        using var replay = Prepare(nextHttp, "lost"); await Throws(() => replay.GenerateAsync(new Admission()));
        Require(after.Inference == 0 && reopened.Snapshot().Attempts.Count == 2, "Restart replayed unresolved dispatch.");
    }

    private static async Task ContradictoryUsage(string path)
    {
        var earlier = "data: {\"type\":\"response.created\",\"response\":{\"id\":\"different-response\",\"status\":\"in_progress\",\"usage\":{\"input_tokens\":100000,\"output_tokens\":20}}}\n\n";
        using var handler = new Fake((request, _) => Task.FromResult(IsCount(request) ? Count() : Stream(earlier + Terminal())));
        using var guard = new DurableProviderGuard(path, handler);
        using var http = new HttpClient(guard);
        using var prepared = Prepare(http, "contradictory-usage");
        await Measure(http, guard, prepared);
        (await prepared.GenerateAsync(new Admission())).NativeContinuation?.Dispose();
        var observed = guard.Snapshot().Attempts.Single(item => item.Kind == "inference");
        Require(observed.InputTokens == 100000 && observed.OutputTokens == 20 && observed.ResponseId == "different-response" &&
            observed.SettledUsd is null && observed.State == "reservation-exceeded" && guard.HeldUsd > observed.ReservedUsd,
            "Conflicting terminal erased known exposure or released its reservation.");
        using var next = Prepare(http, "do-not-continue-after-exposure-overrun");
        await Throws(() => Measure(http, guard, next));
        Require(handler.Counts == 1 && handler.Inference == 1, "Further work followed a known reservation overrun.");
    }

    private static async Task Cancellation(string path)
    {
        using var pending = new WaitingStream(Terminal());
        using var handler = new Fake((request, _) =>
        {
            if (IsCount(request)) return Task.FromResult(Count());
            var content = new StreamContent(pending); content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var guard = new DurableProviderGuard(path, handler); using var http = new HttpClient(guard);
        using var prepared = Prepare(http, "cancel"); await Measure(http, guard, prepared);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var call = prepared.GenerateAsync(new Admission(), cancellation.Token);
        await pending.Waiting.Task.WaitAsync(cancellation.Token); cancellation.Cancel(); await Throws(async () => await call);
        var observed = guard.Snapshot().Attempts.Single(item => item.Kind == "inference");
        Require(observed.InputTokens == 73 && observed.ProviderStatus == "completed" && observed.SettledUsd is null, "Cancellation erased known usage or released reservation.");
    }

    private static async Task Controls(string path)
    {
        using var handler = new Fake((_, _) => throw new InvalidOperationException("No network allowed."));
        using var guard = new DurableProviderGuard(path, handler); using var http = new HttpClient(guard);
        foreach (var uri in new[] { "https://api.openai.com/v1/embeddings", "https://example.com/v1/responses" })
            await Throws(() => http.PostAsync(uri, new StringContent("{}")));
        var invalid = JsonSerializer.SerializeToUtf8Bytes(new { model = "other-model", input = "x", stream = true, store = false, max_output_tokens = 8192, reasoning = new { effort = "high" } });
        await Throws(() => { using var intent = guard.BeginCount(invalid); return Task.CompletedTask; });
        var nativeTool = JsonSerializer.SerializeToUtf8Bytes(new { model = "gpt-6-luna", input = "x", stream = true, store = false, max_output_tokens = 8192, reasoning = new { effort = "high" }, tools = new[] { new { type = "web_search" } }, max_tool_calls = 1 });
        await Throws(() => { using var intent = guard.BeginCount(nativeTool); return Task.CompletedTask; });
        Require(handler.Counts == 0 && handler.Inference == 0 && guard.Snapshot().Attempts.Count == 0, "Unapproved route or model dispatched.");
    }

    private static LlmPreparedCall Prepare(HttpClient http, string input, int maximumOutput = 8192) =>
        LlmCallPreparation.Prepare(new LlmRequest("gpt-6-luna", [new(LlmMessageRole.User, input)],
            new(MaxOutputTokens: maximumOutput, Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High)))
        { OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly }, new OpenAiResponsesClientOptions("fixture-key"),
            new(Guid.NewGuid().ToString("N")), "offline-proof", http);
    private static async Task Measure(HttpClient http, DurableProviderGuard guard, LlmPreparedCall prepared)
    {
        using var executor = new OpenAiExperimentExecutor(new(name => name == "OPENAI_API_KEY" ? "fixture-key" : null), http);
        var linked = new LinkedCountExecutor(executor, guard);
        using var counter = (IDisposable)linked.CreateInputTokenCounter(new() { MaxOutputTokens = 8192 });
        var body = prepared.CopySensitiveRequestBody();
        try { await ((IExperimentInputTokenCounter)counter).CountAsync(body); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(body); }
    }
    private static bool IsCount(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/input_tokens", StringComparison.Ordinal);
    private static HttpResponseMessage Count(int tokens = 73) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { @object = "response.input_tokens", input_tokens = tokens }), Encoding.UTF8, "application/json") };
    private static string Terminal(bool includeUsage = true)
    {
        var response = new Dictionary<string, object?>
        {
            ["id"] = "synthetic-response",
            ["status"] = "completed",
            ["output"] = new object[]
            {
                new { type = "reasoning", encrypted_content = "opaque-secret", content = "private-thought" },
                new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Synthetic answer" } } }
            }
        };
        if (includeUsage) response["usage"] = new { input_tokens = 73, output_tokens = 8, output_tokens_details = new { reasoning_tokens = 3 } };
        return "data: " + JsonSerializer.Serialize(new { type = "response.completed", response }) + "\n\n";
    }
    private static HttpResponseMessage Stream(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> action)
    { try { await action(); } catch (Exception error) when (error is not OutOfMemoryException) { return; } throw new InvalidOperationException("Expected rejection."); }
    private sealed class Admission : ILlmCallAdmission
    { public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken) => ValueTask.CompletedTask; }
    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Counts { get; private set; }
        public int Inference { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { if (IsCount(request)) Counts++; else Inference++; return send(request, cancellationToken); }
    }
    private sealed class WaitingStream(string initial) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(initial); private int _position;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _bytes.Length) { var count = Math.Min(buffer.Length, _bytes.Length - _position); _bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; return count; }
            Waiting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using Agentica.Clients.Embeddings;
using Agentica.Memory;
using Agentica.ProviderConsumer;
using System.Net;
using System.Text;
using System.Text.Json;

if (args is ["--resume", var statePath, var nativeMode])
{
    await RecoveryCases.ResumeProcess(statePath, nativeMode);
    return;
}
if (args is ["--return-before-accept", var pendingStatePath])
{
    await RecoveryCases.ReturnBeforeAcceptProcess(pendingStatePath);
    return;
}

var cases = new (string Name, Func<Task> Run)[]
{
    ("document_order_profile_and_source_mapping", () => Cases.Success(MemoryEmbeddingPurpose.Document)),
    ("query_purpose_and_source_mapping", () => Cases.Success(MemoryEmbeddingPurpose.Query)),
    ("required_reservation_rejects_before_transport", Cases.ReservationRejection),
    ("known_usage_survives_invalid_vector", Cases.InvalidVector),
    ("missing_input_usage_stays_unknown", Cases.MissingUsage),
    ("http_failure_retains_observed_evidence_without_retry", Cases.HttpFailure),
    ("cancellation_before_dispatch_is_free", Cases.CancellationBeforeDispatch),
    ("cancellation_after_dispatch_retains_uncertainty", Cases.CancellationAfterDispatch),
    ("lost_response_retains_original_attempt", Cases.LostResponse),
    ("failed_body_read_preserves_observed_headers", Cases.FailedBodyRead),
    ("profile_and_purpose_mismatches_never_dispatch", Cases.ProfileAndPurposeMismatch),
    ("reordered_input_cannot_use_captured_source_mapping", Cases.ReorderedInput),
    ("duplicate_attempt_never_dispatches_again", Cases.DuplicateAttempt),
    ("request_identity_is_stable_and_scope_bound", Cases.Identity),
    ("duplicate_source_reference_rejected", Cases.DuplicateSource)
};
foreach (var item in cases)
{
    await item.Run();
    Console.WriteLine(JsonSerializer.Serialize(new { scenario = item.Name, result = "PASS" }));
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    boundary = "P08 A5 offline packed embedding consumer only",
    result = "PASS", scenarios = cases.Length,
    googleSdkAssembly = typeof(Google.GenAI.Client).Assembly.GetName().Name,
    paidCalls = 0, fullP08 = "NOT-RUN", installedBridge = "NOT-RUN"
}));
Console.WriteLine("agentica-provider-consumer-a5-ok");
await RecoveryCases.RunAll();
Console.WriteLine("agentica-provider-consumer-p08-ok");
await OrderedMessageCases.Run();
Console.WriteLine("agentica-provider-consumer-ordered-ok");

internal static class Cases
{
    private const string Model = "text-embedding-3-small";
    private const string PrivateText = "private synthetic 🧭 source text";
    private static MemoryEmbeddingProfile Profile(string revision = "fixture.configuration.1") =>
        new("openai", Model, revision, 3, "fixture.document.1", "fixture.query.1");
    private static SourceInput[] Sources() =>
        [new("message-one", "revision-one", PrivateText), new("message-two", "revision-two", "second\nsource")];

    public static async Task Success(MemoryEmbeddingPurpose purpose)
    {
        var source = Sources();
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())), purpose: purpose, sources: source);
        source[0] = new("tampered", "revision-new", "caller mutated after capture");
        var result = await harness.Attempt.ExecuteAsync();
        Check.Equal(1, harness.Wire.Calls);
        Check.Equal(Profile().ProfileId, result.ProfileId);
        Check.Equal("message-one", result.Items[0].Source.SourceId);
        Check.Equal("revision-two", result.Items[1].Source.Revision);
        Check.Equal(1f, result.Items[0].Vector[0]);
        Check.Equal(1f, result.Items[1].Vector[1]);
        Check.Equal(5L, result.InputTokens);
        var evidence = harness.Evidence;
        Check.Equal(purpose, evidence.Purpose);
        Check.Equal("provider-request-fixture", evidence.ProviderRequestId);
        Check.Equal(200, evidence.StatusCode);
        Check.Equal(AcceptanceState.Accepted, evidence.Acceptance);
        Check.Equal(DispatchState.ResponseObserved, evidence.Dispatch);
        Check.That(evidence.RequestIdentity.StartsWith("sha256:", StringComparison.Ordinal), "request identity");
        var receipt = JsonSerializer.Serialize(evidence);
        Check.That(!receipt.Contains("private synthetic", StringComparison.Ordinal) &&
            !receipt.Contains("synthetic-embedding-consumer-key", StringComparison.Ordinal), "receipt redaction");
    }

    public static async Task ReservationRejection()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())),
            journal: new() { RejectReservation = true });
        await Check.Throws<InvalidOperationException>(() => harness.Attempt.ExecuteAsync());
        Check.Equal(0, harness.Wire.Calls);
        Check.Equal(0, harness.Journal.Records.Count);
    }

    public static async Task InvalidVector()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid(17, invalidVector: true))));
        var error = await Check.Throws<EmbeddingClientException>(() => harness.Attempt.ExecuteAsync());
        Check.Equal("invalid_response", error.ErrorCode);
        Check.Equal(17L, harness.Evidence.InputTokens);
        Check.Equal("provider-request-fixture", harness.Evidence.ProviderRequestId);
        Check.Equal(AcceptanceState.Rejected, harness.Evidence.Acceptance);
        Check.Equal(1, harness.Wire.Calls);
    }

    public static async Task MissingUsage()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid(null))));
        var batch = await harness.Attempt.ExecuteAsync();
        Check.Equal<long?>(null, batch.InputTokens);
        Check.Equal<long?>(null, harness.Evidence.InputTokens);
    }

    public static async Task HttpFailure()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(
            "{\"error\":{\"message\":\"private remote body\"},\"usage\":{\"prompt_tokens\":13}}", HttpStatusCode.TooManyRequests)));
        var error = await Check.Throws<EmbeddingClientException>(() => harness.Attempt.ExecuteAsync());
        Check.Equal("http_status", error.ErrorCode);
        Check.Equal(429, harness.Evidence.StatusCode);
        Check.Equal(13L, harness.Evidence.InputTokens);
        Check.Equal(1, harness.Wire.Calls);
        Check.That(!JsonSerializer.Serialize(harness.Evidence).Contains("private remote body", StringComparison.Ordinal), "remote error redaction");
    }

    public static async Task CancellationBeforeDispatch()
    {
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())));
        await Check.Throws<OperationCanceledException>(() => harness.Attempt.ExecuteAsync(stopped.Token));
        Check.Equal(0, harness.Wire.Calls);
        Check.Equal(0, harness.Journal.Records.Count);
    }

    public static async Task CancellationAfterDispatch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Valid());
        });
        var pending = harness.Attempt.ExecuteAsync(caller.Token);
        await entered.Task.WaitAsync(timeout.Token);
        await caller.CancelAsync();
        await Check.Throws<OperationCanceledException>(() => pending);
        Check.Equal(1, harness.Wire.Calls);
        Check.Equal(DispatchState.DispatchedUnknown, harness.Evidence.Dispatch);
        Check.Equal(AcceptanceState.Cancelled, harness.Evidence.Acceptance);
        Check.Equal<long?>(null, harness.Evidence.InputTokens);
    }

    public static async Task LostResponse()
    {
        using var harness = new Harness((_, _) => throw new HttpRequestException("synthetic lost acknowledgement"));
        var error = await Check.Throws<EmbeddingClientException>(() => harness.Attempt.ExecuteAsync());
        Check.Equal("transport_failure", error.ErrorCode);
        Check.Equal(1, harness.Wire.Calls);
        Check.Equal(DispatchState.DispatchedUnknown, harness.Evidence.Dispatch);
        Check.Equal<long?>(null, harness.Evidence.InputTokens);
        Check.Equal("fixture-attempt", harness.Evidence.AttemptId);
    }

    public static async Task FailedBodyRead()
    {
        using var harness = new Harness((_, _) =>
        {
            var response = Response(Valid());
            response.Content.Dispose();
            response.Content = new StreamContent(new FailingStream());
            return Task.FromResult(response);
        });
        var error = await Check.Throws<EmbeddingClientException>(() => harness.Attempt.ExecuteAsync());
        Check.Equal("response_read_failure", error.ErrorCode);
        Check.Equal(DispatchState.ResponseObserved, harness.Evidence.Dispatch);
        Check.Equal("provider-request-fixture", harness.Evidence.ProviderRequestId);
        Check.Equal(200, harness.Evidence.StatusCode);
        Check.Equal<long?>(null, harness.Evidence.InputTokens);
        Check.Equal(1, harness.Wire.Calls);
    }

    public static async Task ProfileAndPurposeMismatch()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())));
        await Check.Throws<ArgumentException>(() => harness.Attempt.ExecuteAsync(Profile("different.configuration"),
            MemoryEmbeddingPurpose.Document, Sources().Select(source => source.Text).ToArray()));
        await Check.Throws<ArgumentException>(() => harness.Attempt.ExecuteAsync(Profile(),
            MemoryEmbeddingPurpose.Query, Sources().Select(source => source.Text).ToArray()));
        Check.Equal(0, harness.Wire.Calls);
        Check.Equal(0, harness.Journal.Records.Count);
    }

    public static async Task ReorderedInput()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())));
        await Check.Throws<InvalidOperationException>(() => harness.Attempt.ExecuteAsync(Profile(),
            MemoryEmbeddingPurpose.Document, Sources().Reverse().Select(source => source.Text).ToArray()));
        Check.Equal(0, harness.Wire.Calls);
        Check.Equal(0, harness.Journal.Records.Count);
    }

    public static async Task DuplicateAttempt()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())));
        await harness.Attempt.ExecuteAsync();
        await Check.Throws<InvalidOperationException>(() => harness.Attempt.ExecuteAsync());
        Check.Equal(1, harness.Wire.Calls);
        Check.Equal(1, harness.Journal.Records.Count);
        Check.Equal(AcceptanceState.Accepted, harness.Evidence.Acceptance);
        using var reopened = new Harness((_, _) => Task.FromResult(Response(Valid())), journal: harness.Journal);
        await Check.Throws<InvalidOperationException>(() => reopened.Attempt.ExecuteAsync());
        Check.Equal(0, reopened.Wire.Calls);
        Check.Equal(AcceptanceState.Accepted, harness.Evidence.Acceptance);
    }

    public static async Task Identity()
    {
        using var first = new Harness((_, _) => Task.FromResult(Response(Valid())), attemptId: "first-attempt");
        using var second = new Harness((_, _) => Task.FromResult(Response(Valid())), attemptId: "second-attempt");
        using var otherScope = new Harness((_, _) => Task.FromResult(Response(Valid())), scope: "different-scope");
        await first.Attempt.ExecuteAsync();
        await second.Attempt.ExecuteAsync();
        await otherScope.Attempt.ExecuteAsync();
        Check.Equal(first.Evidence.RequestIdentity, second.Evidence.RequestIdentity);
        Check.Equal(first.Evidence.BodyHash, otherScope.Evidence.BodyHash);
        Check.That(first.Evidence.RequestIdentity != otherScope.Evidence.RequestIdentity, "scope-bound identity");
    }

    public static Task DuplicateSource()
    {
        Check.ThrowsSync<ArgumentException>(() =>
        {
            using var harness = new Harness((_, _) => Task.FromResult(Response(Valid())),
                sources: [new("same", "one", "first"), new("same", "two", "second")]);
        });
        return Task.CompletedTask;
    }

    private static string Valid(long? tokens = 5, bool invalidVector = false) => JsonSerializer.Serialize(new
    {
        @object = "list", model = Model,
        data = new[]
        {
            new { @object = "embedding", index = 1, embedding = new[] { 0f, 1f, 0f } },
            new { @object = "embedding", index = 0, embedding = invalidVector ? new[] { 0f, 0f, 0f } : new[] { 1f, 0f, 0f } }
        },
        // total_tokens alone cannot become measured prompt usage.
        usage = tokens.HasValue ? new Dictionary<string, long> { ["prompt_tokens"] = tokens.Value, ["total_tokens"] = tokens.Value } : new Dictionary<string, long> { ["total_tokens"] = 5 }
    });

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-request-id", "provider-request-fixture");
        return response;
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _attemptId;
        public Harness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            InMemoryAttemptJournal? journal = null, MemoryEmbeddingPurpose purpose = MemoryEmbeddingPurpose.Document,
            SourceInput[]? sources = null, string attemptId = "fixture-attempt", string scope = "fixture-scope")
        {
            _attemptId = attemptId;
            Journal = journal ?? new();
            Wire = new(respond);
            Attempt = new(attemptId, "fixture.operation", scope, Profile(), purpose, sources ?? Sources(), Journal, Wire);
        }
        public InMemoryAttemptJournal Journal { get; }
        public FixtureTransport Wire { get; }
        public EmbeddingAttempt Attempt { get; }
        public AttemptEvidence Evidence => Journal[_attemptId];
        public void Dispose() => Attempt.Dispose();
    }

    private sealed class FixtureTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic broken response stream");
    }
}

internal static class Check
{
    public static void That(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("assertion_failed:" + name);
    }
    public static void Equal<T>(T expected, T actual) => That(EqualityComparer<T>.Default.Equals(expected, actual), "equality");
    public static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("expected_exception:" + typeof(T).Name);
    }
    public static void ThrowsSync<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("expected_exception:" + typeof(T).Name);
    }
}

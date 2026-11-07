using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentProviderCustodyTests
{
    [Fact]
    public async Task Borrowed_project_mutation_during_required_request_journal_denies_generation()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count()));
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("OpenAI-Project", "captured-project");
        using var executor = new OpenAiExperimentExecutor(new(name => name == "OPENAI_API_KEY" ? "fixture-key" : null), http);
        var settings = new ExperimentSettings();
        var turn = new ExperimentTurn("turn", "chat", 1, "Current user", "", "running", DateTimeOffset.UtcNow, null, 1, settings, "transcript-only");
        var context = new ExperimentExecutionContext("chat", turn, [], Directory.GetCurrentDirectory(), trace =>
        {
            if (trace.Type == "provider.request")
            { http.DefaultRequestHeaders.Remove("OpenAI-Project"); http.DefaultRequestHeaders.Add("OpenAI-Project", "changed-project"); }
            return Task.CompletedTask;
        }, _ => Task.CompletedTask);
        var result = await executor.ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal("failed", result.Status);
        Assert.Equal("provider.transport_changed", result.ErrorCode);
        Assert.Equal("/v1/responses/input_tokens", Assert.Single(handler.Paths));
        var evidence = Assert.Single(JsonSerializer.SerializeToElement(result.Usage).GetProperty("attempts").EnumerateArray());
        Assert.Equal((int)LlmDispatchCertainty.NotDispatched, evidence.GetProperty("DispatchCertainty").GetInt32());
        Assert.StartsWith("sha256:", evidence.GetProperty("References").GetProperty("RouteFingerprint").GetString());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("http-error")]
    [InlineData("cancel")]
    public async Task Native_count_required_journal_reopens_with_observed_headers_on_every_exit(string mode)
    {
        var directory = State();
        string scope;
        using (var store = new ExperimentStore(directory))
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        using (var handler = new Handler((_, _) =>
        {
            var response = mode == "http-error" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("not retained") } : Count();
            if (mode == "cancel") response.Content = new StreamContent(new CancelOnRead(cancellation));
            response.Headers.Add("x-request-id", "observed-count-request");
            return Task.FromResult(response);
        }))
        using (var http = new HttpClient(handler))
        using (var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key", journal: store))
        {
            scope = counter.ScopeId;
            if (mode == "success") Assert.Equal(42, (await counter.CountAsync(Body(), cancellation.Token)).Tokens);
            else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counter.CountAsync(Body(), cancellation.Token));
            else await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(), cancellation.Token));
            var record = Assert.Single(counter.CountAttempts);
            Assert.Equal(LlmProviderWorkKind.InputCount, record.Request.WorkKind);
            Assert.Equal("observed-count-request", record.Evidence!.Value.GetProperty("ProviderRequestId").GetString());
        }
        using var reopened = new ExperimentStore(directory);
        var persisted = Assert.Single(reopened.ReadInputCountAttempts(scope));
        var evidence = persisted.Evidence!.Value;
        Assert.Equal("observed-count-request", evidence.GetProperty("ProviderRequestId").GetString());
        Assert.Equal(mode == "http-error" ? 503 : 200, evidence.GetProperty("HttpStatusCode").GetInt32());
        Assert.Equal((int)(mode == "success" ? LlmAttemptOutcome.Succeeded : mode == "cancel" ? LlmAttemptOutcome.Cancelled : LlmAttemptOutcome.Failed),
            evidence.GetProperty("Outcome").GetInt32());
        Assert.DoesNotContain("fixture-key", evidence.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_required_count_reservation_prevents_HTTP_and_retains_rejection_capture()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count()));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key", journal: new DeniedJournal());
        await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Paths);
        var capture = Assert.Single(counter.CountAttempts).Evidence!.Value;
        Assert.Equal((int)LlmDispatchCertainty.NotDispatched, capture.GetProperty("DispatchCertainty").GetInt32());
    }

    [Fact]
    public async Task Count_journal_cannot_change_borrowed_project_after_capture_and_still_dispatch()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count()));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key", journal: new MutatingJournal(http));
        var failure = await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(), TestContext.Current.CancellationToken));
        Assert.Equal("provider.transport_changed", failure.Code);
        Assert.Empty(handler.Paths);
        Assert.Equal((int)LlmDispatchCertainty.NotDispatched,
            Assert.Single(counter.CountAttempts).Evidence!.Value.GetProperty("DispatchCertainty").GetInt32());
    }

    [Fact]
    public async Task Public_app_binds_provided_executor_to_its_actual_store_count_journal_without_starting_host()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count()));
        using var http = new HttpClient(handler);
        var config = new OpenAiProviderConfiguration(name => name == "OPENAI_API_KEY" ? "fixture-key" : null);
        var provided = new OpenAiExperimentExecutor(config, http);
        var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], storageDirectory: State(), openAiConfiguration: config, experimentExecutor: provided);
        try
        {
            var executor = Assert.IsType<OpenAiExperimentExecutor>(app.Services.GetRequiredService<IExperimentExecutor>());
            using var counter = Assert.IsType<OpenAiExperimentInputTokenCounter>(executor.CreateInputTokenCounter(new()));
            await counter.CountAsync(Body(), TestContext.Current.CancellationToken);
            var store = app.Services.GetRequiredService<ExperimentStore>();
            Assert.NotNull(Assert.Single(store.ReadInputCountAttempts(counter.ScopeId)).Evidence);
        }
        finally { await app.DisposeAsync(); }
    }

    private static byte[] Body() => JsonSerializer.SerializeToUtf8Bytes(new
    { model = "gpt-6-luna", input = "Synthetic input", stream = true, store = false, max_output_tokens = 8192, reasoning = new { effort = "high" } });
    private static HttpResponseMessage Count() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"object\":\"response.input_tokens\",\"input_tokens\":42}", Encoding.UTF8, "application/json") };
    private static string State() => Path.Combine(AppContext.BaseDirectory, ".agentica", "custody-proof", Guid.NewGuid().ToString("N"));
    private sealed class DeniedJournal : IExperimentInputCountJournal
    {
        public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken) => throw new IOException("synthetic required reservation failed");
        public ValueTask ObserveAsync(LlmPreparedRequestDescriptor request, JsonElement evidence) => ValueTask.CompletedTask;
    }
    private sealed class MutatingJournal(HttpClient borrowed) : IExperimentInputCountJournal
    {
        public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
        { borrowed.DefaultRequestHeaders.Add("OpenAI-Project", "changed-after-capture"); return ValueTask.CompletedTask; }
        public ValueTask ObserveAsync(LlmPreparedRequestDescriptor request, JsonElement evidence) => ValueTask.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Paths.Add(request.RequestUri!.AbsolutePath); return send(request, cancellationToken); }
    }
    private sealed class CancelOnRead(CancellationTokenSource cancellation) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellation.Cancel(); return ValueTask.FromCanceled<int>(cancellation.Token); }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

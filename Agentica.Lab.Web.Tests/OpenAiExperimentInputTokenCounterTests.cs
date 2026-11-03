using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;

namespace Agentica.Lab.Web.Tests;

public sealed class OpenAiExperimentInputTokenCounterTests
{
    [Fact]
    public async Task Native_count_preserves_exact_model_input_and_schema_fields_and_excludes_generation_settings()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count(731)));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-private-key");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "gpt-6-luna",
            input = new[] { new { role = "user", content = "  exact <input> é 😀\r\n" } },
            instructions = "  exact instructions\r\n",
            reasoning = new { effort = "high", summary = "auto" },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "result",
                    strict = true,
                    schema = new { type = "object", properties = new { answer = new { type = "string" } } }
                }
            },
            tools = new[] { new { type = "function", name = "lookup", parameters = new { type = "object" } } },
            tool_choice = "auto",
            parallel_tool_calls = false,
            truncation = "disabled",
            stream = true,
            store = false,
            max_output_tokens = 64000,
            max_tool_calls = 3,
            include = new[] { "web_search_call.action.sources" },
            metadata = new { privateContext = "not-counted-bookkeeping" },
            conversation = (string?)null,
            previous_response_id = (string?)null
        });
        var original = body.ToArray();
        var measurement = await counter.CountAsync(body, TestContext.Current.CancellationToken);
        Assert.Equal(731, measurement.Tokens); Assert.True(measurement.ProviderCount);
        Assert.Equal("openai.responses.input_tokens", measurement.Method);
        Assert.Equal(Hash(body), measurement.RequestHash); Assert.Equal(original, body);
        var captured = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/responses/input_tokens", captured.Url);
        Assert.Equal("Bearer fixture-private-key", captured.Authorization);
        Assert.Equal("application/json", captured.MediaType);
        using var sent = JsonDocument.Parse(captured.Body); using var generation = JsonDocument.Parse(body);
        Assert.Equal(new[] { "model", "input", "instructions", "reasoning", "text", "tools", "tool_choice", "parallel_tool_calls", "truncation" },
            sent.RootElement.EnumerateObject().Select(property => property.Name));
        foreach (var property in sent.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(generation.RootElement.GetProperty(property.Name), property.Value));
        Assert.Equal(Hash(captured.Body), measurement.CountRequestHash);
        Assert.Equal(measurement, Assert.Single(counter.Measurements));
    }

    [Fact]
    public async Task Cache_uses_exact_generation_body_identity_including_model_and_exposes_detached_measurement_snapshots()
    {
        using var handler = new Handler((call, _) => Task.FromResult(Count(call * 10)));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key");
        var firstBody = Body("gpt-6-luna", "same input", 64000);
        var first = await counter.CountAsync(firstBody, TestContext.Current.CancellationToken);
        var snapshot = counter.Measurements;
        var repeated = await counter.CountAsync(firstBody.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(first, repeated); Assert.Single(handler.Requests);
        var otherModel = await counter.CountAsync(Body("exact-other-model", "same input", 64000), TestContext.Current.CancellationToken);
        var otherOutput = await counter.CountAsync(Body("gpt-6-luna", "same input", 60000), TestContext.Current.CancellationToken);
        var otherInput = await counter.CountAsync(Body("gpt-6-luna", "changed input", 64000), TestContext.Current.CancellationToken);
        Assert.NotEqual(first.RequestHash, otherModel.RequestHash); Assert.NotEqual(first.RequestHash, otherOutput.RequestHash);
        Assert.NotEqual(first.RequestHash, otherInput.RequestHash);
        Assert.Equal(first.CountRequestHash, otherOutput.CountRequestHash); // Output limit is not count-endpoint input.
        Assert.NotEqual(first.CountRequestHash, otherModel.CountRequestHash);
        Assert.NotEqual(first.CountRequestHash, otherInput.CountRequestHash);
        Assert.Equal(4, handler.Requests.Count); Assert.Equal(4, counter.Measurements.Count); Assert.Single(snapshot);
    }

    [Fact]
    public async Task Large_request_uses_the_provider_integer_not_its_character_or_wire_byte_count()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count(12001)));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key");
        var text = new string('a', 62413) + new string('é', 14);
        var body = Body("gpt-6-luna", text, 64000);
        var measured = await counter.CountAsync(body, TestContext.Current.CancellationToken);
        Assert.Equal(12001, measured.Tokens); Assert.NotEqual(body.Length + 512, measured.Tokens);
        Assert.NotEqual(text.Length, measured.Tokens);
        using var sent = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(text, sent.RootElement.GetProperty("input").GetString());
        Assert.True(measured.ProviderCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"model\":\"\",\"input\":\"text\"}")]
    [InlineData("{\"model\":\"gpt-6-luna\",\"input\":42}")]
    [InlineData("{\"model\":\"gpt-6-luna\",\"input\":\"text\",\"conversation\":\"private-conversation\"}")]
    [InlineData("{\"model\":\"gpt-6-luna\",\"input\":\"text\",\"previous_response_id\":\"private-response\"}")]
    public async Task Invalid_or_server_state_referencing_input_is_refused_without_outbound_work(string input)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Count(1)));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-private-key");
        var error = await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Encoding.UTF8.GetBytes(input), TestContext.Current.CancellationToken));
        Assert.Equal("context.token_count_invalid", error.Code); Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("private", error.Message); Assert.Empty(handler.Requests); Assert.Empty(counter.Measurements);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"input_tokens\":1}")]
    [InlineData("{\"object\":\"response\",\"input_tokens\":1}")]
    [InlineData("{\"object\":\"response.input_tokens\",\"input_tokens\":-1}")]
    [InlineData("{\"object\":\"response.input_tokens\",\"input_tokens\":1.5}")]
    [InlineData("{\"object\":\"response.input_tokens\",\"input_tokens\":2147483648}")]
    [InlineData("{\"object\":\"response.input_tokens\",\"input_tokens\":\"2\"}")]
    [InlineData("{\"object\":\"response.input_tokens\",\"input_tokens\":null}")]
    public async Task Invalid_native_responses_produce_safe_typed_unavailable_errors(string response)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(response, Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-private-key");
        var error = await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(), TestContext.Current.CancellationToken));
        Assert.Equal("context.token_count_unavailable", error.Code); Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("private", error.Message); Assert.Empty(counter.Measurements); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Error_bodies_network_failures_and_timeouts_are_not_exposed_or_retried()
    {
        foreach (var failure in new[] { "http", "network", "timeout" })
        {
            using var handler = new Handler((_, _) => failure switch
            {
                "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("private-source fixture-private-key") }),
                "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException("private-source fixture-private-key")),
                _ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("private-source fixture-private-key"))
            });
            using var http = new HttpClient(handler);
            using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-private-key");
            var error = await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(), TestContext.Current.CancellationToken));
            Assert.Equal("context.token_count_unavailable", error.Code); Assert.Equal(503, error.StatusCode);
            Assert.DoesNotContain("private", error.ToString()); Assert.Single(handler.Requests); Assert.Empty(counter.Measurements);
        }
    }

    [Fact]
    public async Task Response_limit_applies_to_streamed_bodies_without_content_length()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string(' ', 65537)))) }));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key");
        var error = await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(), TestContext.Current.CancellationToken));
        Assert.Equal("context.token_count_unavailable", error.Code); Assert.Empty(counter.Measurements);
    }

    [Fact]
    public async Task Zero_is_valid_and_count_capacity_includes_failed_requests_while_cached_values_remain_available()
    {
        using var handler = new Handler((call, _) => Task.FromResult(call == 1 ? Count(0) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key", maximumCalls: 2);
        var cached = await counter.CountAsync(Body(), TestContext.Current.CancellationToken);
        Assert.Equal(0, cached.Tokens);
        await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(input: "second"), TestContext.Current.CancellationToken));
        var limited = await Assert.ThrowsAsync<ExperimentException>(() => counter.CountAsync(Body(input: "third"), TestContext.Current.CancellationToken));
        Assert.Equal("context.token_count_limit", limited.Code); Assert.Equal(503, limited.StatusCode);
        Assert.Equal(cached, await counter.CountAsync(Body(), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests.Count); Assert.Single(counter.Measurements);
    }

    [Fact]
    public async Task Caller_cancellation_before_and_during_HTTP_remains_cancellation_with_no_retry()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new Handler((_, token) =>
        {
            cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counter.CountAsync(Body(), cancellation.Token));
        Assert.Single(handler.Requests); Assert.Empty(counter.Measurements);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counter.CountAsync(Body(input: "never sent"), cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Queued_cancellation_consumes_no_request_and_identical_concurrent_work_reuses_the_successful_measurement()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.TrySetResult(); await release.Task.WaitAsync(token); return Count(19);
        });
        using var http = new HttpClient(handler);
        using var counter = new OpenAiExperimentInputTokenCounter(http, "fixture-key", maximumCalls: 1);
        var first = counter.CountAsync(Body(), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = counter.CountAsync(Body(), queuedCancellation.Token);
        var same = counter.CountAsync(Body(), TestContext.Current.CancellationToken);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.TrySetResult();
        Assert.Equal(await first, await same); Assert.Single(handler.Requests); Assert.Single(counter.Measurements);
    }

    private static byte[] Body(string model = "gpt-6-luna", string input = "input", int output = 4096) =>
        JsonSerializer.SerializeToUtf8Bytes(new { model, input, stream = true, store = false, max_output_tokens = output });
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static HttpResponseMessage Count(int count) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { @object = "response.input_tokens", input_tokens = count }), Encoding.UTF8, "application/json") };
    private sealed record Captured(string Url, string? Authorization, string? MediaType, byte[] Body);
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        internal List<Captured> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return await response(Requests.Count, cancellationToken);
        }
    }
}

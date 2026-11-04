using Agentica.Clients.Embeddings;
using Agentica.Memory;
using System.Collections;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentica.Tests;

public sealed class SemanticEmbeddingClientTests
{
    private const string FixtureKey = "embedding-fixture-key";
    private const string Model = "text-embedding-3-small";

    [Theory]
    [InlineData(MemoryEmbeddingPurpose.Document)]
    [InlineData(MemoryEmbeddingPurpose.Query)]
    public async Task Sends_exact_projected_text_and_returns_vectors_in_input_index_order(MemoryEmbeddingPurpose purpose)
    {
        var inputs = new List<string> { "  first 🧭\nquoted passage", "second" };
        var profile = Profile();
        using var harness = new Harness(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(OpenAiEmbeddingClientOptions.DefaultEndpoint, request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(FixtureKey, request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(Model, root.GetProperty("model").GetString());
            Assert.Equal(3, root.GetProperty("dimensions").GetInt32());
            Assert.Equal("float", root.GetProperty("encoding_format").GetString());
            Assert.Equal(inputs, root.GetProperty("input").EnumerateArray().Select(item => item.GetString()!));
            Assert.Equal(4, root.EnumerateObject().Count());
            Assert.DoesNotContain(FixtureKey, body, StringComparison.Ordinal);
            inputs[0] = "caller changed after snapshot";
            var response = JsonNode.Parse(Success())!;
            var data = response["data"]!.AsArray();
            response["data"] = new JsonArray(data[1]!.DeepClone(), data[0]!.DeepClone());
            return Json(response.ToJsonString());
        }, profile);
        var equivalentProfile = Profile();
        Assert.NotSame(profile, equivalentProfile);
        var batch = await harness.Client.EmbedAsync(equivalentProfile, purpose, inputs, TestContext.Current.CancellationToken);
        Assert.Equal(profile.ProfileId, batch.ProfileId);
        Assert.Equal([1f, 0f, 0f], batch.Vectors[0]);
        Assert.Equal([0f, 1f, 0f], batch.Vectors[1]);
        Assert.Equal(5, batch.InputTokens);
        Assert.Equal(1, harness.Calls);
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<float>>)batch.Vectors).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<float>)batch.Vectors[0])[0] = 99);
        Assert.DoesNotContain(FixtureKey, harness.Options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FixtureKey, JsonSerializer.Serialize(harness.Options), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("configuration")]
    [InlineData("dimensions")]
    [InlineData("document_projection")]
    [InlineData("query_projection")]
    public async Task Every_embedding_space_coordinate_must_match_before_http(string changed)
    {
        using var harness = new Harness((_, _) => Task.FromResult(Json(Success(1))));
        var profile = new MemoryEmbeddingProfile(changed == "provider" ? "other" : "openai",
            changed == "model" ? "different-model" : Model,
            changed == "configuration" ? "configuration.2" : "configuration.1",
            changed == "dimensions" ? 4 : 3,
            changed == "document_projection" ? "document.2" : "document.1",
            changed == "query_projection" ? "query.2" : "query.1");
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.EmbedAsync(profile,
            MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.Calls);
    }

    [Fact]
    public async Task Bounds_actual_input_enumeration_text_bytes_unicode_and_purpose_before_http()
    {
        using var harness = new Harness((_, _) => Task.FromResult(Json(Success(1))));
        IReadOnlyList<string>[] refused =
        [
            [], [""], [null!], ["\ud800"],
            [new string('x', OpenAiEmbeddingClient.MaximumInputCharacters + 1)],
            Enumerable.Repeat("x", OpenAiEmbeddingClient.MaximumInputs + 1).ToArray(),
            Enumerable.Repeat(new string('x', 32_768), 33).ToArray(),
            new MisreportedInputs(1, Enumerable.Repeat("x", OpenAiEmbeddingClient.MaximumInputs + 1)),
            new MisreportedInputs(2, ["only-one"])
        ];
        foreach (var inputs in refused)
            await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Client.EmbedAsync(Profile(),
                MemoryEmbeddingPurpose.Document, inputs, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Client.EmbedAsync(Profile(),
            (MemoryEmbeddingPurpose)42, ["input"], TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.Calls);
    }

    [Theory]
    [InlineData("invalid_json")]
    [InlineData("non_object")]
    [InlineData("missing_model")]
    [InlineData("wrong_model")]
    [InlineData("wrong_object")]
    [InlineData("missing_data")]
    [InlineData("wrong_data_type")]
    [InlineData("wrong_count")]
    [InlineData("duplicate_index")]
    [InlineData("negative_index")]
    [InlineData("out_of_range_index")]
    [InlineData("missing_index")]
    [InlineData("string_index")]
    [InlineData("fractional_index")]
    [InlineData("missing_vector")]
    [InlineData("wrong_vector_type")]
    [InlineData("short_vector")]
    [InlineData("long_vector")]
    [InlineData("non_numeric_component")]
    [InlineData("non_finite_component")]
    [InlineData("zero_vector")]
    [InlineData("wrong_item_object")]
    public async Task Rejects_malformed_or_incompatible_vectors_without_repair_or_retry(string condition)
    {
        using var harness = new Harness((_, _) => Task.FromResult(Json(Malformed(condition))));
        var error = await Assert.ThrowsAsync<EmbeddingClientException>(() => harness.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Document, ["first", "second"], TestContext.Current.CancellationToken));
        Assert.Equal("invalid_response", error.ErrorCode);
        Assert.Equal(1, harness.Calls);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("refusal")]
    public async Task Provider_refusal_cannot_be_hidden_by_otherwise_valid_data(string field)
    {
        var response = JsonNode.Parse(Success(1))!;
        response[field] = "private source echo " + FixtureKey;
        using var harness = new Harness((_, _) => Task.FromResult(Json(response.ToJsonString())));
        var error = await Assert.ThrowsAsync<EmbeddingClientException>(() => harness.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Query, ["private source"], TestContext.Current.CancellationToken));
        Assert.Equal("provider_refusal", error.ErrorCode);
        Assert.DoesNotContain("private source", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FixtureKey, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, harness.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Http_failure_exposes_only_safe_status_and_never_retries(int status)
    {
        using var harness = new Harness((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            ReasonPhrase = "private source " + FixtureKey,
            Content = new StringContent("private source " + FixtureKey)
        }));
        var error = await Assert.ThrowsAsync<EmbeddingClientException>(() => harness.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Query, ["private source"], TestContext.Current.CancellationToken));
        Assert.Equal("http_status", error.ErrorCode);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("openai", error.ProviderId);
        Assert.DoesNotContain(FixtureKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private source", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, harness.Calls);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("{}", null)]
    [InlineData("{\"total_tokens\":5}", null)]
    [InlineData("{\"prompt_tokens\":0}", 0L)]
    [InlineData("{\"prompt_tokens\":17,\"total_tokens\":99}", 17L)]
    [InlineData("{\"prompt_tokens\":-1}", null)]
    [InlineData("{\"prompt_tokens\":9223372036854775808}", null)]
    [InlineData("{\"prompt_tokens\":1.5}", null)]
    [InlineData("{\"prompt_tokens\":\"5\"}", null)]
    [InlineData("null", null)]
    public async Task Input_usage_is_raw_optional_and_never_inferred_from_total_or_text(string? usage, long? expected)
    {
        var root = JsonNode.Parse(Success(1))!;
        if (usage is null) root.AsObject().Remove("usage");
        else root["usage"] = JsonNode.Parse(usage);
        using var harness = new Harness((_, _) => Task.FromResult(Json(root.ToJsonString())));
        var batch = await harness.Client.EmbedAsync(Profile(), MemoryEmbeddingPurpose.Query, ["input"],
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, batch.InputTokens);
        Assert.Single(batch.Vectors);
    }

    [Fact]
    public async Task Response_limit_checks_declared_and_actual_stream_size()
    {
        using (var declared = new Harness((_, _) => Task.FromResult(Json(new string(' ', 513))), maximumResponseBytes: 512))
        {
            var error = await Assert.ThrowsAsync<EmbeddingClientException>(() => declared.Client.EmbedAsync(Profile(),
                MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
            Assert.Equal("response_too_large", error.ErrorCode);
        }
        var stream = new CountingStream(Encoding.UTF8.GetBytes(new string(' ', 1024)));
        using var streamed = new Harness((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }), maximumResponseBytes: 512);
        var streamedError = await Assert.ThrowsAsync<EmbeddingClientException>(() => streamed.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
        Assert.Equal("response_too_large", streamedError.ErrorCode);
        Assert.InRange(stream.BytesRead, 1, 513);
        Assert.Equal(1, streamed.Calls);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_and_during_http_is_not_a_refusal_or_retry()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Success(1));
        });
        using var alreadyStopped = new CancellationTokenSource();
        alreadyStopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Query, ["input"], alreadyStopped.Token));
        Assert.Equal(0, harness.Calls);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var pending = harness.Client.EmbedAsync(Profile(), MemoryEmbeddingPurpose.Query, ["input"], caller.Token);
        await started.Task.WaitAsync(timeout.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, harness.Calls);
    }

    [Fact]
    public async Task Response_body_read_observes_the_callers_cancellation()
    {
        var stream = new WaitingStream();
        using var harness = new Harness((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        var pending = harness.Client.EmbedAsync(Profile(), MemoryEmbeddingPurpose.Query, ["input"], stop.Token);
        await stream.Started.Task.WaitAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, harness.Calls);
    }

    [Fact]
    public async Task Transport_errors_are_sanitized_but_wrapped_process_integrity_failures_escape()
    {
        using (var ordinary = new Harness((_, _) => throw new HttpRequestException("private source " + FixtureKey)))
        {
            var error = await Assert.ThrowsAsync<EmbeddingClientException>(() => ordinary.Client.EmbedAsync(Profile(),
                MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
            Assert.Equal("transport_failure", error.ErrorCode);
            Assert.DoesNotContain(FixtureKey, error.ToString(), StringComparison.Ordinal);
            Assert.Null(error.InnerException);
            Assert.Equal(1, ordinary.Calls);
        }
#pragma warning disable CA2201 // Synthetic integrity failure proves the wrapper escapes unchanged.
        var fatal = new HttpRequestException("wrapped fatal", new OutOfMemoryException());
#pragma warning restore CA2201
        using var integrity = new Harness((_, _) => throw fatal);
        var observed = await Assert.ThrowsAsync<HttpRequestException>(() => integrity.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
        Assert.Same(fatal, observed);
        Assert.Equal(1, integrity.Calls);
    }

    [Fact]
    public async Task Transport_timeout_and_response_read_failure_are_safe_distinct_single_attempts()
    {
        using (var timedOut = new Harness((_, _) => throw new OperationCanceledException("private source " + FixtureKey)))
        {
            var error = await Assert.ThrowsAsync<EmbeddingClientException>(() => timedOut.Client.EmbedAsync(Profile(),
                MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
            Assert.Equal("timeout", error.ErrorCode);
            Assert.DoesNotContain(FixtureKey, error.ToString(), StringComparison.Ordinal);
            Assert.Null(error.InnerException);
            Assert.Equal(1, timedOut.Calls);
        }
        using var failedRead = new Harness((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream())
        }));
        var readError = await Assert.ThrowsAsync<EmbeddingClientException>(() => failedRead.Client.EmbedAsync(Profile(),
            MemoryEmbeddingPurpose.Query, ["input"], TestContext.Current.CancellationToken));
        Assert.Equal("response_read_failure", readError.ErrorCode);
        Assert.DoesNotContain(FixtureKey, readError.ToString(), StringComparison.Ordinal);
        Assert.Null(readError.InnerException);
        Assert.Equal(1, failedRead.Calls);
    }

    [Fact]
    public async Task Dimension_request_is_explicit_and_endpoint_credentials_are_never_embedded_in_urls()
    {
        var legacy = new MemoryEmbeddingProfile("openai", "text-embedding-ada-002", "configuration.legacy",
            1536, "document.1", "query.1");
        Assert.Throws<ArgumentException>(() => new OpenAiEmbeddingClientOptions(legacy, FixtureKey));
        using var harness = new Harness(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.False(body.RootElement.TryGetProperty("dimensions", out _));
            var vector = new float[1536];
            vector[0] = 1;
            return Json(JsonSerializer.Serialize(new
            {
                @object = "list",
                model = legacy.ModelId,
                data = new[] { new { @object = "embedding", index = 0, embedding = vector } }
            }));
        }, legacy, sendDimensions: false);
        var batch = await harness.Client.EmbedAsync(legacy, MemoryEmbeddingPurpose.Document, ["input"],
            TestContext.Current.CancellationToken);
        Assert.Equal(1536, Assert.Single(batch.Vectors).Count);
        var credentialUri = new UriBuilder("https", "example.invalid") { UserName = "fixture", Password = "fixture" }.Uri;
        Assert.Throws<ArgumentException>(() => new OpenAiEmbeddingClientOptions(Profile(), FixtureKey, credentialUri));
        Assert.Throws<ArgumentException>(() => new OpenAiEmbeddingClientOptions(Profile(), FixtureKey, new Uri("http://example.invalid/embeddings")));
        Assert.Throws<ArgumentException>(() => new OpenAiEmbeddingClientOptions(Profile(), "bad\ncredential"));
        Assert.Throws<ArgumentException>(() => new OpenAiEmbeddingClientOptions(Profile(), "non-ascii-\u00e9"));
    }

    private static MemoryEmbeddingProfile Profile() => new("openai", Model, "configuration.1", 3, "document.1", "query.1");

    private static string Success(int count = 2) => JsonSerializer.Serialize(new
    {
        @object = "list",
        model = Model,
        data = Enumerable.Range(0, count).Select(index => new
        {
            @object = "embedding",
            index,
            embedding = index == 0 ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f }
        }).ToArray(),
        usage = new { prompt_tokens = 5, total_tokens = 5 }
    });

    private static string Malformed(string condition)
    {
        if (condition == "invalid_json") return "{";
        if (condition == "non_object") return "[]";
        var root = JsonNode.Parse(Success())!;
        var data = root["data"]!.AsArray();
        var first = data[0]!;
        switch (condition)
        {
            case "missing_model": root.AsObject().Remove("model"); break;
            case "wrong_model": root["model"] = "other-model"; break;
            case "wrong_object": root["object"] = "different"; break;
            case "missing_data": root.AsObject().Remove("data"); break;
            case "wrong_data_type": root["data"] = "not-an-array"; break;
            case "wrong_count": data.RemoveAt(1); break;
            case "duplicate_index": data[1]!["index"] = 0; break;
            case "negative_index": first["index"] = -1; break;
            case "out_of_range_index": first["index"] = 2; break;
            case "missing_index": first.AsObject().Remove("index"); break;
            case "string_index": first["index"] = "0"; break;
            case "fractional_index": first["index"] = 0.5; break;
            case "missing_vector": first.AsObject().Remove("embedding"); break;
            case "wrong_vector_type": first["embedding"] = "base64-is-not-requested"; break;
            case "short_vector": first["embedding"] = new JsonArray(1, 0); break;
            case "long_vector": first["embedding"] = new JsonArray(1, 0, 0, 0); break;
            case "non_numeric_component": first["embedding"]![0] = "NaN"; break;
            case "non_finite_component": first["embedding"]![0] = JsonNode.Parse("1e100"); break;
            case "zero_vector": first["embedding"] = new JsonArray(0, 0, 0); break;
            case "wrong_item_object": first["object"] = "not-embedding"; break;
            default: throw new ArgumentOutOfRangeException(nameof(condition));
        }
        return root.ToJsonString();
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private sealed class Harness : IDisposable
    {
        private readonly StubHandler _handler;
        private readonly HttpClient _http;

        public Harness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            MemoryEmbeddingProfile? profile = null, int maximumResponseBytes = OpenAiEmbeddingClientOptions.MaximumSupportedResponseBytes,
            bool sendDimensions = true)
        {
            _handler = new(respond);
            _http = new HttpClient(_handler);
            Options = new(profile ?? Profile(), FixtureKey, sendDimensions: sendDimensions,
                maximumResponseBytes: maximumResponseBytes);
            Client = new(Options, _http);
        }

        public OpenAiEmbeddingClient Client { get; }
        public OpenAiEmbeddingClientOptions Options { get; }
        public int Calls => _handler.Calls;
        public void Dispose() => _http.Dispose();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class MisreportedInputs(int count, IEnumerable<string> items) : IReadOnlyList<string>
    {
        public int Count => count;
        public string this[int index] => throw new NotSupportedException();
        public IEnumerator<string> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class WaitingStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("private source " + FixtureKey);
    }
}

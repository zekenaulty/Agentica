using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class OpenAiWebSearchTests
{
    [Theory]
    [InlineData(LlmWebSearchMode.Auto, "auto")]
    [InlineData(LlmWebSearchMode.Required, "required")]
    public async Task Search_envelope_preserves_opt_in_limits_activity_and_external_references(LlmWebSearchMode mode, string choice)
    {
        string? body = null;
        var handler = new FixtureHandler(async message =>
        {
            body = await message.Content!.ReadAsStringAsync();
            return Response(Event(new { type = "response.web_search_call.in_progress", item_id = "ws1", output_index = 0 }) + Terminal());
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(new(ApiKey: "fixture-key"), http);
        var request = Request(new(mode, 5));
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(request)) events.Add(item);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal(choice, sent.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(5, sent.RootElement.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal("web_search", sent.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Contains(sent.RootElement.GetProperty("include").EnumerateArray(), item => item.GetString() == "web_search_call.action.sources");
        Assert.Contains(sent.RootElement.GetProperty("include").EnumerateArray(), item => item.GetString() == "reasoning.encrypted_content");
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        var response = events.Single(item => item.Kind == LlmStreamEventKind.Completed).Response!;
        var evidence = Assert.IsType<LlmWebSearchEvidence>(response.WebSearch);
        Assert.Equal("**source**", response.Text);
        Assert.Equal(1, evidence.CompletedCalls);
        Assert.Null(evidence.Calls[0].Query);
        Assert.Null(evidence.Calls[0].Queries);
        Assert.Equal("external-untrusted", evidence.Trust);
        Assert.Equal(0, evidence.Citations[0].AnswerStartIndex);
        Assert.Equal(10, evidence.Citations[0].AnswerEndIndex);
        Assert.Same(evidence, request.AttemptCapture!.LatestWebSearch);
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.Activity && item.WebSearch?.Calls[0].Status == "in_progress");
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Default_omits_search_and_rejects_unbound_search_output()
    {
        string? body = null;
        var handler = new FixtureHandler(async message =>
        {
            body = await message.Content!.ReadAsStringAsync();
            return Response(Terminal());
        });
        using var http = new HttpClient(handler);
        var request = Request(null);
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(http).GenerateAsync(request));
        Assert.Equal("unsupported_output_item", failure.ErrorClass);
        using var sent = JsonDocument.Parse(body!);
        Assert.False(sent.RootElement.TryGetProperty("tools", out _));
        Assert.False(sent.RootElement.TryGetProperty("tool_choice", out _));
        Assert.Null(failure.WebSearch);
        Assert.Null(request.AttemptCapture!.LatestWebSearch);
    }

    [Fact]
    public void Explicit_request_is_query_only_and_preserves_selected_generation_controls()
    {
        var options = new LlmGenerationOptions(MaxOutputTokens: 123,
            Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High));
        var request = OpenAiWebSearch.CreateRequest("selected", " exact query ", options, 4);
        Assert.Equal("selected", request.ModelId);
        Assert.Same(options, request.GenerationOptions);
        Assert.Equal(LlmWebSearchMode.Required, request.WebSearch!.Mode);
        Assert.Equal(4, request.WebSearch.MaxToolCalls);
        Assert.Null(request.NativeContinuation);
        Assert.Null(request.StructuredOutput);
        var user = Assert.Single(request.Messages);
        Assert.Equal(LlmMessageRole.User, user.Role);
        Assert.Equal(" exact query ", user.Content);
        var body = Client(new HttpClient(new FixtureHandler(_ => Task.FromResult(Response(Terminal()))))).BuildRequestBody(request, request.ModelId);
        Assert.False(body.ContainsKey("instructions"));
        Assert.Single((List<object>)body["input"]!);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("incomplete")]
    [InlineData("malformed")]
    public async Task Search_observations_survive_unsuccessful_client_delivery(string failureMode)
    {
        var body = Terminal();
        if (failureMode == "refusal") body = Terminal(content: [new { type = "refusal", refusal = "fixture" }]);
        if (failureMode == "incomplete") body = Terminal(status: "incomplete", eventType: "response.incomplete");
        if (failureMode == "malformed") body += "data: {malformed}\n\n";
        var handler = new FixtureHandler(_ => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var request = Request(new());
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(http).GenerateAsync(request));
        Assert.Equal(1, failure.WebSearch!.CompletedCalls);
        Assert.Same(request.AttemptCapture!.LatestWebSearch, failure.WebSearch);
        Assert.Equal(7, failure.AttemptEvidence?.Usage?.PromptTokens);
        Assert.DoesNotContain("example.com", JsonSerializer.Serialize(failure.AttemptEvidence));
    }

    [Fact]
    public async Task Cancellation_keeps_search_evidence_without_changing_exception_semantics()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new CancelAtEndStream(Terminal(), cancellation)) }));
        using var http = new HttpClient(handler);
        var request = Request(new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GenerateAsync(request, cancellation.Token));
        Assert.Equal(1, request.AttemptCapture!.LatestWebSearch!.CompletedCalls);
        Assert.Equal(LlmAttemptOutcome.Cancelled, request.AttemptCapture.Latest!.Outcome);
        Assert.Equal(7, request.AttemptCapture.Latest.Usage?.PromptTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Required_search_must_complete_and_provider_call_ceiling_is_checked(bool exceedsLimit)
    {
        var output = exceedsLimit ? new object[] { Call("ws1"), Call("ws2"), Message() } : [Message()];
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(output: output))));
        using var http = new HttpClient(handler);
        var request = Request(new(LlmWebSearchMode.Required, 1));
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(http).GenerateAsync(request));
        Assert.Equal(exceedsLimit ? "web_search_call_limit" : "web_search_not_completed", failure.ErrorClass);
        Assert.Equal(exceedsLimit ? 2 : 0, failure.WebSearch!.Calls.Count);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,fixture")]
    public async Task Unsafe_urls_are_excluded_without_rewriting_canonical_answer(string url)
    {
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(output: [Call(url: url), Message(url: url)]))));
        using var http = new HttpClient(handler);
        var response = await Client(http).GenerateAsync(Request(new()));
        Assert.Equal("**source**", response.Text);
        Assert.Empty(response.WebSearch!.Sources);
        Assert.Empty(response.WebSearch.Citations);
    }

    [Theory]
    [InlineData(LlmCitationIndexUnit.UnicodeScalars, 1, 2, 2, 3)]
    [InlineData(LlmCitationIndexUnit.Utf16CodeUnits, 2, 3, 2, 3)]
    public async Task Explicit_index_projection_preserves_raw_anchors_and_converts_surrogates(
        LlmCitationIndexUnit unit, int start, int end, int expectedStart, int expectedEnd)
    {
        var content = new object[]
        {
            new { type = "output_text", text = "😀 x", annotations = new[] { Citation(start: start, end: end) } },
            new { type = "output_text", text = "**source**", annotations = new[] { Citation() } }
        };
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(content: content))));
        using var http = new HttpClient(handler);
        var response = await Client(http).GenerateAsync(Request(new(citationIndexUnit: unit)));
        var first = response.WebSearch!.Citations[0];
        Assert.Equal(start, first.StartIndex);
        Assert.Equal(end, first.EndIndex);
        Assert.Equal(expectedStart, first.AnswerStartIndex);
        Assert.Equal(expectedEnd, first.AnswerEndIndex);
        Assert.Equal(4, response.WebSearch.Citations[1].TextOffset);
        Assert.Equal(4, response.WebSearch.Citations[1].AnswerStartIndex);
        Assert.Equal("😀 x**source**", response.Text);
        var restored = JsonSerializer.Deserialize<LlmWebSearchEvidence>(JsonSerializer.Serialize(response.WebSearch))!;
        Assert.Equal(first.AnswerStartIndex, restored.Citations[0].AnswerStartIndex);
        Assert.Equal(response.WebSearch.Citations[1].OutputIndex, restored.Citations[1].OutputIndex);
    }

    [Fact]
    public async Task Ambiguous_native_units_keep_raw_positions_and_clickable_source_without_guessing()
    {
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(content:
            [new { type = "output_text", text = "😀 source", annotations = new[] { Citation(start: 2, end: 8) } }]))));
        using var http = new HttpClient(handler);
        var response = await Client(http).GenerateAsync(Request(new()));
        var citation = response.WebSearch!.Citations[0];
        Assert.Equal(2, citation.StartIndex);
        Assert.Null(citation.AnswerStartIndex);
        Assert.Equal("https://example.com/", citation.Source.Url);
    }

    [Fact]
    public async Task Native_replay_preserves_search_items_without_enabling_search_on_followup()
    {
        var bodies = new List<string>();
        var handler = new FixtureHandler(async message =>
        {
            bodies.Add(await message.Content!.ReadAsStringAsync());
            return Response(bodies.Count == 1 ? Terminal() : Terminal(output: [Message()]));
        });
        using var http = new HttpClient(handler);
        var client = Client(http);
        var first = await client.GenerateAsync(Request(new()));
        using var native = first.NativeContinuation!;
        Assert.NotNull(native);
        var second = await client.GenerateAsync(Request(null) with { NativeContinuation = native });
        using var replay = JsonDocument.Parse(bodies[1]);
        Assert.False(replay.RootElement.TryGetProperty("tools", out _));
        Assert.Contains(replay.RootElement.GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "web_search_call");
        Assert.Null(second.WebSearch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unsupported_provider_routes_reject_search_before_transport(bool xai)
    {
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal())));
        using var http = new HttpClient(handler);
        ILlmClient client = xai ? new XaiResponsesLlmClient(new(ApiKey: "fixture"), http) : new AnthropicMessagesLlmClient(new(ApiKey: "fixture"), http);
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(Request(new())));
        Assert.Equal("unsupported_web_search", failure.ErrorClass);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, failure.AttemptEvidence?.DispatchCertainty);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task Completed_item_citations_survive_a_later_terminal_failure()
    {
        var prefix = Event(new { type = "response.output_item.done", output_index = 0, item = Call() }) +
            Event(new { type = "response.output_item.done", output_index = 1, item = Message() });
        var failed = Event(new { type = "response.failed", response = new { status = "failed", usage = new { input_tokens = 7 } } });
        var handler = new FixtureHandler(_ => Task.FromResult(Response(prefix + failed)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(http).GenerateAsync(Request(new())));
        var citation = Assert.Single(failure.WebSearch!.Citations);
        Assert.Equal(1, citation.OutputIndex);
        Assert.Equal(0, citation.StartIndex);
        Assert.Null(citation.AnswerStartIndex); // Earlier message offsets are not yet known.
        Assert.Equal("https://example.com/", citation.Source.Url);
    }

    [Fact]
    public async Task Native_queries_are_optional_and_no_query_is_inferred_from_user_input()
    {
        var native = new
        {
            type = "web_search_call",
            id = "ws1",
            status = "completed",
            action = new { type = "search", query = "native query", queries = new[] { "one", "two" } }
        };
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(output: [native, Message()]))));
        using var http = new HttpClient(handler);
        var response = await Client(http).GenerateAsync(Request(new()));
        var call = Assert.Single(response.WebSearch!.Calls);
        Assert.Equal("native query", call.Query);
        Assert.Equal(["one", "two"], call.Queries);
        var missing = new LlmWebSearchCall("missing", 0, "completed", "search");
        var serialized = JsonSerializer.Serialize(missing);
        Assert.DoesNotContain("Query", serialized);
        Assert.DoesNotContain("Queries", serialized);
    }

    [Fact]
    public void External_snapshots_copy_collections_and_enforce_structural_bounds()
    {
        var queries = new List<string> { "query" };
        var call = new LlmWebSearchCall("ws1", 0, "completed", "search", queries: queries);
        queries.Clear();
        Assert.Single(call.Queries!);
        var calls = new List<LlmWebSearchCall> { call };
        var evidence = new LlmWebSearchEvidence(calls, [], []);
        calls.Clear();
        Assert.Single(evidence.Calls);
        Assert.Throws<NotSupportedException>(() => ((IList<LlmWebSearchCall>)evidence.Calls).Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmWebSearchOptions(maxToolCalls: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmWebSearchOptions((LlmWebSearchMode)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmWebSearchEvidence(Enumerable.Repeat(call, 65).ToArray(), [], []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conflicting_search_identity_rejects_delivery_and_retains_both_observations(bool duplicateId)
    {
        var body = duplicateId ? Terminal(output: [Call("same"), Call("same"), Message()]) :
            Event(new { type = "response.output_item.done", output_index = 0, item = Call("first") }) +
            Event(new { type = "response.output_item.done", output_index = 0, item = Call("second") });
        var handler = new FixtureHandler(_ => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(http).GenerateAsync(Request(new(maxToolCalls: 1))));
        Assert.Equal("web_search_identity_conflict", failure.ErrorClass);
        Assert.Equal(2, failure.WebSearch!.Calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_external_fields_are_reported_as_bounded_omissions(bool queryField)
    {
        var call = new
        {
            type = "web_search_call",
            id = "ws1",
            status = "completed",
            action = new
            {
                type = "search",
                query = queryField ? new string('q', 4097) : null,
                sources = new[] { new { url = "https://example.com/", title = queryField ? "source" : new string('t', 1025) } }
            }
        };
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(output: [call, Message()]))));
        using var http = new HttpClient(handler);
        var response = await Client(http).GenerateAsync(Request(new()));
        Assert.True(response.WebSearch!.ReferencesTruncated);
        Assert.Null(response.WebSearch.Calls[0].Query);
        Assert.Single(response.WebSearch.Calls);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("open_page")]
    [InlineData("find_in_page")]
    public async Task Durable_native_exchange_keeps_exact_search_actions_private_and_replayable(string actionType)
    {
        object action = actionType switch
        {
            "open_page" => new { type = actionType, url = "https://example.com/private-native-marker" },
            "find_in_page" => new { type = actionType, url = "https://example.com/private-native-marker", pattern = "pattern" },
            _ => new { type = actionType, queries = new[] { "private-native-marker" } }
        };
        var nativeCall = new { type = "web_search_call", id = "ws1", status = "completed", action };
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(output: [nativeCall, Message()]))));
        using var http = new HttpClient(handler);
        var request = Request(new());
        var response = await Client(http).GenerateAsync(request);
        using var native = response.NativeContinuation!;
        var report = LlmConfigurationReports.Describe(request, new OpenAiResponsesClientOptions(ApiKey: "fixture-key"));
        using var binding = LlmNativeContinuationBinding.FromRequest(report, request);
        var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding);
        Assert.True(exported.Succeeded, exported.Rejection.ToString());
        using var sensitive = exported.SensitivePayload!;
        Assert.DoesNotContain("private-native-marker", JsonSerializer.Serialize(new { native, sensitive, exported }));
        var imported = LlmNativeContinuationCodec.ImportSensitive(sensitive, binding);
        Assert.True(imported.Succeeded, imported.Rejection.ToString());
        using var reopened = imported.Continuation!;
        Assert.Equal(native.HistoryStepsJson, reopened.HistoryStepsJson);
        var followup = Request(new()) with { NativeContinuation = reopened };
        var body = Client(http).BuildRequestBody(followup, followup.ModelId);
        Assert.Contains("private-native-marker", JsonSerializer.Serialize(body["input"]));
    }

    [Theory]
    [InlineData("xai", "completed", "search")]
    [InlineData("openai", "invented-status", "search")]
    [InlineData("openai", "completed", "function_call")]
    public void Durable_exchange_rejects_unimplemented_or_malformed_search_shapes(string provider, string status, string actionType)
    {
        var history = JsonSerializer.Serialize(new object[]
        {
            new { role = "user", content = "fixture" },
            new { type = "web_search_call", id = "ws1", status, action = new { type = actionType } },
            Message()
        });
        using var native = new LlmNativeContinuation(provider, "fixture-model", "", history);
        using var binding = new LlmNativeContinuationBinding(provider, "responses", "fixture-model", "fixture-config", "");
        var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding);
        Assert.Equal(LlmNativeContinuationRejection.InvalidNativeShape, exported.Rejection);
        Assert.False(exported.Succeeded);
    }

    [Fact]
    public void Configuration_reports_distinguish_enabled_search_controls_and_unsupported_routes()
    {
        var request = Request(new(LlmWebSearchMode.Required, 5));
        var options = new OpenAiResponsesClientOptions(ApiKey: "fixture-key");
        var report = LlmConfigurationReports.Describe(request, options);
        Assert.True(report.AdapterAcceptsRequest);
        Assert.Equal(LlmCapabilitySupport.Supported, report.Capabilities.Single(item => item.Name == "native_search").AdapterSupport);
        Assert.Equal("required", report.Controls.Single(item => item.Name == "web_search_mode").AppliedValue);
        Assert.Equal("5", report.Controls.Single(item => item.Name == "max_web_search_calls").AppliedValue);
        var without = LlmConfigurationReports.Describe(request with { WebSearch = null }, options);
        Assert.NotEqual(report.ConfigurationIdentity, without.ConfigurationIdentity);
        var unsupported = LlmConfigurationReports.Describe(request, new AnthropicMessagesClientOptions(ApiKey: "fixture-key"));
        Assert.False(unsupported.AdapterAcceptsRequest);
        Assert.Contains(unsupported.Issues, item => item.Code == "unsupported_web_search");
    }

    [Fact]
    public async Task Completion_observer_retains_failed_search_evidence_and_does_not_publish_foreign_capture_state()
    {
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal() + "data: {malformed}\n\n")));
        using var http = new HttpClient(handler);
        var request = Request(new());
        var events = new List<LlmStreamEvent>();
        await Assert.ThrowsAsync<LlmClientException>(() => LlmStreamCompletion.GenerateAsync(Client(http), request, events.Add, CancellationToken.None));
        var failed = events.Single(item => item.Kind == LlmStreamEventKind.Failed);
        Assert.Same(request.AttemptCapture!.LatestWebSearch, failed.WebSearch);
        var original = failed.WebSearch;
        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LlmStreamCompletion.GenerateAsync(Client(http), request, events.Add, CancellationToken.None));
        Assert.Null(events.Single(item => item.Kind == LlmStreamEventKind.Failed).WebSearch);
        Assert.Same(original, request.AttemptCapture.LatestWebSearch);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Default_encrypted_reasoning_and_search_sources_roundtrip_without_public_payload_exposure()
    {
        var bodies = new List<string>();
        var reasoning = new { type = "reasoning", encrypted_content = "opaque-private-reasoning-marker", summary = Array.Empty<object>() };
        var handler = new FixtureHandler(async message =>
        {
            bodies.Add(await message.Content!.ReadAsStringAsync());
            return Response(Terminal(output: [reasoning, Call(), Message()]));
        });
        using var http = new HttpClient(handler);
        var client = Client(http);
        var request = Request(new());
        var response = await client.GenerateAsync(request);
        using var native = response.NativeContinuation!;
        Assert.NotNull(native);
        Assert.DoesNotContain("opaque-private-reasoning-marker", JsonSerializer.Serialize(response));
        var report = LlmConfigurationReports.Describe(request, new OpenAiResponsesClientOptions(ApiKey: "fixture-key"));
        using var binding = LlmNativeContinuationBinding.FromRequest(report, request);
        var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding);
        Assert.True(exported.Succeeded, exported.Rejection.ToString());
        using var sensitive = exported.SensitivePayload!;
        using var imported = LlmNativeContinuationCodec.ImportSensitive(sensitive, binding).Continuation!;
        await client.GenerateAsync(Request(new()) with { NativeContinuation = imported });
        foreach (var body in bodies)
        {
            using var parsed = JsonDocument.Parse(body);
            var include = parsed.RootElement.GetProperty("include").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("reasoning.encrypted_content", include);
            Assert.Contains("web_search_call.action.sources", include);
            Assert.False(parsed.RootElement.GetProperty("store").GetBoolean());
            Assert.False(parsed.RootElement.TryGetProperty("previous_response_id", out _));
        }
        Assert.Contains("opaque-private-reasoning-marker", bodies[1]);
    }

    [Fact]
    public async Task Encrypted_reasoning_opt_out_is_truthful_about_unavailable_native_continuation()
    {
        string? body = null;
        var reasoning = new { type = "reasoning", summary = Array.Empty<object>() };
        var handler = new FixtureHandler(async message =>
        {
            body = await message.Content!.ReadAsStringAsync();
            return Response(Terminal(output: [reasoning, Message()]));
        });
        using var http = new HttpClient(handler);
        var options = new OpenAiResponsesClientOptions(ApiKey: "fixture-key") { IncludeEncryptedReasoning = false };
        var request = Request(null);
        var response = await new OpenAiResponsesLlmClient(options, http).GenerateAsync(request);
        using var sent = JsonDocument.Parse(body!);
        Assert.False(sent.RootElement.TryGetProperty("include", out _));
        Assert.Null(response.NativeContinuation);
        Assert.Equal("False", response.Metadata!["openai.continuation.available"]);
        var with = LlmConfigurationReports.Describe(request, options with { IncludeEncryptedReasoning = true });
        var without = LlmConfigurationReports.Describe(request, options);
        Assert.NotEqual(with.ConfigurationIdentity, without.ConfigurationIdentity);
        var (_, _, _) = options; // Existing positional constructor and deconstruction stay unchanged.
    }

    [Fact]
    public async Task User_info_urls_are_excluded_without_literal_credential_like_fixture_strings()
    {
        var uri = new UriBuilder("https://example.com/") { UserName = "fixture", Password = "fixture" }.Uri.AbsoluteUri;
        var handler = new FixtureHandler(_ => Task.FromResult(Response(Terminal(output: [Call(url: uri), Message(url: uri)]))));
        using var http = new HttpClient(handler);
        var response = await Client(http).GenerateAsync(Request(new()));
        Assert.Empty(response.WebSearch!.Sources);
        Assert.Empty(response.WebSearch.Citations);
        Assert.Equal("**source**", response.Text);
    }

    private static OpenAiResponsesLlmClient Client(HttpClient http) => new(new(ApiKey: "fixture-key"), http);
    private static LlmRequest Request(LlmWebSearchOptions? search) => new("fixture-model", [new(LlmMessageRole.User, "fixture")])
    { WebSearch = search, AttemptCapture = new(new("attempt:" + Guid.NewGuid().ToString("N"))) };
    private static object Call(string id = "ws1", string url = "https://example.com/") => new
    { type = "web_search_call", id, status = "completed", action = new { type = "search", sources = new[] { new { url, title = "source" } } } };
    private static object Citation(string url = "https://example.com/", int start = 0, int end = 10) => new
    { type = "url_citation", url, title = "source", start_index = start, end_index = end };
    private static object Message(string url = "https://example.com/") => new
    { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "**source**", annotations = new[] { Citation(url) } } } };
    private static string Terminal(object[]? output = null, object[]? content = null, string status = "completed", string eventType = "response.completed") => Event(new
    {
        type = eventType,
        response = new
        {
            id = "response-1",
            status,
            usage = new { input_tokens = 7, output_tokens = 3 },
            output = output ?? [Call(), content is null ? Message() : new { type = "message", role = "assistant", content }]
        }
    });
    private static string Event(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return response(request); }
    }
    private sealed class CancelAtEndStream(string text, CancellationTokenSource cancellation) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position != Length) return base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }
}

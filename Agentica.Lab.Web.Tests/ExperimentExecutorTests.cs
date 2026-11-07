using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentExecutorTests
{
    [Fact]
    public async Task Clean_room_omits_overlay_and_replays_only_selected_visible_messages_once()
    {
        using var handler = new Handler(_ => Stream(TextResponse("Hello")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings(), [
            new("user-1", "user", "Earlier user", DateTimeOffset.UtcNow),
            new("assistant-1", "assistant", "Earlier answer", DateTimeOffset.UtcNow)]);
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Hello", result.AssistantText);
        Assert.Equal("Hello", probe.Output.ToString());
        var wire = Assert.Single(handler.Bodies);
        Assert.Single(handler.CountBodies);
        using var document = JsonDocument.Parse(wire);
        var body = document.RootElement;
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("gpt-6-luna", body.GetProperty("model").GetString());
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        foreach (var absent in new[] { "instructions", "tools", "tool_choice", "previous_response_id", "text", "include" })
            Assert.False(body.TryGetProperty(absent, out _));
        var inputs = body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(["user", "assistant", "user"], inputs.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal(["Earlier user", "Earlier answer", "Current user"], inputs.Select(message => message.GetProperty("content").GetString()));
        var retained = JsonSerializer.Serialize(probe.Events);
        Assert.DoesNotContain("fixture-secret", retained, StringComparison.Ordinal);
        var request = Payload(Assert.Single(probe.Events, item => item.Type == "provider.request"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wire))), request.GetProperty("hash").GetString());
        Assert.Equal("transcript-only", request.GetProperty("continuity").GetString());
    }

    [Fact]
    public async Task Ambient_search_is_an_explicit_discretionary_provider_tool_with_clickable_source_metadata()
    {
        using var handler = new Handler(_ => Stream(WebResponse("A sourced answer.")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { AmbientWebSearch = true, MaxWebSearchCalls = 2 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Equal("A sourced answer.", result.AssistantText);
        using var request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var wire = request.RootElement;
        Assert.Equal("web_search", Assert.Single(wire.GetProperty("tools").EnumerateArray()).GetProperty("type").GetString());
        Assert.Equal("auto", wire.GetProperty("tool_choice").GetString());
        Assert.Equal(2, wire.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal("web_search_call.action.sources", Assert.Single(wire.GetProperty("include").EnumerateArray()).GetString());
        Assert.False(wire.GetProperty("store").GetBoolean());
        Assert.False(wire.TryGetProperty("instructions", out _));
        var citations = Payload(Assert.Single(probe.Events, item => item.Type == "provider.citations"));
        Assert.Equal("assistant", citations.GetProperty("target").GetString());
        var citation = Assert.Single(citations.GetProperty("citations").EnumerateArray());
        Assert.Equal("https://example.org/evidence", citation.GetProperty("url").GetString());
        Assert.Equal(0, citation.GetProperty("startIndex").GetInt32());
        Assert.Equal(8, citation.GetProperty("endIndex").GetInt32());
        Assert.NotEmpty(citations.GetProperty("sources").EnumerateArray());
        Assert.Contains(probe.Events, item => item.Type == "provider.web-search" && item.ItemId == "web-1");
        Assert.Contains(probe.Events, item => item.Type == "provider.record" &&
            Payload(item).GetProperty("type").GetString() == "response.output_item.done");
    }

    [Fact]
    public async Task Unbound_native_search_output_is_rejected_and_never_claimed_as_a_bound_capability()
    {
        using var handler = new Handler(_ => Stream(WebResponse("Unexpected search")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings());
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("provider.unsupported_item", result.ErrorCode);
        Assert.DoesNotContain(probe.Events, item => item.Type == "provider.citations");
    }

    [Theory]
    [InlineData("  Exact instructions\n\nKeep spacing.  ")]
    [InlineData("   ")]
    public async Task Exact_nonempty_instructions_are_never_trimmed_or_rewritten(string instructions)
    {
        using var handler = new Handler(_ => Stream(TextResponse("Done")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { Instructions = instructions, ReasoningEffort = null });
        Assert.Equal("completed", (await executor.ExecuteAsync(probe.Context, CancellationToken.None)).Status);
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(instructions, document.RootElement.GetProperty("instructions").GetString());
        Assert.False(document.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task Native_items_summary_and_usage_are_retained_without_private_reasoning_carriers()
    {
        var events = Event(new
        {
            type = "response.output_item.added",
            output_index = 0,
            item = new { id = "reasoning-1", type = "reasoning", status = "in_progress", encrypted_content = "opaque-secret", content = "private-thought" }
        }) +
            Event(new { type = "response.reasoning_summary_text.delta", item_id = "reasoning-1", output_index = 0, summary_index = 1, delta = "Checking evidence" }) +
            Event(new { type = "response.reasoning_text.done", item_id = "reasoning-1", output_index = 0, text = "private-thought" }) + TextResponse("Answer");
        using var handler = new Handler(_ => Stream(events));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { ReasoningSummary = true });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        var summary = Assert.Single(probe.Events, item => item.Type == "provider.summary.delta");
        Assert.Equal("reasoning-1", summary.ItemId);
        Assert.Equal("Checking evidence", Payload(summary).GetProperty("text").GetString());
        Assert.Contains(probe.Events, item => item.Type == "provider.item" && item.ItemId == "reasoning-1");
        var source = Assert.Single(probe.Events, item => item.Type == "provider.record" &&
            Payload(item).GetProperty("type").GetString() == "response.reasoning_summary_text.delta");
        var nativeSummary = Payload(source);
        Assert.Equal(0, nativeSummary.GetProperty("output_index").GetInt32());
        Assert.Equal(1, nativeSummary.GetProperty("summary_index").GetInt32());
        Assert.Equal("reasoning-1", nativeSummary.GetProperty("item_id").GetString());
        Assert.Equal("Checking evidence", nativeSummary.GetProperty("delta").GetString());
        Assert.Equal(summary.CallId, source.CallId);
        Assert.True(probe.Events.IndexOf(source) < probe.Events.IndexOf(summary));
        Assert.Equal(5, probe.Events.Count(item => item.Type == "provider.record"));
        var privateSource = Payload(Assert.Single(probe.Events, item => item.Type == "provider.record" &&
            Payload(item).GetProperty("type").GetString() == "response.reasoning_text.done"));
        Assert.Equal(0, privateSource.GetProperty("output_index").GetInt32());
        Assert.Equal("reasoning-1", privateSource.GetProperty("item_id").GetString());
        Assert.True(privateSource.GetProperty("contentRedacted").GetBoolean());
        Assert.False(privateSource.TryGetProperty("text", out _));
        Assert.Contains("ThinkingTokens", JsonSerializer.Serialize(result.Usage), StringComparison.Ordinal);
        var retained = JsonSerializer.Serialize(probe.Events);
        Assert.DoesNotContain("opaque-secret", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("private-thought", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted_content", retained, StringComparison.Ordinal);
        Assert.Contains("not-retained", retained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_stream_keeps_partial_text_and_never_retries_or_claims_completion()
    {
        using var handler = new Handler(_ => Stream(Event(new { type = "response.output_text.delta", delta = "Partial" })));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings());
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("provider.incomplete_stream", result.ErrorCode);
        Assert.Equal("Partial", result.AssistantText);
        Assert.Equal("Partial", probe.Output.ToString());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Cancellation_preserves_streamed_text_and_does_not_reissue_request()
    {
        using var handler = new Handler(_ =>
        {
            var content = new StreamContent(new WaitingStream(Event(new { type = "response.output_text.delta", delta = "Partial before cancellation" })));
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var probe = new Probe(new ExperimentSettings());
        var context = probe.Context with
        {
            Output = async delta =>
        {
            await probe.Context.Output(delta);
            cancellation.Cancel();
        }
        };
        var result = await executor.ExecuteAsync(context, cancellation.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.Equal("turn.cancelled", result.ErrorCode);
        Assert.Equal("Partial before cancellation", result.AssistantText);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Wire_budget_rejects_before_network_and_preserves_inspectable_prepared_request()
    {
        using var handler = new Handler(_ => Stream(TextResponse("Should not run")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { MaxInputCharacters = 10 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("context.wire_limit", result.ErrorCode);
        Assert.Empty(handler.Bodies);
        Assert.Empty(handler.CountBodies);
        Assert.Single(probe.Events, item => item.Type == "provider.request");
        Assert.DoesNotContain(probe.Events, item => item.Type == "provider.dispatch");
    }

    [Fact]
    public async Task Direct_tools_and_memory_are_rejected_without_dispatch()
    {
        using var handler = new Handler(_ => Stream(TextResponse("Should not run")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var tools = await executor.ExecuteAsync(new Probe(new ExperimentSettings { EnabledTools = ["utility.clock"] }).Context, CancellationToken.None);
        var memory = await executor.ExecuteAsync(new Probe(new ExperimentSettings { MemoryEnabled = true }).Context, CancellationToken.None);
        Assert.Equal("tools.require_harness", tools.ErrorCode);
        Assert.Equal("memory.disabled", memory.ErrorCode);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Real_harness_executes_query_refines_and_completes_from_response_artifact()
    {
        using var handler = new Handler(call => Stream(TextResponse(call == 1 ? Plan("query", "utility.clock", new { }) :
            JsonSerializer.Serialize(new
            {
                fromPlanId = "plan-query",
                reason = "Clock observation received.",
                evidence = Array.Empty<object>(),
                refinedPlan = JsonSerializer.Deserialize<JsonElement>(Plan("answer", OpenAiExperimentExecutor.ResponseToolId, new { content = "The observed clock is available." }))
            }))));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { Mode = "harness", EnabledTools = ["utility.clock"], MaxProviderCalls = 3 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Equal("The observed clock is available.", result.AssistantText);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(2, handler.CountBodies.Count);
        Assert.Equal(result.AssistantText, probe.Output.ToString());
        var outcome = Payload(Assert.Single(probe.Events, item => item.Type == "harness.outcome"));
        Assert.Equal("Succeeded", outcome.GetProperty("status").GetString());
        Assert.Equal(["utility.clock", OpenAiExperimentExecutor.ResponseToolId], outcome.GetProperty("receipts").EnumerateArray().Select(item => item.GetProperty("ToolId").GetString()));
        Assert.NotEmpty(outcome.GetProperty("plans").EnumerateArray());
        Assert.NotEmpty(outcome.GetProperty("refinements").EnumerateArray());
        Assert.NotEmpty(outcome.GetProperty("frames").EnumerateArray());
        Assert.NotEmpty(outcome.GetProperty("CompletionEvidence").EnumerateArray());
        Assert.Empty(outcome.GetProperty("residualObligations").EnumerateArray());
        Assert.Empty(outcome.GetProperty("blockers").EnumerateArray());
        Assert.Equal(2, JsonSerializer.SerializeToElement(result.Usage).GetProperty("attempts").GetArrayLength());
        Assert.Contains(probe.Events, item => item.Type == "harness.event");
        Assert.Equal(2, probe.Events.Count(item => item.Type == "provider.request"));
        Assert.Contains("Workflow", handler.Bodies[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_harness_search_sends_only_query_and_shares_actual_provider_call_admission()
    {
        using var handler = new Handler(call => Stream(call switch
        {
            1 => TextResponse(SearchPlan("search", "explicit bounded query")),
            2 => WebResponse("Search result answer."),
            _ => TextResponse(JsonSerializer.Serialize(new
            {
                fromPlanId = "plan-search",
                reason = "observation",
                evidence = Array.Empty<object>(),
                refinedPlan = JsonSerializer.Deserialize<JsonElement>(Plan("answer", OpenAiExperimentExecutor.ResponseToolId, new { content = "Final answer from searched evidence." }))
            }))
        }));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings
        {
            Mode = "harness",
            EnabledTools = ["web.search"],
            Instructions = "parent-only-instructions",
            MaxProviderCalls = 3,
            MaxWebSearchCalls = 2
        }, [new("history-user", "user", "parent-only-history", DateTimeOffset.UtcNow),
            new("history-assistant", "assistant", "parent-only-answer", DateTimeOffset.UtcNow)]);
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(3, handler.CountBodies.Count);
        Assert.Equal("Final answer from searched evidence.", probe.Output.ToString());
        Assert.DoesNotContain("Search result answer.", probe.Output.ToString(), StringComparison.Ordinal);
        using var search = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("explicit bounded query", Assert.Single(search.RootElement.GetProperty("input").EnumerateArray()).GetProperty("content").GetString());
        Assert.Equal("required", search.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(2, search.RootElement.GetProperty("max_tool_calls").GetInt32());
        Assert.False(search.RootElement.GetProperty("store").GetBoolean());
        Assert.False(search.RootElement.TryGetProperty("instructions", out _));
        foreach (var excluded in new[] { "parent-only-history", "parent-only-answer", "parent-only-instructions", "Current user" })
            Assert.DoesNotContain(excluded, handler.Bodies[1], StringComparison.Ordinal);
        Assert.Equal(3, probe.Events.Count(item => item.Type == "provider.request"));
        Assert.Equal("tool", Payload(Assert.Single(probe.Events, item => item.Type == "provider.citations")).GetProperty("target").GetString());
        Assert.Contains("external-untrusted", handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_search_subrequest_cannot_bypass_the_parent_provider_call_limit()
    {
        using var handler = new Handler(_ => Stream(TextResponse(SearchPlan("search", "explicit query"))));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { Mode = "harness", EnabledTools = ["web.search"], MaxProviderCalls = 1 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("provider.call_limit", result.ErrorCode);
        Assert.Single(handler.Bodies);
        Assert.Contains(probe.Events, item => item.Type == "provider.admission.denied");
    }

    [Fact]
    public async Task Explicit_search_responses_share_a_cumulative_native_call_allowance()
    {
        using var handler = new Handler(call => Stream(call switch
        {
            1 => TextResponse(SearchPlan("search-1", "first query")),
            2 or 4 => WebResponse("Search result answer."),
            3 => TextResponse(JsonSerializer.Serialize(new
            {
                fromPlanId = "plan-search-1",
                reason = "observation",
                evidence = Array.Empty<object>(),
                refinedPlan = JsonSerializer.Deserialize<JsonElement>(SearchPlan("search-2", "second query"))
            })),
            _ => TextResponse(JsonSerializer.Serialize(new
            {
                fromPlanId = "plan-search-2",
                reason = "observation",
                evidence = Array.Empty<object>(),
                refinedPlan = JsonSerializer.Deserialize<JsonElement>(Plan("answer", OpenAiExperimentExecutor.ResponseToolId, new { content = "Compared two searched results." }))
            }))
        }));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { Mode = "harness", EnabledTools = ["web.search"], MaxProviderCalls = 5, MaxWebSearchCalls = 2 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("completed", result.Status);
        Assert.Equal(5, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[1]);
        using var second = JsonDocument.Parse(handler.Bodies[3]);
        Assert.Equal(2, first.RootElement.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal(1, second.RootElement.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal(2, JsonSerializer.SerializeToElement(result.Usage).GetProperty("webSearchCalls").GetInt32());
        Assert.Equal("Compared two searched results.", probe.Output.ToString());
    }

    [Fact]
    public async Task Planner_json_repair_cannot_bypass_provider_call_limit()
    {
        using var handler = new Handler(_ => Stream(TextResponse("not valid JSON")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { Mode = "harness", MaxProviderCalls = 1 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("provider.call_limit", result.ErrorCode);
        Assert.Single(handler.Bodies);
        Assert.Contains(probe.Events, item => item.Type == "provider.admission.denied");
        Assert.Empty(probe.Output.ToString());
    }

    [Fact]
    public async Task Existing_factory_default_does_not_silently_opt_into_chat_repair_policy()
    {
        using var handler = new Handler(_ => Stream(TextResponse("not valid JSON")));
        using var http = new HttpClient(handler);
        using var factory = new LabPlannerFactory(http, openAiConfiguration: Configuration());
        var planner = factory.Create(new ProviderSettings("openai", "fixture-model"), _ => { });
        var failure = await Assert.ThrowsAsync<WorkflowPlannerException>(() => planner.CreatePlanAsync(
            new PlanningRequest(new RunRequest("Fixture objective", RequestOrigin.User), [], [], [])));
        Assert.Equal("native_history_required", Assert.IsType<LlmClientException>(failure.InnerException).ErrorClass);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Public_chat_harness_does_not_consume_the_registered_external_host_planner_factory()
    {
        var directory = Directory.CreateTempSubdirectory("agentica-chat-executor-di-").FullName;
        var spy = new SpyHostPlannerFactory();
        var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], plannerFactory: spy,
            storageDirectory: directory, openAiConfiguration: new(_ => null));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await app.StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var created = await http.PostAsJsonAsync("/api/chats", new ExperimentCreateRequest(), HostProtocol.Json, deadline.Token);
            created.EnsureSuccessStatusCode();
            var conversation = (await created.Content.ReadFromJsonAsync<ExperimentConversation>(HostProtocol.Json, deadline.Token))!;
            using var saved = await http.PutAsJsonAsync($"/api/chats/{conversation.Id}/settings",
                new ExperimentSettingsUpdate(conversation.SettingsRevision, conversation.Settings with { Mode = "harness" }), HostProtocol.Json, deadline.Token);
            saved.EnsureSuccessStatusCode();
            using var sent = await http.PostAsJsonAsync($"/api/chats/{conversation.Id}/turns",
                new ExperimentSendRequest("Exercise the chat-owned harness", "di-regression"), HostProtocol.Json, deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, sent.StatusCode);
            using var refused = JsonDocument.Parse(await sent.Content.ReadAsStringAsync(deadline.Token));
            Assert.Equal("provider.not_configured", refused.RootElement.GetProperty("code").GetString());
            var unchanged = await http.GetFromJsonAsync<ExperimentConversation>($"/api/chats/{conversation.Id}?limit=20", HostProtocol.Json, deadline.Token);
            Assert.Empty(unchanged!.Turns); // Native-count admission requires a key before a durable turn exists.

            // Exercise the executor actually composed by the public app as well. This
            // detects accidental injection of the external-host planner even though
            // public admission now rejects the missing credential before execution.
            var composed = Assert.IsType<OpenAiExperimentExecutor>(app.Services.GetRequiredService<IExperimentExecutor>());
            var probe = new Probe(new ExperimentSettings { Mode = "harness" });
            var result = await composed.ExecuteAsync(probe.Context, deadline.Token);
            Assert.Equal("failed", result.Status); // No credential: factory creation stops before outbound work.
            Assert.Equal(0, spy.CreateCalls);
            var trace = JsonSerializer.Serialize(probe.Events);
            Assert.DoesNotContain("injected-planner-fixture", trace, StringComparison.Ordinal);
            Assert.DoesNotContain("provider.dispatch", trace, StringComparison.Ordinal);
            Assert.DoesNotContain("provider.request", trace, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            var resolved = Path.GetFullPath(directory);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(resolved, recursive: true);
        }
    }

    [Fact]
    public async Task Cumulative_evidence_budget_does_not_erase_authoritative_query_receipt()
    {
        using var handler = new Handler(_ => Stream(TextResponse(Plan("query", "utility.clock", new { }))));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings
        { Mode = "harness", EnabledTools = ["utility.clock"], MaxToolEvidenceCharacters = 256 });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("context.evidence_limit", result.ErrorCode);
        Assert.Single(handler.Bodies);
        var outcome = Payload(Assert.Single(probe.Events, item => item.Type == "harness.outcome"));
        Assert.Single(outcome.GetProperty("receipts").EnumerateArray());
    }

    [Fact]
    public async Task Provider_rejection_has_no_alias_fallback_secret_or_retry()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("fixture-secret rejected") });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { Model = "unknown-model", ReasoningEffort = "high" });
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("provider.http_400", result.ErrorCode);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Single(handler.Bodies);
        Assert.Contains("unknown-model", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Specific_model_effort_error_is_visible_with_credentials_removed()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "unsupported_value",
                    type = "invalid_request_error",
                    message = "reasoning.effort 'high' is unsupported for selected model. fixture-secret sk-shouldneverberetained"
                }
            }))
        });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings());
        var result = await executor.ExecuteAsync(probe.Context, CancellationToken.None);
        Assert.Equal("provider.unsupported_value", result.ErrorCode);
        Assert.Contains("reasoning.effort 'high' is unsupported", result.ErrorMessage, StringComparison.Ordinal);
        var retained = JsonSerializer.Serialize(new { result, events = probe.Events });
        Assert.DoesNotContain("fixture-secret", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-shouldneverberetained", retained, StringComparison.Ordinal);
        Assert.Single(probe.Events, item => item.Type == "provider.error");
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Accessible_model_ids_come_from_provider_and_are_sorted_without_aliases()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"z-model\"}]}") });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        Assert.Equal(["a-model", "z-model"], await executor.ListModelsAsync(CancellationToken.None));
        Assert.Equal("https://api.openai.com/v1/models", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task Late_stream_error_retains_terminal_usage_without_accepting_a_completed_turn()
    {
        using var handler = new Handler(_ => Stream(TextResponse("Terminal text") + Event(new { type = "error" })));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings());
        var result = await executor.ExecuteAsync(probe.Context, TestContext.Current.CancellationToken);
        Assert.Equal("failed", result.Status);
        var evidence = Assert.Single(JsonSerializer.SerializeToElement(result.Usage).GetProperty("attempts").EnumerateArray());
        Assert.Equal("completed", evidence.GetProperty("ProviderStatus").GetString());
        Assert.Equal(12, evidence.GetProperty("Usage").GetProperty("PromptTokens").GetInt32());
        Assert.Equal(3, evidence.GetProperty("Usage").GetProperty("ThinkingTokens").GetInt32());
        Assert.Equal((int)LlmAttemptOutcome.Failed, evidence.GetProperty("Outcome").GetInt32());
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(probe.Events, item => item.Type == "provider.continuity");
    }

    [Fact]
    public async Task Cancellation_after_observed_terminal_preserves_known_usage_and_separate_host_outcome()
    {
        using var pending = new WaitingStream(TextResponse("Observed terminal"));
        using var handler = new Handler(_ =>
        {
            var content = new StreamContent(pending);
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var probe = new Probe(new ExperimentSettings());
        var execution = executor.ExecuteAsync(probe.Context, cancellation.Token);
        await pending.Waiting.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        var result = await execution;
        Assert.Equal("cancelled", result.Status);
        var evidence = Assert.Single(JsonSerializer.SerializeToElement(result.Usage).GetProperty("attempts").EnumerateArray());
        Assert.Equal("completed", evidence.GetProperty("ProviderStatus").GetString());
        Assert.Equal(12, evidence.GetProperty("Usage").GetProperty("PromptTokens").GetInt32());
        Assert.Equal((int)LlmAttemptOutcome.Cancelled, evidence.GetProperty("Outcome").GetInt32());
        Assert.Equal((int)LlmDispatchCertainty.ResponseObserved, evidence.GetProperty("DispatchCertainty").GetInt32());
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(probe.Events, item => item.Type == "provider.continuity");
    }

    [Fact]
    public async Task Required_request_journal_failure_prevents_inference_but_optional_native_telemetry_failure_does_not()
    {
        using var handler = new Handler(_ => Stream(TextResponse("Answer")));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var denied = new Probe(new ExperimentSettings());
        var deniedResult = await executor.ExecuteAsync(denied.Context with
        {
            Trace = item => item.Type == "provider.request" ? throw new IOException("synthetic journal unavailable") : denied.Context.Trace(item)
        }, TestContext.Current.CancellationToken);
        Assert.Equal("failed", deniedResult.Status);
        Assert.Empty(handler.Bodies);
        var rejected = Assert.Single(JsonSerializer.SerializeToElement(deniedResult.Usage).GetProperty("attempts").EnumerateArray());
        Assert.Equal((int)LlmDispatchCertainty.NotDispatched, rejected.GetProperty("DispatchCertainty").GetInt32());

        var observed = new Probe(new ExperimentSettings());
        var observedResult = await executor.ExecuteAsync(observed.Context with
        {
            Trace = item => item.Type == "provider.record" ? throw new IOException("synthetic UI trace unavailable") : observed.Context.Trace(item)
        }, TestContext.Current.CancellationToken);
        Assert.Equal("completed", observedResult.Status);
        Assert.Equal("Answer", observedResult.AssistantText);
        Assert.True(JsonSerializer.SerializeToElement(observedResult.Usage).GetProperty("telemetryDeliveryFailed").GetBoolean());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Oversized_private_inspection_item_does_not_discard_shared_terminal_evidence()
    {
        var terminal = Event(new
        {
            type = "response.completed",
            response = new
            {
                id = "large-native-response",
                status = "completed",
                output = new object[]
                {
                    new { type = "reasoning", encrypted_content = new string('x', 1_100_000) },
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Bounded visible answer" } } }
                },
                usage = new { input_tokens = 31, output_tokens = 8 }
            }
        });
        using var handler = new Handler(_ => Stream(terminal));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings());
        var result = await executor.ExecuteAsync(probe.Context, TestContext.Current.CancellationToken);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Bounded visible answer", result.AssistantText);
        var usage = JsonSerializer.SerializeToElement(result.Usage);
        Assert.True(usage.GetProperty("telemetryDeliveryFailed").GetBoolean());
        Assert.Equal(31, Assert.Single(usage.GetProperty("attempts").EnumerateArray()).GetProperty("Usage").GetProperty("PromptTokens").GetInt32());
        Assert.DoesNotContain("encrypted_content", JsonSerializer.Serialize(probe.Events), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_search_retains_typed_sources_even_when_native_inspection_is_unavailable(bool cancel)
    {
        using var pending = new WaitingStream(WebResponse("Sourced terminal answer"));
        using var handler = new Handler(_ =>
        {
            if (!cancel) return Stream(WebResponse("Sourced terminal answer") + Event(new { type = "error" }));
            var content = new StreamContent(pending);
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var probe = new Probe(new ExperimentSettings { AmbientWebSearch = true });
        var execution = executor.ExecuteAsync(probe.Context with
        {
            Trace = item => item.Type == "provider.record" ? throw new IOException("synthetic optional inspection unavailable") : probe.Context.Trace(item)
        }, cancellation.Token);
        if (cancel)
        {
            await pending.Waiting.Task.WaitAsync(cancellation.Token);
            cancellation.Cancel();
        }
        var result = await execution;
        Assert.Equal(cancel ? "cancelled" : "failed", result.Status);
        var saved = Assert.Single(JsonSerializer.SerializeToElement(result.Usage).GetProperty("webSearchOutcomes").EnumerateArray());
        Assert.NotEmpty(saved.GetProperty("callId").GetString()!);
        var search = saved.GetProperty("evidence");
        Assert.Equal("https://example.org/evidence", Assert.Single(search.GetProperty("Sources").EnumerateArray()).GetProperty("Url").GetString());
        Assert.NotEmpty(search.GetProperty("Citations").EnumerateArray());
        Assert.Contains(probe.Events, item => item.Type == "provider.web-search.outcome");
        Assert.DoesNotContain(probe.Events, item => item.Type == "provider.citations");
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Optional_inspection_decoder_cannot_reject_a_comment_ignored_by_the_shared_parser()
    {
        var bytes = Encoding.UTF8.GetBytes(TextResponse("Valid terminal answer")).Concat(new byte[] { (byte)':', 0xff, (byte)'\n', (byte)'\n' }).ToArray();
        using var handler = new Handler(_ =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings());
        var result = await executor.ExecuteAsync(probe.Context, TestContext.Current.CancellationToken);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Valid terminal answer", result.AssistantText);
        Assert.True(JsonSerializer.SerializeToElement(result.Usage).GetProperty("telemetryDeliveryFailed").GetBoolean());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Saved_search_exports_bound_conversation_rows_and_report_the_original_reference_coverage()
    {
        var sources = Enumerable.Range(0, 100).Select(index => new
        { url = "https://example.org/" + index + "/" + new string('x', 3000), title = "Source " + index }).ToArray();
        var terminal = Event(new
        {
            type = "response.completed",
            response = new
            {
                id = "bounded-search",
                status = "completed",
                output = new object[]
                {
                    new { id = "web-1", type = "web_search_call", status = "completed", action = new { type = "search", sources } },
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Bounded answer" } } }
                },
                usage = new { input_tokens = 31, output_tokens = 8 }
            }
        });
        using var handler = new Handler(_ => Stream(terminal));
        using var http = new HttpClient(handler);
        using var executor = new OpenAiExperimentExecutor(Configuration(), http);
        var probe = new Probe(new ExperimentSettings { AmbientWebSearch = true, MaxProviderCalls = 100 });
        var result = await executor.ExecuteAsync(probe.Context, TestContext.Current.CancellationToken);
        Assert.Equal("completed", result.Status);
        var exported = Assert.Single(JsonSerializer.SerializeToElement(result.Usage).GetProperty("webSearchOutcomes").EnumerateArray());
        Assert.True(exported.GetProperty("hostRetentionTruncated").GetBoolean());
        Assert.Equal(100, exported.GetProperty("observed").GetProperty("sources").GetInt32());
        Assert.True(exported.GetProperty("evidence").GetProperty("Sources").GetArrayLength() < 100);
        Assert.True(exported.GetRawText().Length <= 1_048_576 / 100);
        Assert.True(JsonSerializer.Serialize(result.Usage).Length < 20000);
    }

    private static OpenAiProviderConfiguration Configuration() => new(name => name == "OPENAI_API_KEY" ? "fixture-secret" : null);
    private static JsonElement Payload(ExperimentTraceInput item) => JsonSerializer.SerializeToElement(item.Data);
    private static string Event(object data) => "data: " + JsonSerializer.Serialize(data) + "\n\n";
    private static string TextResponse(string text) => Event(new { type = "response.output_text.delta", item_id = "message-1", delta = text }) +
        Event(new
        {
            type = "response.completed",
            response = new
            {
                id = "response-1",
                status = "completed",
                output = new[] { new { id = "message-1", type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text } } } },
                usage = new { input_tokens = 12, output_tokens = 8, input_tokens_details = new { cached_tokens = 2 }, output_tokens_details = new { reasoning_tokens = 3 } }
            }
        });
    private static string WebResponse(string text)
    {
        var web = new
        {
            id = "web-1",
            type = "web_search_call",
            status = "completed",
            action = new { type = "search", query = "provider search query", sources = new[] { new { type = "url", url = "https://example.org/evidence", title = "Evidence page" } } }
        };
        return Event(new { type = "response.output_item.done", output_index = 0, item = web }) +
            Event(new { type = "response.output_text.delta", item_id = "message-1", delta = text }) +
            Event(new
            {
                type = "response.completed",
                response = new
                {
                    id = "response-web",
                    status = "completed",
                    output = new object[] { web,
                    new { id = "message-1", type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text, annotations = new[] { new
                        { type = "url_citation", start_index = 0, end_index = 8, url = "https://example.org/evidence", title = "Evidence page" } } } } } },
                    usage = new { input_tokens = 20, output_tokens = 10, output_tokens_details = new { reasoning_tokens = 2 } }
                }
            });
    }
    private static HttpResponseMessage Stream(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
    private static string Plan(string id, string tool, object input) => JsonSerializer.Serialize(new
    {
        planId = "plan-" + id,
        description = "Fixture experiment plan",
        steps = new[] { new
        {
            stepId = "step-" + id, toolId = tool, kind = tool == "utility.clock" ? "query" : "action",
            effect = tool == "utility.clock" ? "readOnly" : "writesLocalState", input,
            reason = tool == "utility.clock" ? "Observe the current clock." : "Emit the answer supported by the clock observation."
        } }
    });
    private static string SearchPlan(string id, string query) => JsonSerializer.Serialize(new
    {
        planId = "plan-" + id,
        description = "Search the explicitly bound public web capability",
        steps = new[] { new
        {
            stepId = "step-" + id, toolId = "web.search", kind = "action", effect = "externalSideEffect", input = new { query },
            reason = "Retrieve current public evidence for the user's question."
        } }
    });

    private sealed class Probe
    {
        public List<ExperimentTraceInput> Events { get; } = [];
        public StringBuilder Output { get; } = new();
        public ExperimentExecutionContext Context { get; }
        public Probe(ExperimentSettings settings, IReadOnlyList<ExperimentMessage>? history = null)
        {
            var turn = new ExperimentTurn("turn-1", "conversation-1", 1, "Current user", "", "running", DateTimeOffset.UtcNow,
                null, 1, settings, "transcript-only");
            Context = new("conversation-1", turn, history ?? [], Path.Combine(Path.GetTempPath(), "agentica-executor-unused"),
                item => { lock (Events) Events.Add(item); return Task.CompletedTask; },
                text => { Output.Append(text); return Task.CompletedTask; });
        }
    }
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Urls { get; } = [];
        public List<string> CountBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("fixture-secret", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath == "/v1/responses/input_tokens")
            {
                CountBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                // These fixtures qualify streaming, binding and generation call limits;
                // the native-count contract and real measured budgets have separate fixtures.
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                    "{\"object\":\"response.input_tokens\",\"input_tokens\":1024}", Encoding.UTF8, "application/json")
                };
            }
            Urls.Add(request.RequestUri!.ToString());
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return response(Urls.Count);
        }
    }

    private sealed class SpyHostPlannerFactory : ILabPlannerFactory
    {
        public int CreateCalls { get; private set; }
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            CreateCalls++;
            throw new InvalidOperationException("The external-host fixture planner must not be used by chat.");
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class WaitingStream(string initial) : Stream
    {
        private readonly byte[] _initial = Encoding.UTF8.GetBytes(initial);
        private int _position;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _initial.Length)
            {
                var count = Math.Min(buffer.Length, _initial.Length - _position);
                _initial.AsMemory(_position, count).CopyTo(buffer); _position += count;
                return count;
            }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

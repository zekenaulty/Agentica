using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Embeddings;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Chat.Recall;
using Agentica.Lab.Web.Providers;
using Agentica.Tools;
using Agentica.Artifacts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

/// <summary>Local host proof with real canonical/projection storage and intercepted HTTP, not model-quality proof.</summary>
public sealed class ExperimentSemanticRecallTests
{
    private const string Query = "footwear restoration location";
    private const string AgentQuery = "orbital instrumentation";
    private const string OldFact = "A cobbler mends worn boots near the eastern gate.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Real_lab_service_searches_an_old_nonlexical_source_reads_it_refines_and_saves_the_answer()
    {
        using var fixture = new Fixture();
        var settings = HarnessSettings();
        var chat = fixture.Store.Create(settings, "Scoped semantic experiment");
        var old = Complete(fixture.Store, chat.Id, OldFact, "Acknowledged as historical source data.");
        var recent = Complete(fixture.Store, chat.Id, "Today we are discussing the weather.", "Rain is expected.");
        var sourceId = old.Id + ":user";
        Assert.DoesNotContain(Query, OldFact, StringComparison.OrdinalIgnoreCase);
        fixture.Store.RebuildIndex(chat.Id);
        Assert.Equal("NO_MATCH", fixture.Store.BindEvidenceSource(chat.Id, recent.Sequence).Search(new(Query)).Status);
        using var generation = new GenerationHandler((call, body) => call switch
        {
            1 => Plan("search", ExperimentToolIds.ConversationSearch, new { query = Query, limit = 1, maxCharacters = 2000 }),
            2 => ReadAfterSearch(body, sourceId),
            3 => AnswerAfterRead(body, sourceId),
            _ => throw new InvalidOperationException("The fixture must finish after search, read and answer.")
        });
        using var http = new HttpClient(generation);
        var configuration = Configuration();
        using var executor = new OpenAiExperimentExecutor(configuration, http);
        using var service = new ExperimentService(fixture.Store, executor, configuration, fixture.Recall);
        var started = await service.SendAsync(chat.Id, new(Query), TestContext.Current.CancellationToken);
        var finished = await WaitForCompletion(fixture.Store, started);
        var events = Events(fixture.Store, chat.Id, finished.Id);

        Assert.True(finished.Status == "completed", Failure(finished, generation, events));
        Assert.Contains("eastern gate", finished.AssistantText, StringComparison.Ordinal);
        Assert.Contains(sourceId, finished.AssistantText, StringComparison.Ordinal);
        Assert.Equal(3, generation.Bodies.Count);
        Assert.DoesNotContain(OldFact, generation.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("Rain is expected", generation.Bodies[0], StringComparison.Ordinal);
        Assert.Contains(OldFact, generation.Bodies[1], StringComparison.Ordinal);
        Assert.Contains(sourceId, generation.Bodies[2], StringComparison.Ordinal);
        Assert.Contains(events, item => item.Type == "harness.outcome");
        Assert.Contains(events, item => item.Type == "turn.completed");
        var search = Data(Assert.Single(events, item => item.Type == "recall.search"));
        Assert.Equal(chat.Id, search.GetProperty("scopeRef").GetString());
        Assert.Equal(recent.Sequence, search.GetProperty("asOfSequence").GetInt32());
        var hit = Assert.Single(search.GetProperty("hits").EnumerateArray());
        Assert.Equal(sourceId, hit.GetProperty("sourceId").GetString());
        Assert.Equal(OldFact, hit.GetProperty("excerpt").GetString());
        Assert.Equal("user", hit.GetProperty("role").GetString());
        var citation = hit.GetProperty("semanticCitation");
        Assert.Equal("local-owner", citation.GetProperty("authorId").GetString());
        Assert.Equal("Participant", citation.GetProperty("perspective").GetString());
        Assert.Equal(ExperimentSemanticRecall.Profile.ProfileId, citation.GetProperty("profileId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(citation.GetProperty("exportId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(citation.GetProperty("projectionId").GetString()));
        var read = Data(Assert.Single(events, item => item.Type == "recall.read"));
        Assert.Equal(OldFact, Assert.Single(read.GetProperty("hits").EnumerateArray()).GetProperty("excerpt").GetString());
        Assert.Equal(3, fixture.Embeddings.Batches.Count); // Participant documents, agent documents, one shared query.
        Assert.Equal(1, fixture.Embeddings.QueryCalls);
        Assert.All(fixture.Exports(), export => Assert.Equal(chat.Id, export.Scope.ConversationId));

        static string ReadAfterSearch(string body, string source)
        {
            Assert.Contains(OldFact, body, StringComparison.Ordinal);
            Assert.Contains("semanticCitation", body, StringComparison.Ordinal);
            return Refine("search", Plan("read", ExperimentToolIds.ConversationRead,
                new { messageId = source, characterOffset = 0, maxCharacters = 2000 }));
        }

        static string AnswerAfterRead(string body, string source)
        {
            Assert.Contains(OldFact, body, StringComparison.Ordinal);
            return Refine("read", Plan("answer", OpenAiExperimentExecutor.ResponseToolId,
                new { content = "The cobbler works near the eastern gate. Historical citation: " + source + "." }));
        }
    }

    [Fact]
    public async Task Reopening_both_stores_reuses_document_projections_and_keeps_other_chats_out_of_the_cut()
    {
        using var fixture = new Fixture();
        var chat = fixture.Store.Create(new(), "First scope");
        var other = fixture.Store.Create(new(), "Other scope");
        var own = Complete(fixture.Store, chat.Id, OldFact, "First scope assistant.");
        var foreign = Complete(fixture.Store, other.Id, "A cobbler handles classified-other-chat boots in the forbidden tower.", "Other scope assistant.");
        var foreignResult = await fixture.Bind(other.Id, foreign.Sequence).SearchAsync(new(Query, TopK: 1, Role: "user"), TestContext.Current.CancellationToken);
        Assert.Equal(foreign.Id + ":user", Assert.Single(foreignResult.Hits).Source.SourceId);
        var first = await fixture.Bind(chat.Id, own.Sequence).SearchAsync(new(Query, TopK: 1, Role: "user"), TestContext.Current.CancellationToken);
        var firstHit = Assert.Single(first.Hits);
        Assert.Equal(own.Id + ":user", firstHit.Source.SourceId);
        Assert.Equal(OldFact, firstHit.Excerpt);
        var documents = fixture.Embeddings.DocumentCalls;
        var requests = fixture.Embeddings.Batches.Count;
        var exports = fixture.Exports().Select(item => item.ExportId).Order(StringComparer.Ordinal).ToArray();

        fixture.Reopen();
        var reopened = await fixture.Bind(chat.Id, own.Sequence).SearchAsync(new(Query, TopK: 1, Role: "user"), TestContext.Current.CancellationToken);

        Assert.Equal("FOUND", reopened.Status);
        Assert.Equal(documents, fixture.Embeddings.DocumentCalls);
        Assert.Equal(requests + 1, fixture.Embeddings.Batches.Count); // A new bound turn embeds its query, not old documents.
        Assert.Equal(exports, fixture.Exports().Select(item => item.ExportId).Order(StringComparer.Ordinal));
        var hit = Assert.Single(reopened.Hits);
        Assert.Equal(firstHit.SemanticCitation!.ProjectionId, hit.SemanticCitation!.ProjectionId);
        Assert.Equal(firstHit.SemanticCitation.ExportId, hit.SemanticCitation.ExportId);
        Assert.Equal(chat.Id, reopened.ScopeRef);
        Assert.Equal(2, reopened.Coverage.EligibleSourceCount);
        Assert.Equal(own.Id + ":user", hit.Source.SourceId);
        Assert.DoesNotContain("classified-other-chat", JsonSerializer.Serialize(reopened, Json), StringComparison.Ordinal);
        Assert.Equal("UNAVAILABLE", fixture.Bind(chat.Id, own.Sequence).ReadExact(foreign.Id + ":user").Status);
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "experiments.sqlite3")));
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "semantic-memory.sqlite3")));
    }

    [Fact]
    public async Task Multi_chunk_results_reopen_exact_nonzero_utf16_ranges_with_distinct_participant_and_agent_authorship()
    {
        using var fixture = new Fixture();
        var chat = fixture.Store.Create(new(), "Attributed chunks");
        var userText = new string('x', 1999) + "😀 " + OldFact;
        var agentText = new string('y', 2000) + "An astrolabe measures the stars above the observatory.";
        var old = Complete(fixture.Store, chat.Id, userText, agentText);
        var source = fixture.Bind(chat.Id, old.Sequence);
        var participant = await source.SearchAsync(new(Query, TopK: 1, Role: "user"), TestContext.Current.CancellationToken);
        var agent = await source.SearchAsync(new(AgentQuery, TopK: 1, Role: "assistant"), TestContext.Current.CancellationToken);
        var userHit = Assert.Single(participant.Hits);
        var agentHit = Assert.Single(agent.Hits);

        Assert.Equal(1999, userHit.StartOffset); // The surrogate pair moves intact into the next chunk.
        Assert.Equal(2000, agentHit.StartOffset);
        Assert.Equal(userText[userHit.StartOffset..userHit.EndOffset], userHit.Excerpt);
        Assert.Equal(agentText[agentHit.StartOffset..agentHit.EndOffset], agentHit.Excerpt);
        Assert.Equal(Hash(userText), userHit.Source.ContentHash);
        Assert.Equal(Hash(agentText), agentHit.Source.ContentHash);
        Assert.Equal(old.Id + ":user", userHit.Source.SourceId);
        Assert.Equal(old.Id + ":assistant", agentHit.Source.SourceId);
        Assert.Equal("Participant", userHit.SemanticCitation!.Perspective);
        Assert.Equal("local-owner", userHit.SemanticCitation.AuthorId);
        Assert.Equal("Agent", agentHit.SemanticCitation!.Perspective);
        Assert.Equal("lab-assistant", agentHit.SemanticCitation.AuthorId);
        Assert.Equal("local-owner", agentHit.SemanticCitation.ParticipantId);
        Assert.Equal("lab-assistant", userHit.SemanticCitation.AgentId);
        Assert.All(new[] { userHit, agentHit }, hit =>
        {
            var citation = hit.SemanticCitation!;
            Assert.Equal(ExperimentSemanticRecall.Profile.ProfileId, citation.ProfileId);
            var export = Assert.Single(fixture.Exports(), item => item.ExportId == citation.ExportId);
            Assert.Equal(citation.Perspective, export.Perspective);
            var projection = Assert.Single(export.Projections, item => item.ProjectionId == citation.ProjectionId);
            Assert.Equal(hit.StartOffset, projection.StartUtf16);
            Assert.Equal(hit.Excerpt.Length, projection.LengthUtf16);
            Assert.Equal(hit.Source.SourceId, projection.ReferenceId);
            Assert.Equal(citation.AuthorId, projection.AuthorId);
            Assert.Equal("sha256:" + hit.Source.ContentHash, projection.SourceContentHash);
            Assert.Equal(hit.Excerpt, Assert.Single(source.ReadExact(hit.Source.SourceId, hit.StartOffset, hit.Excerpt.Length).Hits).Excerpt);
        });
        var stored = fixture.Exports();
        Assert.Equal(4, stored.Sum(item => item.Projections.Count));
        Assert.DoesNotContain("cobbler", JsonSerializer.Serialize(stored, Json), StringComparison.Ordinal);
        Assert.DoesNotContain("astrolabe", JsonSerializer.Serialize(stored, Json), StringComparison.Ordinal);
        Assert.Equal(2, fixture.Embeddings.DocumentCalls);
        Assert.Equal(2, fixture.Embeddings.QueryCalls);
    }

    [Fact]
    public async Task Recall_off_and_semantic_context_preview_never_embed_or_create_projection_exports()
    {
        using var fixture = new Fixture();
        var off = new ExperimentSettings { HistoryPolicy = "recent", ThreadSemanticRecallEnabled = true };
        var chat = fixture.Store.Create(off, "Off remains Off");
        Complete(fixture.Store, chat.Id, OldFact, "Historical assistant.");
        using var generation = new GenerationHandler((_, _) => "An isolated direct answer.");
        using var http = new HttpClient(generation);
        var configuration = Configuration();
        using var executor = new OpenAiExperimentExecutor(configuration, http);
        using var service = new ExperimentService(fixture.Store, executor, configuration, fixture.Recall);
        var offPreview = Data(await service.CheckContextAsync(chat.Id, new(Query), TestContext.Current.CancellationToken));
        Assert.False(offPreview.GetProperty("inferenceDispatched").GetBoolean());
        Assert.False(offPreview.GetProperty("turnCreated").GetBoolean());
        var turn = await service.SendAsync(chat.Id, new(Query), TestContext.Current.CancellationToken);
        Assert.Equal("completed", (await WaitForCompletion(fixture.Store, turn)).Status);
        Assert.Empty(fixture.Embeddings.Batches);
        Assert.Empty(fixture.Exports());
        Assert.Single(generation.Bodies);
        var current = fixture.Store.Get(chat.Id);
        fixture.Store.UpdateSettings(chat.Id, new(current.SettingsRevision, off with
        {
            ThreadRecallEnabled = true,
            ThreadRecallMode = "auto",
            ThreadRecallQuery = Query,
            ThreadRecallNeighborRadius = 0
        }));
        var before = fixture.Store.GetHead(chat.Id).LatestSequence;
        var semanticPreview = Data(await service.CheckContextAsync(chat.Id, new(Query), TestContext.Current.CancellationToken));

        Assert.Contains("no embedding request made", semanticPreview.GetProperty("semanticRetrieval").GetString(), StringComparison.Ordinal);
        Assert.False(semanticPreview.GetProperty("inferenceDispatched").GetBoolean());
        Assert.False(semanticPreview.GetProperty("turnCreated").GetBoolean());
        Assert.Equal(before, fixture.Store.GetHead(chat.Id).LatestSequence);
        Assert.Empty(fixture.Embeddings.Batches);
        Assert.Empty(fixture.Exports());
        Assert.Single(generation.Bodies);
    }

    [Fact]
    public async Task An_unknown_embedding_dispatch_stays_unavailable_without_same_turn_retry_or_lexical_substitution()
    {
        using var fixture = new Fixture();
        var chat = fixture.Store.Create(new(), "Unknown provider outcome");
        var old = Complete(fixture.Store, chat.Id, OldFact, "Historical assistant.");
        fixture.Store.RebuildIndex(chat.Id);
        var lexical = fixture.Store.BindEvidenceSource(chat.Id, old.Sequence).Search(new("cobbler"));
        Assert.Equal("COMPLETE", lexical.Status);
        Assert.Equal(OldFact, Assert.Single(lexical.Hits).Excerpt);
        fixture.Embeddings.FailAfterDispatch = true;
        var source = fixture.Bind(chat.Id, old.Sequence);
        var first = await source.SearchAsync(new("cobbler", TopK: 1, Role: "user"), TestContext.Current.CancellationToken);
        var second = await source.SearchAsync(new(Query, TopK: 1, Role: "user"), TestContext.Current.CancellationToken);

        Assert.Equal("UNAVAILABLE", first.Status);
        Assert.Equal("UNAVAILABLE", second.Status);
        Assert.Empty(first.Hits);
        Assert.Empty(second.Hits);
        Assert.True(first.Truncated);
        Assert.NotEqual("COMPLETE", first.Coverage.Status);
        Assert.Single(fixture.Embeddings.Batches);
        Assert.Empty(fixture.Exports());
        var indeterminate = Data(Assert.Single(fixture.Traces, item => item.Type == "recall.embedding.indeterminate"));
        Assert.False(indeterminate.GetProperty("automaticRetry").GetBoolean());
        Assert.False(indeterminate.GetProperty("usageKnown").GetBoolean());
        Assert.DoesNotContain(fixture.Traces, item => item.Type == "recall.embedding.completed");
        Assert.Contains(first.Reasons, reason => reason.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("synthetic dispatch lost", JsonSerializer.Serialize(first, Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_catalog_declares_paid_additive_effects_and_refuses_nonzero_offsets_before_dispatch()
    {
        using var fixture = new Fixture();
        var settings = HarnessSettings();
        var chat = fixture.Store.Create(settings, "Effect contract");
        var old = Complete(fixture.Store, chat.Id, OldFact, "Historical assistant.");
        var source = fixture.Bind(chat.Id, old.Sequence);
        using var sourceLifetime = source as IDisposable;
        var now = DateTimeOffset.UtcNow;
        var turn = new ExperimentTurn("catalog-preview", chat.Id, old.Sequence + 1, Query, "", "running", now,
            null, chat.SettingsRevision, settings, "transcript-only");
        var context = new ExperimentExecutionContext(chat.Id, turn, [], fixture.Store.WorkspaceRoot,
            item => { fixture.Traces.Add(item); return Task.CompletedTask; }, _ => Task.CompletedTask)
        { EvidenceSource = source };
        var catalog = ExperimentTools.CreateCatalog(context);
        var search = catalog.Resolve(ExperimentToolIds.ConversationSearch)!;
        var read = catalog.Resolve(ExperimentToolIds.ConversationRead)!;

        Assert.Equal(ToolKind.Action, search.Descriptor.Kind);
        Assert.Equal(ToolEffect.ExternalSideEffect, search.Descriptor.Effect);
        Assert.Equal(ToolEffect.ExternalSideEffect, search.Security.Effect);
        Assert.Equal(ToolRetrySafety.Additive, search.Descriptor.RetrySafety);
        Assert.Equal(ToolRetrySafety.Additive, search.Security.RetrySafety);
        Assert.Equal(0d, search.Descriptor.InputSchema!.Fields.Single(field => field.Name == "offset").Maximum);
        Assert.Equal(ToolKind.Query, read.Descriptor.Kind);
        Assert.Equal(ToolEffect.ReadOnly, read.Descriptor.Effect);
        Assert.Equal(ToolRetrySafety.Idempotent, read.Security.RetrySafety);
        var refused = await search.Tool.ExecuteAsync(new("run", "search", ExperimentToolIds.ConversationSearch,
            new Dictionary<string, object?> { ["query"] = Query, ["offset"] = 1 }), TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Refused, refused.Receipt.Status);
        Assert.Equal("recall.input", Data(refused.Receipt.Data).GetProperty("result").GetProperty("code").GetString());
        Assert.Equal("recall.query", (await Assert.ThrowsAsync<ExperimentException>(() => source.SearchAsync(
            new(Query, Offset: 1), TestContext.Current.CancellationToken))).Code);
        Assert.Empty(fixture.Embeddings.Batches);
        Assert.Empty(fixture.Exports());
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("later-version")]
    public async Task Unusable_semantic_sidecars_leave_lab_startup_off_turns_and_previews_available(string state)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("agentica-semantic-unavailable-").FullName;
        try
        {
            var chatDirectory = System.IO.Directory.CreateDirectory(Path.Combine(directory, "chat")).FullName;
            var sidecar = Path.Combine(chatDirectory, "semantic-memory.sqlite3");
            if (state == "broken") await File.WriteAllTextAsync(sidecar, "synthetic non-SQLite sidecar", TestContext.Current.CancellationToken);
            else
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sidecar, Pooling = false }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=99";
                command.ExecuteNonQuery();
            }
            var originalBytes = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);
            using var generation = new GenerationHandler((_, _) => "Baseline remains available.");
            using var generationHttp = new HttpClient(generation);
            using var embeddingHandler = new EmbeddingHandler();
            using var embeddingHttp = new HttpClient(embeddingHandler, disposeHandler: false);
            var provider = new OpenAiEmbeddingClient(new(ExperimentSemanticRecall.Profile, "synthetic-embedding-key"), embeddingHttp);
            var configuration = Configuration();
            using var executor = new OpenAiExperimentExecutor(configuration, generationHttp);
            await using var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"],
                storageDirectory: directory, openAiConfiguration: configuration, experimentExecutor: executor,
                memoryEmbeddingProvider: provider);
            await app.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                var store = app.Services.GetRequiredService<ExperimentStore>();
                var service = app.Services.GetRequiredService<ExperimentService>();
                var settings = new ExperimentSettings { HistoryPolicy = "recent", ThreadSemanticRecallEnabled = true };
                var chat = store.Create(settings, "Optional sidecar failure");
                Complete(store, chat.Id, OldFact, "Historical assistant.");
                var started = await service.SendAsync(chat.Id, new(Query), TestContext.Current.CancellationToken);
                Assert.Equal("completed", (await WaitForCompletion(store, started)).Status);
                Assert.Empty(embeddingHandler.Batches);
                Assert.Single(generation.Bodies);
                var current = store.Get(chat.Id);
                store.UpdateSettings(chat.Id, new(current.SettingsRevision, settings with
                {
                    ThreadRecallEnabled = true,
                    ThreadRecallMode = "auto",
                    ThreadRecallQuery = Query,
                    ThreadRecallNeighborRadius = 0
                }));
                var preview = Data(await service.CheckContextAsync(chat.Id, new(Query), TestContext.Current.CancellationToken));
                Assert.False(preview.GetProperty("inferenceDispatched").GetBoolean());
                Assert.False(preview.GetProperty("turnCreated").GetBoolean());
                Assert.Empty(embeddingHandler.Batches);
                Assert.Single(generation.Bodies);
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));

                var semantic = app.Services.GetRequiredService<ExperimentSemanticRecall>();
                var source = semantic.Bind(chat.Id, started.Sequence, _ => Task.CompletedTask);
                using var sourceLifetime = source as IDisposable;
                var admitted = await source.SearchAsync(new(Query, TopK: 1), TestContext.Current.CancellationToken);
                Assert.Equal("UNAVAILABLE", admitted.Status);
                Assert.Empty(admitted.Hits);
                Assert.Empty(embeddingHandler.Batches);
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await app.StopAsync(cleanup.Token);
            }
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    private static ExperimentSettings HarnessSettings() => new()
    {
        Mode = "harness",
        ThreadRecallEnabled = true,
        ThreadSemanticRecallEnabled = true,
        ThreadRecallMode = "agent",
        ThreadRecallRecentTurns = 1,
        ThreadRecallNeighborRadius = 0,
        ThreadRecallTopK = 1,
        HistoryPolicy = "recent",
        MaxHistoryMessages = 2,
        MaxToolResultCharacters = 10000,
        MaxToolEvidenceCharacters = 30000,
        EnabledTools = [ExperimentToolIds.ConversationSearch, ExperimentToolIds.ConversationRead]
    };

    private static OpenAiProviderConfiguration Configuration() => new(name => name == "OPENAI_API_KEY" ? "synthetic-generation-key" : null);

    private static ExperimentTurn Complete(ExperimentStore store, string chatId, string user, string answer)
    {
        var turn = store.Begin(chatId, new(user)).Turn;
        return store.Complete(chatId, turn.Id, new("completed", answer));
    }

    private static async Task<ExperimentTurn> WaitForCompletion(ExperimentStore store, ExperimentTurn turn)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var current = store.GetTurn(turn.ConversationId, turn.Id);
            if (current.Status != "running") return current;
            await Task.Delay(10, deadline.Token);
        }
    }

    private static IReadOnlyList<ExperimentEvent> Events(ExperimentStore store, string chatId, string turnId)
    {
        var result = new List<ExperimentEvent>();
        while (true)
        {
            var page = store.ReadEvents(chatId, turnId, result.Count == 0 ? 0 : result[^1].Sequence, 100);
            if (page.Count == 0) return result;
            result.AddRange(page);
            Assert.True(result.Count <= 4001, "The host trace must remain bounded.");
        }
    }

    private static JsonElement Data(ExperimentEvent item) => Data(item.Data);
    private static JsonElement Data(ExperimentTraceInput item) => Data(item.Data);
    private static JsonElement Data(object? value) => JsonSerializer.SerializeToElement(value, Json);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Failure(ExperimentTurn turn, GenerationHandler generation, IReadOnlyList<ExperimentEvent> events)
    {
        var traces = events.Where(item => item.Type.StartsWith("recall.", StringComparison.Ordinal) ||
            item.Type.StartsWith("harness.", StringComparison.Ordinal) || item.Type == "context.tool-evidence")
            .Select(item => item.Type + ": " + Prefix(Data(item.Data).GetRawText(), 5000));
        var requests = generation.Bodies.Select((body, index) => $"Request {index + 1}: " + Prefix(body, 12000));
        return $"{turn.Status}: {turn.ErrorCode}: {turn.ErrorMessage}; generation calls={generation.Bodies.Count}; " +
            $"fixture HTTP assertion={generation.Failure}; event types={string.Join(", ", events.Select(item => item.Type))}\n" +
            string.Join("\n", traces.Concat(requests));

        static string Prefix(string value, int limit) => value.Length <= limit ? value : value[..limit] + " [diagnostic truncated]";
    }

    private static string Plan(string id, string tool, object input) => JsonSerializer.Serialize(new
    {
        planId = "plan-" + id,
        description = "Locate the exact historical source, inspect it, then answer.",
        steps = new[]
        {
            new
            {
                stepId = "step-" + id,
                toolId = tool,
                kind = tool is OpenAiExperimentExecutor.ResponseToolId or ExperimentToolIds.ConversationSearch ? "action" : "query",
                effect = tool == ExperimentToolIds.ConversationSearch ? "externalSideEffect" :
                    tool == OpenAiExperimentExecutor.ResponseToolId ? "writesLocalState" : "readOnly",
                input,
                reason = "Continue from the returned bounded historical evidence."
            }
        }
    });

    private static string Refine(string from, string plan) => JsonSerializer.Serialize(new
    {
        fromPlanId = "plan-" + from,
        reason = "Use the returned source evidence for the next observation or answer.",
        evidence = Array.Empty<object>(),
        refinedPlan = JsonSerializer.Deserialize<JsonElement>(plan)
    });

    private sealed class GenerationHandler(Func<int, string, string> response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public Exception? Failure { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath == "/v1/responses/input_tokens")
                return new(HttpStatusCode.OK)
                {
                    // Synthetic native count: these tests qualify integration, not tokenizer accuracy.
                    Content = new StringContent("{\"object\":\"response.input_tokens\",\"input_tokens\":1024}", Encoding.UTF8, "application/json")
                };
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            Bodies.Add(body);
            string text;
            try { text = response(Bodies.Count, body); }
            catch (Exception error) { Failure = error; throw; }
            var completed = JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new
                {
                    id = "synthetic-semantic-response-" + Bodies.Count,
                    status = "completed",
                    output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } }
                }
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + completed + "\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class EmbeddingHandler : HttpMessageHandler
    {
        public List<string[]> Batches { get; } = [];
        public bool FailAfterDispatch { get; set; }
        public int QueryCalls => Batches.Count(batch => batch.Length == 1 && batch[0] is Query or AgentQuery);
        public int DocumentCalls => Batches.Count - QueryCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(OpenAiEmbeddingClientOptions.DefaultEndpoint, request.RequestUri);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(ExperimentSemanticRecall.Profile.ModelId, document.RootElement.GetProperty("model").GetString());
            Assert.Equal(1536, document.RootElement.GetProperty("dimensions").GetInt32());
            var inputs = document.RootElement.GetProperty("input").EnumerateArray().Select(item => item.GetString()!).ToArray();
            Batches.Add(inputs);
            if (FailAfterDispatch) throw new HttpRequestException("synthetic dispatch lost after possible provider acceptance");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    @object = "list",
                    model = ExperimentSemanticRecall.Profile.ModelId,
                    data = inputs.Select((text, index) => new
                    {
                        @object = "embedding",
                        index,
                        embedding = Vector(text)
                    }).ToArray(),
                    usage = new { prompt_tokens = inputs.Length, total_tokens = inputs.Length }
                }), Encoding.UTF8, "application/json")
            };
        }

        private static float[] Vector(string text)
        {
            // Explicit synthetic semantic mapping, not a live embedding-quality assertion.
            var vector = new float[1536];
            var dimension = text == Query || text.Contains("cobbler", StringComparison.OrdinalIgnoreCase) ? 0
                : text == AgentQuery || text.Contains("astrolabe", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            vector[dimension] = 1;
            return vector;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _embeddingHttp;
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("agentica-semantic-recall-").FullName;
        public ExperimentStore Store { get; private set; }
        public SemanticMemoryStorageProvider Storage { get; private set; }
        public EmbeddingHandler Embeddings { get; } = new();
        public List<ExperimentTraceInput> Traces { get; } = [];
        public ExperimentSemanticRecall Recall => new(Store, Storage,
            () => new OpenAiEmbeddingClient(new(ExperimentSemanticRecall.Profile, "synthetic-embedding-key"), _embeddingHttp));

        public Fixture()
        {
            Store = new(Directory);
            Storage = new(Directory, Store);
            _embeddingHttp = new(Embeddings);
        }

        public IConversationEvidenceSource Bind(string chatId, int asOf) => Recall.Bind(chatId, asOf,
            item => { Traces.Add(item); return Task.CompletedTask; });

        public void Reopen()
        {
            Store.Dispose();
            Store = new(Directory);
            Storage = new(Directory, Store);
        }

        public IReadOnlyList<SemanticMemoryStorageExport> Exports()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Directory, "semantic-memory.sqlite3"),
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body_json FROM semantic_memory_exports ORDER BY export_id";
            using var reader = command.ExecuteReader();
            var exports = new List<SemanticMemoryStorageExport>();
            while (reader.Read()) exports.Add(JsonSerializer.Deserialize<SemanticMemoryStorageExport>(reader.GetString(0), Json)!);
            return exports;
        }

        public void Dispose()
        {
            _embeddingHttp.Dispose();
            Store.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

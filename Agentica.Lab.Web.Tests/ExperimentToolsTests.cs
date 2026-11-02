using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Lab.Web.Chat;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentToolsTests
{
    [Fact]
    public void Utilities_are_opt_in_and_clean_room_never_mounts_them()
    {
        using var fixture = new ScratchFixture();
        Assert.Empty(ExperimentTools.CreateCatalog(fixture.Context()).Descriptors);
        var dirtySelection = fixture.Context(ExperimentToolIds.Clock) with
        {
            Turn = fixture.Context().Turn with { Settings = new ExperimentSettings { EnabledTools = [ExperimentToolIds.Clock] } }
        };
        Assert.Empty(ExperimentTools.CreateRegistrations(dirtySelection));
        Assert.False(Directory.Exists(fixture.Workspace));
    }

    [Fact]
    public void Every_installed_utility_compiles_with_explicit_schema_effect_and_provenance()
    {
        using var fixture = new ScratchFixture();
        var selected = ExperimentTools.Capabilities.Select(capability => capability.Id).ToArray();
        var catalog = ExperimentTools.CreateCatalog(fixture.Context(selected));
        Assert.Equal(9, catalog.Descriptors.Count);
        foreach (var capability in ExperimentTools.Capabilities)
        {
            var registration = Assert.IsType<CompiledToolRegistration>(catalog.Resolve(capability.Id));
            Assert.NotNull(registration.Descriptor.InputSchema);
            Assert.Equal(capability.Effect, registration.Security.Effect.ToString());
            Assert.Equal(ExperimentTools.Source, capability.Source);
            Assert.Equal(ExperimentTools.Version, capability.Version);
            Assert.Equal(capability.Source, registration.Security.Provenance.Source);
            Assert.Equal(ToolApprovalRequirement.None, registration.Security.ApprovalRequirement);
            Assert.DoesNotContain(ToolDataBoundary.Unknown, registration.Security.Reads);
        }
        Assert.Equal(ToolEffect.WritesLocalState, catalog.Resolve(ExperimentToolIds.ScratchWrite)!.Security.Effect);
        Assert.Equal(ToolRetrySafety.MutationUnsafe, catalog.Resolve(ExperimentToolIds.ScratchWrite)!.Security.RetrySafety);
        Assert.DoesNotContain(ExperimentTools.Capabilities, capability => capability.Id.Contains("memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_bindings_and_mismatched_conversations_fail_closed()
    {
        using var fixture = new ScratchFixture();
        Assert.Throws<ExperimentException>(() => ExperimentTools.CreateCatalog(fixture.Context("not-installed")));
        var context = fixture.Context(ExperimentToolIds.Clock) with { ConversationId = "other-conversation" };
        Assert.Throws<ArgumentException>(() => ExperimentTools.CreateCatalog(context));
    }

    [Fact]
    public async Task Registration_snapshots_the_selected_context_without_other_conversation_handles()
    {
        using var fixture = new ScratchFixture();
        var messages = new List<ExperimentMessage> { new("source-a", "user", "retained phrase", DateTimeOffset.UtcNow) };
        var context = fixture.Context(ExperimentToolIds.ConversationSearch) with { History = messages };
        var tool = ExperimentTools.CreateCatalog(context).Resolve(ExperimentToolIds.ConversationSearch)!.Tool;
        messages.Add(new("source-b", "user", "foreign phrase", DateTimeOffset.UtcNow));
        var result = await tool.ExecuteAsync(Invocation(ExperimentToolIds.ConversationSearch, ("query", "foreign phrase")), TestContext.Current.CancellationToken);
        Assert.Equal(0, Payload(result).GetProperty("totalMatches").GetInt32());
        var included = await tool.ExecuteAsync(Invocation(ExperimentToolIds.ConversationSearch, ("query", "RETAINED")), TestContext.Current.CancellationToken);
        Assert.Equal("source-a", Payload(included).GetProperty("matches")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("(2 + 3) * 4 - 8 / 2", "16")]
    [InlineData("-2 * (3 + 5) % 7", "-2")]
    [InlineData("1e3 + .25", "1000.25")]
    public async Task Arithmetic_uses_bounded_decimal_grammar(string expression, string expected)
    {
        using var fixture = new ScratchFixture();
        var result = await ExecuteAsync(fixture.Context(ExperimentToolIds.Calculate), ExperimentToolIds.Calculate, ("expression", expression));
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(expected, Payload(result).GetProperty("exactDecimal").GetString());
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText('secret')")]
    [InlineData("1 / 0")]
    [InlineData("1e999")]
    [InlineData("NaN")]
    [InlineData("Math.Pow(2, 8)")]
    [InlineData("1 + 2; exit")]
    public async Task Arithmetic_refuses_code_nonfinite_and_range_errors(string expression)
    {
        using var fixture = new ScratchFixture();
        var result = await ExecuteAsync(fixture.Context(ExperimentToolIds.Calculate), ExperimentToolIds.Calculate, ("expression", expression));
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
    }

    [Fact]
    public async Task Arithmetic_nesting_is_bounded()
    {
        using var fixture = new ScratchFixture();
        var result = await ExecuteAsync(fixture.Context(ExperimentToolIds.Calculate), ExperimentToolIds.Calculate,
            ("expression", new string('(', 40) + "1" + new string(')', 40)));
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
    }

    [Fact]
    public async Task Clock_reports_explicit_UTC_without_host_credential_access()
    {
        using var fixture = new ScratchFixture();
        var before = DateTimeOffset.UtcNow;
        var result = await ExecuteAsync(fixture.Context(ExperimentToolIds.Clock), ExperimentToolIds.Clock);
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("UTC", Payload(result).GetProperty("timeZone").GetString());
        Assert.InRange(Payload(result).GetProperty("utc").GetDateTimeOffset(), before, DateTimeOffset.UtcNow);
        Assert.False(Directory.Exists(fixture.Workspace));
    }

    [Fact]
    public async Task JSON_pointer_preserves_literal_keys_and_reports_missing_paths()
    {
        using var fixture = new ScratchFixture();
        var context = fixture.Context(ExperimentToolIds.InspectJson);
        var result = await ExecuteAsync(context, ExperimentToolIds.InspectJson,
            ("json", "{\"a/b\":{\"~key\":[10,20]}}"), ("pointer", "/a~1b/~0key/1"));
        Assert.Equal("20", Payload(result).GetProperty("content").GetString());
        Assert.Equal("Number", Payload(result).GetProperty("kind").GetString());
        foreach (var pointer in new[] { "/missing", "/a~2b", "/a~1b/~0key/01", "a/b" })
        {
            var refused = await ExecuteAsync(context, ExperimentToolIds.InspectJson,
                ("json", "{\"a/b\":{\"~key\":[10,20]}}"), ("pointer", pointer));
            Assert.Equal(ReceiptStatus.Refused, refused.Receipt.Status);
        }
    }

    [Fact]
    public async Task Text_inspection_pages_counts_and_literal_matches()
    {
        using var fixture = new ScratchFixture();
        var result = await ExecuteAsync(fixture.Context(ExperimentToolIds.InspectText), ExperimentToolIds.InspectText,
            ("text", "abc\nabc"), ("needle", "abc"), ("offset", 4), ("maxCharacters", 3));
        var data = Payload(result);
        Assert.Equal(7, data.GetProperty("characters").GetInt32());
        Assert.Equal(2, data.GetProperty("lines").GetInt32());
        Assert.Equal("abc", data.GetProperty("content").GetString());
        Assert.Equal(new[] { 0, 4 }, data.GetProperty("matchOffsets").EnumerateArray().Select(item => item.GetInt32()).ToArray());
    }

    [Fact]
    public async Task Result_ceiling_accounts_for_JSON_escaping_and_reports_truncation()
    {
        using var fixture = new ScratchFixture();
        var context = fixture.Context(ExperimentToolIds.InspectText);
        context = context with { Turn = context.Turn with { Settings = context.Turn.Settings with { MaxToolResultCharacters = 256 } } };
        var result = await ExecuteAsync(context, ExperimentToolIds.InspectText,
            ("text", string.Concat(Enumerable.Repeat("\"\\\n😀", 1000))), ("maxCharacters", 12000));
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.True(JsonSerializer.Serialize(result.Receipt.Data).Length <= 256);
        Assert.True(JsonSerializer.Serialize(result.Observation!.Data).Length <= 256);
        Assert.True(JsonSerializer.SerializeToElement(result.Receipt.Data).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Conversation_read_and_search_use_only_supplied_messages_and_current_user()
    {
        using var fixture = new ScratchFixture();
        var context = fixture.Context(ExperimentToolIds.ConversationRead, ExperimentToolIds.ConversationSearch) with
        {
            History = [new("older", "assistant", "isolated alpha", DateTimeOffset.UtcNow)]
        };
        var read = await ExecuteAsync(context, ExperimentToolIds.ConversationRead);
        Assert.Equal(2, Payload(read).GetProperty("totalMessages").GetInt32());
        var current = await ExecuteAsync(context, ExperimentToolIds.ConversationSearch, ("query", "current input"));
        Assert.Equal(1, Payload(current).GetProperty("totalMatches").GetInt32());
        var absent = await ExecuteAsync(context, ExperimentToolIds.ConversationRead, ("messageId", "foreign-message"));
        Assert.Equal(ReceiptStatus.Refused, absent.Receipt.Status);
        var exact = await ExecuteAsync(context, ExperimentToolIds.ConversationRead, ("messageId", "older"), ("characterOffset", 9), ("maxCharacters", 5));
        Assert.Equal("alpha", Payload(exact).GetProperty("content").GetString());
    }

    [Fact]
    public async Task Scratch_create_read_list_and_existing_target_preserve_isolation()
    {
        using var fixture = new ScratchFixture();
        var context = fixture.Context(ExperimentToolIds.ScratchWrite, ExperimentToolIds.ScratchRead, ExperimentToolIds.ScratchList);
        var created = await ExecuteAsync(context, ExperimentToolIds.ScratchWrite, ("path", "notes/result.txt"), ("content", "saved αβγ"));
        Assert.Equal(ReceiptStatus.Succeeded, created.Receipt.Status);
        var read = await ExecuteAsync(context, ExperimentToolIds.ScratchRead, ("path", "notes/result.txt"), ("offset", 6), ("maxCharacters", 2));
        Assert.Equal("αβ", Payload(read).GetProperty("content").GetString());
        Assert.Equal(8, Payload(read).GetProperty("nextOffset").GetInt32());
        var listed = await ExecuteAsync(context, ExperimentToolIds.ScratchList, ("path", "notes"));
        Assert.Equal("result.txt", Payload(listed).GetProperty("entries")[0].GetProperty("name").GetString());
        var overwrite = await ExecuteAsync(context, ExperimentToolIds.ScratchWrite, ("path", "notes/result.txt"), ("content", "changed"));
        Assert.NotEqual(ReceiptStatus.Succeeded, overwrite.Receipt.Status);
        Assert.Equal("saved αβγ", await File.ReadAllTextAsync(Path.Combine(fixture.Workspace, "notes", "result.txt"), TestContext.Current.CancellationToken));
        var other = context with
        {
            WorkspaceDirectory = Path.Combine(fixture.Root, "other-conversation"), ConversationId = "other",
            Turn = context.Turn with { ConversationId = "other" }
        };
        var hidden = await ExecuteAsync(other, ExperimentToolIds.ScratchRead, ("path", "notes/result.txt"));
        Assert.Equal(ReceiptStatus.Refused, hidden.Receipt.Status);
        Assert.DoesNotContain(fixture.Root, JsonSerializer.Serialize(hidden), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("nested\\..\\outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("NUL")]
    [InlineData("dir/CON.txt")]
    [InlineData("trailing. ")]
    public async Task Scratch_paths_refuse_traversal_devices_streams_and_absolute_paths(string path)
    {
        using var fixture = new ScratchFixture();
        var result = await ExecuteAsync(fixture.Context(ExperimentToolIds.ScratchWrite), ExperimentToolIds.ScratchWrite, ("path", path), ("content", "no effect"));
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.False(Directory.Exists(fixture.Workspace));
    }

    [Fact]
    public async Task Scratch_link_components_are_refused_when_links_are_available()
    {
        using var fixture = new ScratchFixture();
        Directory.CreateDirectory(fixture.Workspace);
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside"));
        await File.WriteAllTextAsync(Path.Combine(outside.FullName, "private.txt"), "outside fixture", TestContext.Current.CancellationToken);
        var link = Path.Combine(fixture.Workspace, "linked");
        try { Directory.CreateSymbolicLink(link, outside.FullName); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("This host cannot create symbolic links; the link boundary needs a capable test host.");
            return;
        }
        try
        {
            var context = fixture.Context(ExperimentToolIds.ScratchRead, ExperimentToolIds.ScratchWrite, ExperimentToolIds.ScratchList);
            var read = await ExecuteAsync(context, ExperimentToolIds.ScratchRead, ("path", "linked/private.txt"));
            Assert.Equal(ReceiptStatus.Refused, read.Receipt.Status);
            var write = await ExecuteAsync(context, ExperimentToolIds.ScratchWrite, ("path", "linked/new.txt"), ("content", "refused"));
            Assert.Equal(ReceiptStatus.Refused, write.Receipt.Status);
            Assert.False(File.Exists(Path.Combine(outside.FullName, "new.txt")));
            var listing = await ExecuteAsync(context, ExperimentToolIds.ScratchList);
            Assert.Equal("blocked_link", Payload(listing).GetProperty("entries")[0].GetProperty("kind").GetString());
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Direct_registration_still_validates_unknown_fields_types_and_identity()
    {
        using var fixture = new ScratchFixture();
        var context = fixture.Context(ExperimentToolIds.Calculate);
        var unknown = await ExecuteAsync(context, ExperimentToolIds.Calculate, ("expression", "1"), ("unexpected", true));
        Assert.Equal(ReceiptStatus.Refused, unknown.Receipt.Status);
        var wrongType = await ExecuteAsync(context, ExperimentToolIds.Calculate, ("expression", 1));
        Assert.Equal(ReceiptStatus.Refused, wrongType.Receipt.Status);
        var tool = ExperimentTools.CreateCatalog(context).Resolve(ExperimentToolIds.Calculate)!.Tool;
        var mismatch = await tool.ExecuteAsync(Invocation("unbound", ("expression", "1")), TestContext.Current.CancellationToken);
        Assert.Equal(ReceiptStatus.Refused, mismatch.Receipt.Status);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_creates_no_scratch_effect()
    {
        using var fixture = new ScratchFixture();
        var tool = ExperimentTools.CreateCatalog(fixture.Context(ExperimentToolIds.ScratchWrite)).Resolve(ExperimentToolIds.ScratchWrite)!.Tool;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            Invocation(ExperimentToolIds.ScratchWrite, ("path", "never.txt"), ("content", "unused")), new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(fixture.Workspace));
    }

    private static JsonElement Payload(ToolResult result) => JsonSerializer.SerializeToElement(result.Receipt.Data).GetProperty("result");

    private static Task<ToolResult> ExecuteAsync(ExperimentExecutionContext context, string id, params (string Name, object? Value)[] input) =>
        ExperimentTools.CreateCatalog(context).Resolve(id)!.Tool.ExecuteAsync(Invocation(id, input), TestContext.Current.CancellationToken);

    private static ToolInvocation Invocation(string id, params (string Name, object? Value)[] input) =>
        new("run", "step", id, input.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal));

    private sealed class ScratchFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("agentica-experiment-tools-").FullName;
        public string Workspace => Path.Combine(Root, "conversation-a");

        public ExperimentExecutionContext Context(params string[] enabled)
        {
            var settings = new ExperimentSettings { Mode = "harness", EnabledTools = enabled };
            return new("conversation-a", new("turn-a", "conversation-a", 1, "current input", "", "running", DateTimeOffset.UtcNow,
                null, 1, settings, "transcript-only"), [], Workspace, _ => Task.CompletedTask, _ => Task.CompletedTask);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

extern alias AgenticaLab;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Tools;
using LabChatToolIds = AgenticaLab::ChatToolIds;
using LabWorkspaceFileReadTool = AgenticaLab::WorkspaceFileReadTool;
using LabWorkspaceFileSearchTool = AgenticaLab::WorkspaceFileSearchTool;
using LabWorkspacePathBoundary = AgenticaLab::WorkspacePathBoundary;
using LabWorkspaceSearchProcessSpec = AgenticaLab::WorkspaceSearchProcessSpec;
using LabWorkspaceSearchResourceLimits = AgenticaLab::WorkspaceSearchResourceLimits;

namespace Agentica.Tests;

public sealed class ChatWorkspaceResourceLimitTests
{
    [Fact]
    public async Task File_read_refuses_nul_containing_binary_without_returning_content()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "nul-binary-secret";
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.WorkspaceRoot, "binary.dat"),
            Encoding.UTF8.GetBytes($"{new string('x', 5000)}\0{secret}"));
        var tool = new LabWorkspaceFileReadTool(fixture.WorkspaceRoot);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileRead,
                new Dictionary<string, object?>
                {
                    ["path"] = "binary.dat",
                    ["maxChars"] = 100
                }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.binary",
            "nul_byte",
            secret);
    }

    [Fact]
    public async Task File_read_refuses_invalid_utf8_without_returning_content()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "invalid-utf8-secret";
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.WorkspaceRoot, "invalid.txt"),
            [.. Encoding.UTF8.GetBytes(new string('x', 5000)), 0xff, .. Encoding.UTF8.GetBytes(secret)]);
        var tool = new LabWorkspaceFileReadTool(fixture.WorkspaceRoot);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileRead,
                new Dictionary<string, object?>
                {
                    ["path"] = "invalid.txt",
                    ["maxChars"] = 100
                }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.invalid_utf8",
            "invalid_utf8",
            secret);
    }

    [Fact]
    public async Task Fallback_search_refuses_nul_containing_binary_without_returning_matches()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "fallback-nul-secret";
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.WorkspaceRoot, "binary.dat"),
            Encoding.UTF8.GetBytes($"needle\n{new string('x', 5000)}\0{secret}"));
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            MissingProcess(fixture),
            TestLimits());

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = "needle",
                    ["maxResults"] = 1
                }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.binary",
            "nul_byte",
            secret);
    }

    [Fact]
    public async Task Fallback_search_refuses_invalid_utf8_without_returning_matches()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "fallback-invalid-secret";
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.WorkspaceRoot, "invalid.txt"),
            [.. Encoding.UTF8.GetBytes($"needle\n{new string('x', 5000)}"), 0xff, .. Encoding.UTF8.GetBytes(secret)]);
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            MissingProcess(fixture),
            TestLimits());

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = "needle",
                    ["maxResults"] = 1
                }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.invalid_utf8",
            "invalid_utf8",
            secret);
    }

    [Fact]
    public async Task Default_search_refuses_nul_in_matching_process_output()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "default-nul-secret";
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.WorkspaceRoot, "binary.dat"),
            Encoding.UTF8.GetBytes($"needle\0{secret}\n"));
        var tool = new LabWorkspaceFileSearchTool(fixture.WorkspaceRoot);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.binary",
            "nul_byte",
            secret);
    }

    [Fact]
    public async Task Default_search_refuses_invalid_utf8_in_matching_process_output()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "default-invalid-secret";
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.WorkspaceRoot, "invalid.txt"),
            [.. Encoding.UTF8.GetBytes("needle"), 0xff, .. Encoding.UTF8.GetBytes(secret), (byte)'\n']);
        var tool = new LabWorkspaceFileSearchTool(fixture.WorkspaceRoot);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.invalid_utf8",
            "invalid_utf8",
            secret);
    }

    [Fact]
    public async Task Process_search_refuses_invalid_utf8_stdout_without_fallback()
    {
        using var fixture = new WorkspaceFixture();
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            InvalidUtf8OutputProcess(),
            TestLimits());

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            CancellationToken.None);

        AssertTextResourceRefusal(
            result,
            "workspace.resource.invalid_utf8",
            "invalid_utf8",
            "not-present-in-proof");
    }

    [Fact]
    public async Task File_read_streams_only_a_bounded_prefix()
    {
        using var fixture = new WorkspaceFixture();
        var path = Path.Combine(fixture.WorkspaceRoot, "large.txt");
        await File.WriteAllTextAsync(path, new string('x', 1024 * 1024));
        var tool = new LabWorkspaceFileReadTool(fixture.WorkspaceRoot);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileRead,
                new Dictionary<string, object?>
                {
                    ["path"] = "large.txt",
                    ["maxChars"] = 100
                }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(100, Assert.IsType<string>(result.Receipt.Data["content"]).Length);
        Assert.True(Assert.IsType<bool>(result.Receipt.Data["truncated"]));
        Assert.Equal("character_limit", result.Receipt.Data["limitReason"]);
        Assert.InRange(Convert.ToInt64(result.Receipt.Data["bytesRead"]), 1, 256 * 1024);
        Assert.True(Convert.ToInt64(result.Receipt.Data["bytesRead"]) < new FileInfo(path).Length);
    }

    [Fact]
    public async Task Fallback_search_bounds_each_file_and_total_bytes()
    {
        using var fixture = new WorkspaceFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.WorkspaceRoot, "large.txt"),
            "needle-first\n" + new string('x', 32 * 1024));
        var limits = TestLimits() with
        {
            MaxFallbackFileBytes = 128,
            MaxFallbackTotalBytes = 128,
            MaxSearchLineChars = 64
        };
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            MissingProcess(fixture),
            limits);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = "needle-first",
                    ["maxResults"] = 10
                }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.True(Assert.IsType<bool>(result.Receipt.Data["usedFallback"]));
        Assert.True(Assert.IsType<bool>(result.Receipt.Data["truncated"]));
        Assert.Equal("file_bytes", result.Receipt.Data["limitReason"]);
        Assert.InRange(Convert.ToInt64(result.Receipt.Data["bytesRead"]), 1, 128);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Receipt.Data["matches"]));
    }

    [Fact]
    public async Task Search_refuses_when_bounded_preflight_cannot_cover_the_tree()
    {
        using var fixture = new WorkspaceFixture();
        for (var index = 0; index < 6; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, $"file-{index}.txt"), "needle");
        }

        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            MissingProcess(fixture),
            TestLimits() with { MaxTraversalEntries = 2 });

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Contains("bounded preflight traversal", result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_kills_process_tree_when_result_limit_is_reached()
    {
        using var fixture = new WorkspaceFixture();
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            OutputFloodProcess(),
            TestLimits());
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = "needle",
                    ["maxResults"] = 3
                }),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.False(Assert.IsType<bool>(result.Receipt.Data["usedFallback"]));
        Assert.True(Assert.IsType<bool>(result.Receipt.Data["truncated"]));
        Assert.Equal("result_count", result.Receipt.Data["limitReason"]);
        Assert.Equal(3, Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Receipt.Data["matches"]).Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Bounded search took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Search_drains_bounded_stderr_and_falls_back_without_deadlock()
    {
        using var fixture = new WorkspaceFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "note.txt"), "fallback-needle");
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            ErrorFloodProcess(),
            TestLimits() with { MaxSearchErrorChars = 64 });
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "fallback-needle" }),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.True(Assert.IsType<bool>(result.Receipt.Data["usedFallback"]));
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Receipt.Data["matches"]));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Bounded fallback took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Search_observes_reader_drains_and_never_falls_back_when_termination_is_unconfirmed()
    {
        using var fixture = new WorkspaceFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.WorkspaceRoot, "note.txt"), "needle");
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            OutputFloodWithTerminationFailureProcess(),
            TestLimits());
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = "needle",
                    ["maxResults"] = 1
                }),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("workspace.search.process_termination_unconfirmed", result.Receipt.Data["code"]);
        Assert.Equal("process_termination_unconfirmed", result.Receipt.Data["reason"]);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Termination refusal took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Search_cancellation_kills_the_process_tree_promptly()
    {
        using var fixture = new WorkspaceFixture();
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            SleepingProcess(),
            TestLimits());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            cancellation.Token));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Search_duration_limit_kills_a_silent_process_without_caller_cancellation()
    {
        using var fixture = new WorkspaceFixture();
        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            SleepingProcess(),
            TestLimits() with { MaxSearchDuration = TimeSpan.FromMilliseconds(250) });
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("workspace.search.duration", result.Receipt.Data["code"]);
        Assert.Equal("search_duration", result.Receipt.Data["reason"]);
        Assert.Equal("workspace_search", result.Receipt.Data["resource"]);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Duration limit took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Owned_search_duration_also_bounds_missing_process_fallback_path()
    {
        using var fixture = new WorkspaceFixture();
        for (var index = 0; index < 200; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.WorkspaceRoot, $"file-{index:D3}.txt"),
                new string('x', 2048));
        }

        var tool = new LabWorkspaceFileSearchTool(
            fixture.WorkspaceRoot,
            MissingProcess(fixture),
            TestLimits() with
            {
                MaxTraversalEntries = 1000,
                MaxTraversalFiles = 1000,
                MaxSearchDuration = TimeSpan.FromMilliseconds(1)
            });

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?> { ["pattern"] = "needle" }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal("workspace.search.duration", result.Receipt.Data["code"]);
        Assert.Equal("search_duration", result.Receipt.Data["reason"]);
    }

    [Fact]
    public void Boundary_contract_is_static_preflight_not_adversarial_confinement()
    {
        Assert.Contains("static links", LabWorkspacePathBoundary.SecurityModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not OS-handle-relative confinement", LabWorkspacePathBoundary.SecurityModel, StringComparison.Ordinal);
        Assert.Contains("TOCTOU", LabWorkspacePathBoundary.SecurityModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Boundary_reports_owned_creation_when_post_create_validation_fails()
    {
        using var fixture = new WorkspaceFixture();
        var boundary = new LabWorkspacePathBoundary(
            fixture.WorkspaceRoot,
            createdPath =>
            {
                Directory.Delete(createdPath);
                File.WriteAllText(createdPath, "hostile replacement");
            });

        var prepared = boundary.TryPrepareDirectory(
            "images",
            out _,
            out var createdDirectories,
            out var error);

        Assert.False(prepared);
        Assert.Single(createdDirectories);
        Assert.Equal(
            Path.Combine(fixture.WorkspaceRoot, "images"),
            createdDirectories[0]);
        Assert.True(File.Exists(createdDirectories[0]));
        Assert.Contains("expected workspace directory", error, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolInvocation Invocation(
        string toolId,
        IReadOnlyDictionary<string, object?> input) =>
        new("run_resource_test", "step_resource_test", toolId, input);

    private static LabWorkspaceSearchResourceLimits TestLimits() =>
        LabWorkspaceSearchResourceLimits.Default with
        {
            MaxTraversalEntries = 100,
            MaxTraversalFiles = 100,
            MaxFallbackTotalBytes = 64 * 1024,
            MaxFallbackFileBytes = 16 * 1024,
            MaxSearchOutputChars = 8 * 1024,
            MaxSearchLineChars = 1024,
            MaxSearchErrorChars = 1024,
            MaxSearchDuration = TimeSpan.FromSeconds(5),
            ProcessTerminationGrace = TimeSpan.FromSeconds(5)
        };

    private static void AssertTextResourceRefusal(
        ToolResult result,
        string expectedCode,
        string expectedReason,
        string secret)
    {
        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(expectedCode, result.Receipt.Data["code"]);
        Assert.Equal(expectedReason, result.Receipt.Data["reason"]);
        Assert.Equal("workspace_file", result.Receipt.Data["resource"]);
        Assert.False(result.Receipt.Data.ContainsKey("content"));
        Assert.False(result.Receipt.Data.ContainsKey("matches"));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result.Receipt.Data), StringComparison.Ordinal);
    }

    private static LabWorkspaceSearchProcessSpec MissingProcess(WorkspaceFixture fixture) =>
        new(Path.Combine(fixture.Root, "missing-ripgrep"), [], AppendRipgrepArguments: false);

    private static LabWorkspaceSearchProcessSpec OutputFloodProcess() =>
        OperatingSystem.IsWindows()
            ? PowerShell(
                "$i=0; while($i -lt 10000) { [Console]::Out.WriteLine(('fake:{0}:1:needle' -f $i)); $i++ }; Start-Sleep -Seconds 30")
            : Shell(
                "i=0; while [ \"$i\" -lt 10000 ]; do printf 'fake:%s:1:needle\\n' \"$i\"; i=$((i+1)); done; sleep 30");

    private static LabWorkspaceSearchProcessSpec ErrorFloodProcess() =>
        OperatingSystem.IsWindows()
            ? PowerShell("[Console]::Error.Write(('x' * 4096)); Start-Sleep -Seconds 30")
            : Shell("head -c 4096 /dev/zero | tr '\\0' x >&2; sleep 30");

    private static LabWorkspaceSearchProcessSpec OutputFloodWithTerminationFailureProcess()
    {
        static Task TerminateThenFail(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return Task.FromException(new IOException("forced termination confirmation failure"));
        }

        return OperatingSystem.IsWindows()
            ? PowerShell(
                "[Console]::Out.WriteLine('fake:1:1:needle'); Start-Sleep -Seconds 30",
                TerminateThenFail)
            : Shell("printf 'fake:1:1:needle\\n'; sleep 30", TerminateThenFail);
    }

    private static LabWorkspaceSearchProcessSpec SleepingProcess() =>
        OperatingSystem.IsWindows()
            ? PowerShell("Start-Sleep -Seconds 30")
            : Shell("sleep 30");

    private static LabWorkspaceSearchProcessSpec InvalidUtf8OutputProcess() =>
        OperatingSystem.IsWindows()
            ? PowerShell(
                "$b=[byte[]](0x66,0x61,0x6b,0x65,0x3a,0x31,0x3a,0x31,0x3a,0x6e,0x65,0x65,0x64,0x6c,0x65,0xff,0x0a); $s=[Console]::OpenStandardOutput(); $s.Write($b,0,$b.Length); $s.Flush()")
            : Shell("printf 'fake:1:1:needle\\377\\n'");

    private static LabWorkspaceSearchProcessSpec PowerShell(
        string script,
        Func<Process, Task>? terminationOverride = null) =>
        new(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            AppendRipgrepArguments: false,
            TerminationOverride: terminationOverride);

    private static LabWorkspaceSearchProcessSpec Shell(
        string script,
        Func<Process, Task>? terminationOverride = null) =>
        new(
            "/bin/sh",
            ["-c", script],
            AppendRipgrepArguments: false,
            TerminationOverride: terminationOverride);

    private sealed class WorkspaceFixture : IDisposable
    {
        public WorkspaceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"agentica-resource-{Guid.NewGuid():N}");
            WorkspaceRoot = Path.Combine(Root, "workspace");
            Directory.CreateDirectory(WorkspaceRoot);
        }

        public string Root { get; }

        public string WorkspaceRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

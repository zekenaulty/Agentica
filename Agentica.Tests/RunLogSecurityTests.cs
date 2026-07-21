extern alias AgenticaLab;

using System.Text;
using System.Text.Json;
using Agentica.Events;
using LabRunLogEventSink = AgenticaLab::Agentica.Lab.Logging.RunLogEventSink;
using LabRunLogOptions = AgenticaLab::Agentica.Lab.Logging.RunLogOptions;
using LabRunLogWriter = AgenticaLab::Agentica.Lab.Logging.RunLogWriter;

namespace Agentica.Tests;

public sealed class RunLogSecurityTests
{
    [Fact]
    public void Metadata_projects_option_names_without_persisting_arguments_or_command_line()
    {
        using var workspace = new TemporaryDirectory();
        var writer = CreateWriter(workspace.Path);

        writer.WriteMetadata(
            "quest",
            [
                "--api-key",
                "top-secret-api-value",
                "--model=gemini-safe-model",
                "objective containing private words",
                "--authorization=Bearer abcdefghijklmnop",
                "--token-this-entire-argument-is-private"
            ]);

        var json = File.ReadAllText(Path.Combine(writer.DirectoryPath, "metadata.json"));
        Assert.DoesNotContain("top-secret-api-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("gemini-safe-model", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private words", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", json, StringComparison.Ordinal);
        Assert.DoesNotContain("this-entire-argument-is-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("commandLine", json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var metadata = document.RootElement;
        Assert.Equal("agentica.lab.run-metadata.v2", metadata.GetProperty("schemaVersion").GetString());
        Assert.Equal(6, metadata.GetProperty("argumentCount").GetInt32());
        Assert.Equal(
            ["--model"],
            metadata.GetProperty("optionNames").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Records_recursively_redact_sensitive_keys_header_names_and_obvious_secret_values()
    {
        using var workspace = new TemporaryDirectory();
        var writer = CreateWriter(workspace.Path);

        writer.WriteJson("redaction.json", new
        {
            Configuration = new
            {
                ApiKey = "nested-api-secret",
                Settings = new Dictionary<string, object?>
                {
                    ["access_token"] = "nested-token-secret",
                    ["safe"] = "visible-value"
                }
            },
            Headers = new[]
            {
                new { Name = "Authorization", Value = "otherwise-unremarkable-secret" },
                new { Name = "Accept", Value = "application/json" }
            },
            NeutralField = "Bearer abcdefghijklmnop",
            AnotherNeutralField = "sk-abcdefghijklmnopqrstuvwxyz" // trufflehog:ignore - deterministic redaction fixture
        });

        var json = File.ReadAllText(Path.Combine(writer.DirectoryPath, "redaction.json"));
        Assert.DoesNotContain("nested-api-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-token-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("otherwise-unremarkable-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", json, StringComparison.Ordinal); // trufflehog:ignore
        Assert.Contains("visible-value", json, StringComparison.Ordinal);
        Assert.Contains("application/json", json, StringComparison.Ordinal);
        Assert.True(CountOccurrences(json, "[REDACTED]") >= 5);
        JsonDocument.Parse(json).Dispose();
    }

    [Fact]
    public void Root_scalar_secret_value_is_redacted_without_disabling_writer()
    {
        using var workspace = new TemporaryDirectory();
        var writer = CreateWriter(workspace.Path);

        writer.WriteJson("scalar.json", "Bearer abcdefghijklmnop");

        Assert.True(writer.IsEnabled);
        Assert.Equal("[REDACTED]", JsonSerializer.Deserialize<string>(
            File.ReadAllText(Path.Combine(writer.DirectoryPath, "scalar.json"))));
    }

    [Fact]
    public void Oversized_record_is_replaced_by_bounded_valid_json_with_redacted_digest()
    {
        using var workspace = new TemporaryDirectory();
        var writer = CreateWriter(
            workspace.Path,
            TestOptions() with { MaxRecordBytes = 512 });

        writer.WriteJson("large.json", new
        {
            Type = "proof-event",
            RunId = "run-safe",
            ApiKey = "must-not-survive",
            Evidence = new string('x', 8_000)
        });

        var path = Path.Combine(writer.DirectoryPath, "large.json");
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length <= 512);
        using var document = JsonDocument.Parse(bytes);
        Assert.True(document.RootElement.GetProperty("logRecordTruncated").GetBoolean());
        Assert.Equal("run-safe", document.RootElement.GetProperty("summary").GetProperty("runId").GetString());
        Assert.StartsWith(
            "sha256:",
            document.RootElement.GetProperty("redactedSha256").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-survive", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Per_run_byte_and_file_limits_circuit_break_before_storage_exceeds_bounds()
    {
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var options = TestOptions() with
        {
            MaxRecordBytes = 512,
            MaxRunBytes = 600,
            MaxFilesPerRun = 3,
            MaxStoredBytes = 4_096
        };
        var writer = LabRunLogWriter.Create(workspace.Path, "quota", options, warnings.Add);

        for (var index = 0; index < 10 && writer.IsEnabled; index++)
        {
            writer.WriteJson($"record-{index}.json", new { Evidence = new string('x', 450), Index = index });
        }

        Assert.False(writer.IsEnabled);
        Assert.Single(warnings);
        Assert.True(Directory.EnumerateFiles(writer.DirectoryPath).Count() <= options.MaxFilesPerRun);
        Assert.True(Directory.EnumerateFiles(writer.DirectoryPath).Sum(path => new FileInfo(path).Length) <= options.MaxRunBytes);
    }

    [Fact]
    public void Retention_removes_expired_and_oldest_owned_run_directories()
    {
        using var workspace = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var options = TestOptions() with
        {
            MaxRunDirectories = 2,
            RetentionDays = 1
        };

        var expired = LabRunLogWriter.Create(
            workspace.Path,
            "expired",
            options,
            utcNow: () => now.AddDays(-2));
        Assert.True(expired.IsEnabled);

        var firstCurrent = LabRunLogWriter.Create(
            workspace.Path,
            "first",
            options,
            utcNow: () => now.AddMinutes(-1));
        Assert.False(Directory.Exists(expired.DirectoryPath));

        var secondCurrent = LabRunLogWriter.Create(
            workspace.Path,
            "second",
            options,
            utcNow: () => now.AddMinutes(-0.5));
        var thirdCurrent = LabRunLogWriter.Create(
            workspace.Path,
            "third",
            options,
            utcNow: () => now);

        Assert.False(Directory.Exists(firstCurrent.DirectoryPath));
        Assert.True(Directory.Exists(secondCurrent.DirectoryPath));
        Assert.True(Directory.Exists(thirdCurrent.DirectoryPath));
        Assert.Equal(2, Directory.EnumerateDirectories(workspace.Path).Count());
    }

    [Fact]
    public void Root_quota_prunes_old_owned_run_before_accepting_a_new_record()
    {
        using var workspace = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var options = TestOptions() with
        {
            MaxRecordBytes = 1_024,
            MaxRunBytes = 1_200,
            MaxStoredBytes = 2_000,
            MaxRunDirectories = 10
        };

        var oldest = LabRunLogWriter.Create(
            workspace.Path,
            "oldest",
            options,
            utcNow: () => now.AddMinutes(-2));
        oldest.WriteJson("evidence.json", new { Evidence = new string('a', 800) });
        var middle = LabRunLogWriter.Create(
            workspace.Path,
            "middle",
            options,
            utcNow: () => now.AddMinutes(-1));
        middle.WriteJson("evidence.json", new { Evidence = new string('b', 800) });
        var newest = LabRunLogWriter.Create(
            workspace.Path,
            "newest",
            options,
            utcNow: () => now);

        newest.WriteJson("evidence.json", new { Evidence = new string('c', 800) });

        Assert.True(newest.IsEnabled);
        Assert.False(Directory.Exists(oldest.DirectoryPath));
        Assert.True(Directory.Exists(middle.DirectoryPath));
        Assert.True(Directory.Exists(newest.DirectoryPath));
        Assert.True(Directory.EnumerateFiles(workspace.Path, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) <= options.MaxStoredBytes);
    }

    [Fact]
    public void Traversal_file_name_fails_closed_without_writing_outside_run_directory()
    {
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var writer = LabRunLogWriter.Create(workspace.Path, "paths", TestOptions(), warnings.Add);
        var outsidePath = Path.Combine(workspace.Path, "outside.json");

        writer.WriteJson("../outside.json", new { Value = "should-not-write" });

        Assert.False(writer.IsEnabled);
        Assert.False(File.Exists(outsidePath));
        Assert.Equal([LabRunLogWriter.SafeFailureWarning], warnings);
    }

    [Fact]
    public void Reparse_root_is_refused_when_platform_supports_directory_links()
    {
        using var workspace = new TemporaryDirectory();
        var actual = Path.Combine(workspace.Path, "actual");
        var link = Path.Combine(workspace.Path, "linked-root");
        Directory.CreateDirectory(actual);

        try
        {
            Directory.CreateSymbolicLink(link, actual);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var warnings = new List<string>();
        var writer = LabRunLogWriter.Create(link, "linked", TestOptions(), warnings.Add);

        Assert.False(writer.IsEnabled);
        Assert.Equal([LabRunLogWriter.SafeFailureWarning], warnings);
        Directory.Delete(link);
    }

    [Fact]
    public void Reparse_file_target_is_refused_without_modifying_link_destination()
    {
        using var workspace = new TemporaryDirectory();
        var writer = CreateWriter(workspace.Path);
        var destination = Path.Combine(workspace.Path, "destination.json");
        var link = Path.Combine(writer.DirectoryPath, "linked.json");
        File.WriteAllText(destination, "unchanged");

        try
        {
            File.CreateSymbolicLink(link, destination);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        writer.WriteJson("linked.json", new { Value = "must-not-cross-link" });

        Assert.False(writer.IsEnabled);
        Assert.Equal("unchanged", File.ReadAllText(destination));
        File.Delete(link);
    }

    [Fact]
    public void Sink_write_failure_is_contained_and_authoritative_sink_still_receives_event()
    {
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var writer = LabRunLogWriter.Create(workspace.Path, "failure", TestOptions(), warnings.Add);
        Directory.Delete(writer.DirectoryPath, recursive: true);
        File.WriteAllText(writer.DirectoryPath, "blocks-directory-recreation");

        var inner = new InMemoryEventSink();
        var sink = new LabRunLogEventSink(inner, writer);
        var executionEvent = new ExecutionEvent(
            "evt-safe",
            "test.event",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

        var exception = Record.Exception(() => sink.Emit(executionEvent));

        Assert.Null(exception);
        Assert.Equal(executionEvent, Assert.Single(inner.Events));
        Assert.False(writer.IsEnabled);
        Assert.Equal([LabRunLogWriter.SafeFailureWarning], warnings);
        Assert.DoesNotContain(workspace.Path, warnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blocks-directory", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    private static LabRunLogWriter CreateWriter(
        string baseDirectory,
        LabRunLogOptions? options = null)
    {
        var writer = LabRunLogWriter.Create(baseDirectory, "security-test", options ?? TestOptions());
        Assert.True(writer.IsEnabled);
        return writer;
    }

    private static LabRunLogOptions TestOptions() => new()
    {
        MaxRecordBytes = 2_048,
        MaxRunBytes = 64 * 1024,
        MaxFilesPerRun = 16,
        MaxRunDirectories = 8,
        MaxStoredBytes = 512 * 1024,
        RetentionDays = 7
    };

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agentica-run-log-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
                return;
            }

            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

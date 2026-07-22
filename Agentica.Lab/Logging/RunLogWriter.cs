using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Lab.Scenarios.MazeQuest;
using Agentica.Events;
using Agentica.Outcomes;

namespace Agentica.Lab.Logging;

public sealed class RunLogWriter
{
    internal const string SafeFailureWarning =
        "Run logging was disabled after a safe storage failure.";

    private const string OwnershipMarkerName = ".agentica-run-log";
    private const string OwnershipMarkerContent = "agentica-lab-run-log-v1\n";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions JsonLineOptions = CreateJsonOptions(writeIndented: false);
    private static readonly HashSet<string> AllowedMetadataOptionNames = new(
        [
            "--include-thoughts",
            "--log-dir",
            "--log-run",
            "--max-blocked-retries",
            "--max-graph-mutations",
            "--max-orchestration-refinements",
            "--max-orchestration-runs",
            "--max-output-tokens",
            "--max-plan-continuations",
            "--max-refinements",
            "--max-steps",
            "--model",
            "--opponent",
            "--opponent-difficulty",
            "--opponent-model",
            "--opponent-planner",
            "--opponent-seed",
            "--opponent-thinking-budget",
            "--phase",
            "--planner",
            "--planning-mode",
            "--quiet",
            "--run-model",
            "--run-planner",
            "--strategy-mode",
            "--task-model",
            "--task-planner",
            "--thinking-budget",
            "--timeout-seconds",
            "--turn-json",
            "--verbose-envelope",
            "--verbose-events"
        ],
        StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string _rootPath;
    private readonly RunLogOptions _options;
    private readonly Action<string>? _warning;
    private bool _enabled;
    private bool _warningIssued;

    private RunLogWriter(
        string rootPath,
        string directoryPath,
        RunLogOptions options,
        Action<string>? warning,
        bool enabled,
        bool warningIssued = false)
    {
        _rootPath = rootPath;
        DirectoryPath = directoryPath;
        _options = options;
        _warning = warning;
        _enabled = enabled;
        _warningIssued = warningIssued;
    }

    public string DirectoryPath { get; }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public static RunLogWriter Create(
        string? baseDirectory,
        string scenario,
        RunLogOptions? options = null,
        Action<string>? warning = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var effectiveOptions = options ?? RunLogOptions.Default;
        try
        {
            ValidateOptions(effectiveOptions);
            var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
            var root = RunLogPathGuard.ResolveRoot(baseDirectory);
            ApplyRetention(
                root,
                effectiveOptions,
                now,
                directoryToKeep: null,
                reserveBytes: Encoding.UTF8.GetByteCount(OwnershipMarkerContent));

            var stamp = now.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            var safeScenario = SanitizeScenario(scenario);
            var directoryName = $"{stamp}_{safeScenario}_{Guid.NewGuid().ToString("N")[..8]}";
            var directory = RunLogPathGuard.ResolveRunDirectory(root, directoryName);
            Directory.CreateDirectory(directory);
            RunLogPathGuard.EnsurePlainDirectoryTree(root, directory);

            var markerPath = RunLogPathGuard.ResolveFile(root, directory, OwnershipMarkerName);
            using (var marker = new FileStream(
                       markerPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.None))
            {
                marker.Write(Encoding.UTF8.GetBytes(OwnershipMarkerContent));
            }

            var writer = new RunLogWriter(root, directory, effectiveOptions, warning, enabled: true);
            writer.ValidateStorageBounds();
            return writer;
        }
        catch (Exception)
        {
            WarnSafely(warning);
            return new RunLogWriter(
                string.Empty,
                string.Empty,
                effectiveOptions,
                warning,
                enabled: false,
                warningIssued: true);
        }
    }

    public void WriteMetadata(string scenario, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var optionNames = args
            .Select(TryProjectOptionName)
            .Where(name => name is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        WriteJson("metadata.json", new
        {
            schemaVersion = "agentica.lab.run-metadata.v2",
            scenario = SanitizeScenario(scenario),
            startedAt = DateTimeOffset.UtcNow,
            argumentCount = args.Count,
            optionNames
        });
    }

    public void WriteEvent(ExecutionEvent executionEvent) =>
        AppendJsonLine("events.jsonl", executionEvent);

    public void WriteTurn(MazeQuestTurnEnvelope turnEnvelope) =>
        AppendJsonLine("turns.jsonl", turnEnvelope);

    public void WriteJsonLine(string fileName, object value) =>
        AppendJsonLine(fileName, value);

    public void WriteOutcome(OutcomeEnvelope envelope) =>
        WriteJson("outcome.json", envelope);

    public void WriteJson(string fileName, object value)
    {
        TryWrite(fileName, value, JsonOptions, append: false);
    }

    private void AppendJsonLine(string fileName, object value)
    {
        TryWrite(fileName, value, JsonLineOptions, append: true);
    }

    private void TryWrite(
        string fileName,
        object value,
        JsonSerializerOptions serializerOptions,
        bool append)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                var path = RunLogPathGuard.ResolveFile(_rootPath, DirectoryPath, fileName);
                var payload = RunLogRedactor.Serialize(
                    value,
                    serializerOptions,
                    _options.MaxRecordBytes,
                    appendNewLine: true);
                var bytes = Encoding.UTF8.GetBytes(payload);

                EnsureWriteFits(path, bytes.LongLength, append);
                using var stream = new FileStream(
                    path,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.None);
                stream.Write(bytes);
            }
            catch (Exception)
            {
                DisableAndWarn();
            }
        }
    }

    private void EnsureWriteFits(string path, long payloadBytes, bool append)
    {
        ValidateStorageBounds();

        var files = Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Select(file => new FileInfo(file))
            .ToArray();
        var existing = files.FirstOrDefault(file =>
            string.Equals(file.FullName, path, PathComparison));
        if (existing is null && files.Length >= _options.MaxFilesPerRun)
        {
            throw new IOException("The run-log file-count limit was reached.");
        }

        var currentRunBytes = files.Sum(file => file.Length);
        var replacedBytes = append ? 0 : existing?.Length ?? 0;
        var prospectiveRunBytes = checked(currentRunBytes - replacedBytes + payloadBytes);
        if (prospectiveRunBytes > _options.MaxRunBytes)
        {
            throw new IOException("The run-log directory byte limit was reached.");
        }

        var rootBytes = CalculateTreeBytes(_rootPath);
        var prospectiveRootBytes = checked(rootBytes - replacedBytes + payloadBytes);
        if (prospectiveRootBytes > _options.MaxStoredBytes)
        {
            ApplyRetention(
                _rootPath,
                _options,
                DateTimeOffset.UtcNow,
                directoryToKeep: DirectoryPath,
                reserveBytes: Math.Max(0, payloadBytes - replacedBytes));
            rootBytes = CalculateTreeBytes(_rootPath);
            prospectiveRootBytes = checked(rootBytes - replacedBytes + payloadBytes);
            if (prospectiveRootBytes > _options.MaxStoredBytes)
            {
                throw new IOException("The run-log root byte limit was reached.");
            }
        }
    }

    private void ValidateStorageBounds()
    {
        RunLogPathGuard.EnsurePlainDirectoryTree(_rootPath, DirectoryPath);
        if (Directory.EnumerateDirectories(DirectoryPath, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new IOException("Run-log directories cannot contain nested directories.");
        }

        var files = Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Select(file => new FileInfo(file))
            .ToArray();
        if (files.Length > _options.MaxFilesPerRun ||
            files.Sum(file => file.Length) > _options.MaxRunBytes ||
            CalculateTreeBytes(_rootPath) > _options.MaxStoredBytes)
        {
            throw new IOException("Run-log storage is outside its configured bounds.");
        }
    }

    private void DisableAndWarn()
    {
        _enabled = false;
        if (_warningIssued)
        {
            return;
        }

        _warningIssued = true;
        WarnSafely(_warning);
    }

    private static void ApplyRetention(
        string root,
        RunLogOptions options,
        DateTimeOffset now,
        string? directoryToKeep,
        long reserveBytes)
    {
        RunLogPathGuard.EnsurePlainDirectoryTree(root, root);
        var candidates = GetOwnedRunDirectories(root)
            .Where(candidate => directoryToKeep is null ||
                !string.Equals(candidate.Path, directoryToKeep, PathComparison))
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ToList();

        var cutoff = now.ToUniversalTime().AddDays(-options.RetentionDays);
        foreach (var expired in candidates.Where(candidate => candidate.CreatedAt < cutoff).ToArray())
        {
            DeleteOwnedDirectory(root, expired.Path);
            candidates.Remove(expired);
        }

        Func<bool> directoryLimitReached = directoryToKeep is null
            ? () => candidates.Count >= options.MaxRunDirectories
            : () => candidates.Count + 1 > options.MaxRunDirectories;
        while (directoryLimitReached() && candidates.Count > 0)
        {
            DeleteOwnedDirectory(root, candidates[0].Path);
            candidates.RemoveAt(0);
        }

        var targetStoredBytes = checked(options.MaxStoredBytes - reserveBytes);
        while (CalculateTreeBytes(root) > targetStoredBytes && candidates.Count > 0)
        {
            DeleteOwnedDirectory(root, candidates[0].Path);
            candidates.RemoveAt(0);
        }

        if (CalculateTreeBytes(root) > targetStoredBytes)
        {
            throw new IOException("The run-log root quota cannot be satisfied.");
        }
    }

    private static List<OwnedRunDirectory> GetOwnedRunDirectories(string root)
    {
        var result = new List<OwnedRunDirectory>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!TryParseRunDirectoryTimestamp(name, out var createdAt))
            {
                continue;
            }

            RunLogPathGuard.EnsurePlainDirectoryTree(root, directory);
            var marker = RunLogPathGuard.ResolveFile(root, directory, OwnershipMarkerName);
            if (!File.Exists(marker))
            {
                continue;
            }

            var markerInfo = new FileInfo(marker);
            if (markerInfo.Length != Encoding.UTF8.GetByteCount(OwnershipMarkerContent) ||
                !string.Equals(File.ReadAllText(marker), OwnershipMarkerContent, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new OwnedRunDirectory(directory, createdAt));
        }

        return result;
    }

    private static long CalculateTreeBytes(string directory)
    {
        RunLogPathGuard.EnsurePlainDirectoryTree(directory, directory);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                RunLogPathGuard.EnsureContained(directory, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Run-log storage cannot contain reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                total = checked(total + new FileInfo(entry).Length);
            }
        }

        return total;
    }

    private static void DeleteOwnedDirectory(string root, string directory)
    {
        RunLogPathGuard.EnsurePlainDirectoryTree(root, directory);
        Directory.Delete(directory, recursive: true);
    }

    private static bool TryParseRunDirectoryTimestamp(string name, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var firstSeparator = name.IndexOf('_');
        var lastSeparator = name.LastIndexOf('_');
        if (firstSeparator != 19 ||
            lastSeparator <= firstSeparator + 1 ||
            name.Length - lastSeparator - 1 != 8 ||
            !name[(lastSeparator + 1)..].All(Uri.IsHexDigit))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            name[..firstSeparator],
            "yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static string SanitizeScenario(string scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return "run";
        }

        var characters = scenario
            .Take(48)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_')
            .ToArray();
        var sanitized = new string(characters).Trim('_');
        return sanitized.Length == 0 ? "run" : sanitized;
    }

    private static string? TryProjectOptionName(string argument)
    {
        if (string.IsNullOrEmpty(argument) || !argument.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var separator = argument.IndexOf('=');
        var name = separator < 0 ? argument : argument[..separator];
        if (name.Length is < 3 or > 66 ||
            !name[2..].All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            return null;
        }

        var normalized = name.ToLowerInvariant();
        return AllowedMetadataOptionNames.Contains(normalized) ? normalized : null;
    }

    private static void ValidateOptions(RunLogOptions options)
    {
        if (options.MaxRecordBytes < 512 ||
            options.MaxRunBytes < options.MaxRecordBytes ||
            options.MaxFilesPerRun < 2 ||
            options.MaxRunDirectories < 1 ||
            options.MaxStoredBytes < options.MaxRunBytes ||
            options.RetentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Run-log limits are invalid.");
        }
    }

    private static void WarnSafely(Action<string>? warning)
    {
        try
        {
            warning?.Invoke(SafeFailureWarning);
        }
        catch (Exception)
        {
            // Logging diagnostics are best effort and must not affect the command outcome.
        }
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MazePointJsonConverter());
        return options;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record OwnedRunDirectory(string Path, DateTimeOffset CreatedAt);

    private sealed class MazePointJsonConverter : JsonConverter<MazePoint>
    {
        public override MazePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException("MazePoint object deserialization is not supported by run logs.");
        }

        public override void Write(Utf8JsonWriter writer, MazePoint value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteEndObject();
        }

        public override MazePoint ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var key = reader.GetString() ?? string.Empty;
            var parts = key.Split(',', 2);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out var x) ||
                !int.TryParse(parts[1], out var y))
            {
                throw new JsonException("Invalid MazePoint property name.");
            }

            return new MazePoint(x, y);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, MazePoint value, JsonSerializerOptions options)
        {
            writer.WritePropertyName($"{value.X},{value.Y}");
        }
    }
}

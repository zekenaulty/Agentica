using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.CLI.Scenarios.MazeQuest;
using Agentica.Events;
using Agentica.Outcomes;

namespace Agentica.CLI.Logging;

public sealed class RunLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions JsonLineOptions = CreateJsonOptions(writeIndented: false);
    private readonly object _gate = new();

    private RunLogWriter(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public static RunLogWriter Create(string? baseDirectory, string scenario)
    {
        var root = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), ".agentica", "runs")
            : Path.GetFullPath(baseDirectory);

        Directory.CreateDirectory(root);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var safeScenario = SanitizeFileName(scenario);
        var directory = Path.Combine(root, $"{stamp}_{safeScenario}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(directory);

        return new RunLogWriter(directory);
    }

    public void WriteMetadata(string scenario, IReadOnlyList<string> args)
    {
        WriteJson("metadata.json", new
        {
            scenario,
            startedAt = DateTimeOffset.UtcNow,
            commandLine = Environment.CommandLine,
            args
        });
    }

    public void WriteEvent(ExecutionEvent executionEvent) =>
        AppendJsonLine("events.jsonl", executionEvent);

    public void WriteTurn(MazeQuestTurnEnvelope turnEnvelope) =>
        AppendJsonLine("turns.jsonl", turnEnvelope);

    public void WriteOutcome(OutcomeEnvelope envelope) =>
        WriteJson("outcome.json", envelope);

    public void WriteJson(string fileName, object value)
    {
        var path = SafePath(fileName);
        var json = JsonSerializer.Serialize(value, JsonOptions);

        lock (_gate)
        {
            File.WriteAllText(path, json + Environment.NewLine);
        }
    }

    private void AppendJsonLine(string fileName, object value)
    {
        var path = SafePath(fileName);
        var json = JsonSerializer.Serialize(value, JsonLineOptions);

        lock (_gate)
        {
            File.AppendAllText(path, json + Environment.NewLine);
        }
    }

    private string SafePath(string fileName)
    {
        var safeName = SanitizeFileName(fileName);
        return Path.Combine(DirectoryPath, safeName);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
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
                throw new JsonException($"Invalid MazePoint key '{key}'.");
            }

            return new MazePoint(x, y);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, MazePoint value, JsonSerializerOptions options)
        {
            writer.WritePropertyName($"{value.X},{value.Y}");
        }
    }
}

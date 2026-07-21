using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.Lab.Benchmarks;

internal sealed class ProductProofBenchmarkStore
{
    private static readonly JsonSerializerOptions IndentedJson = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions JsonLine = CreateJsonOptions(writeIndented: false);
    private readonly object _gate = new();

    private ProductProofBenchmarkStore(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public static ProductProofBenchmarkStore Create(string? baseDirectory, string cohortId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);

        var root = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), ".agentica", "benchmarks")
            : Path.GetFullPath(baseDirectory);
        var safeCohortId = SanitizeFileName(cohortId);
        if (string.IsNullOrWhiteSpace(safeCohortId))
        {
            throw new InvalidOperationException("The benchmark cohort id did not contain a usable file name.");
        }

        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, safeCohortId);
        Directory.CreateDirectory(directory);
        return new ProductProofBenchmarkStore(directory);
    }

    public static ProductProofBenchmarkStore OpenExisting(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Benchmark cohort directory '{directory}' does not exist.");
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The benchmark cohort directory cannot be a symbolic link or junction.");
        }

        return new ProductProofBenchmarkStore(directory);
    }

    public void WriteManifest(object manifest) =>
        WriteJsonAtomically("manifest.json", manifest);

    public void AppendRun(object result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var json = JsonSerializer.Serialize(result, JsonLine);
        var path = Path.Combine(DirectoryPath, "runs.jsonl");

        lock (_gate)
        {
            RequireOrdinaryDirectory();
            RequireOrdinaryDestination(path);
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    public void WriteAggregate(object aggregate) =>
        WriteJsonAtomically("aggregate.json", aggregate);

    public void WriteReaggregationReceipt(object receipt) =>
        WriteJsonAtomically("reaggregation.json", receipt);

    private void WriteJsonAtomically(string fileName, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var destination = Path.Combine(DirectoryPath, fileName);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, IndentedJson) + Environment.NewLine;

        lock (_gate)
        {
            RequireOrdinaryDirectory();
            RequireOrdinaryDestination(destination);
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                RequireOrdinaryDestination(destination);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary) &&
                    (File.GetAttributes(temporary) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private void RequireOrdinaryDirectory()
    {
        if (!Directory.Exists(DirectoryPath) ||
            (File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The benchmark cohort directory is missing or became a symbolic link or junction.");
        }
    }

    private static void RequireOrdinaryDestination(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException($"Benchmark output '{Path.GetFileName(path)}' must be an ordinary file.");
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

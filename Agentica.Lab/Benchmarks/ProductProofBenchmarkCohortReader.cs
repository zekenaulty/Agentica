using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Agentica.Clients.Gemini;

namespace Agentica.Lab.Benchmarks;

internal sealed record StoredProductProofBenchmarkManifest(
    string HarnessVersion,
    DateTimeOffset StartedAtUtc,
    BenchmarkMatrix Matrix,
    BenchmarkCohortIdentity Cohort,
    ProductProofBenchmarkConfiguration Configuration,
    JsonElement Pricing);

internal sealed record ProductProofBenchmarkCohortSnapshot(
    string DirectoryPath,
    StoredProductProofBenchmarkManifest Manifest,
    IReadOnlyList<BenchmarkRunResult> Results,
    string RunsSha256);

internal sealed class ProductProofBenchmarkCohortException : InvalidOperationException
{
    public ProductProofBenchmarkCohortException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class ProductProofBenchmarkCohortReader
{
    private const string LegacyPricingSnapshotId = "gemini-api-standard-pricing-2026-07-21-v1";
    private const int MaximumManifestBytes = 1 * 1024 * 1024;
    private const int MaximumRunsBytes = 64 * 1024 * 1024;
    private const int MaximumRunLineCharacters = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions StrictJson = CreateJsonOptions();

    public static ProductProofBenchmarkCohortSnapshot Read(string cohortDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortDirectory);

        try
        {
            var directory = Path.GetFullPath(cohortDirectory);
            RequireOrdinaryDirectory(directory);
            var manifestPath = RequireOrdinaryFile(directory, "manifest.json");
            var runsPath = RequireOrdinaryFile(directory, "runs.jsonl");
            var manifestBytes = ReadBoundedFile(manifestPath, MaximumManifestBytes);
            var runsBytes = ReadBoundedFile(runsPath, MaximumRunsBytes);

            var manifest = DeserializeManifest(manifestBytes);
            ValidateManifest(manifest);
            var results = DeserializeRuns(runsBytes);
            ValidateResultCohort(results, manifest.Cohort);

            return new ProductProofBenchmarkCohortSnapshot(
                directory,
                manifest,
                Array.AsReadOnly(results),
                $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(runsBytes))}");
        }
        catch (ProductProofBenchmarkCohortException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ProductProofBenchmarkCohortException(
                $"The benchmark cohort could not be read safely ({exception.GetType().Name}).",
                exception);
        }
    }

    private static StoredProductProofBenchmarkManifest DeserializeManifest(byte[] bytes)
    {
        var json = DecodeUtf8(bytes, "manifest.json");
        ValidateJsonObject(json, "manifest.json");
        return JsonSerializer.Deserialize<StoredProductProofBenchmarkManifest>(json, StrictJson)
            ?? throw Invalid("manifest.json did not contain a benchmark manifest.");
    }

    private static BenchmarkRunResult[] DeserializeRuns(byte[] bytes)
    {
        var jsonLines = DecodeUtf8(bytes, "runs.jsonl");
        if (jsonLines.Length > 0 && jsonLines[0] == '\uFEFF')
        {
            jsonLines = jsonLines[1..];
        }

        var lines = new List<string>();
        using (var reader = new StringReader(jsonLines))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    throw Invalid("runs.jsonl contains a blank record.");
                }

                if (line.Length > MaximumRunLineCharacters)
                {
                    throw Invalid("runs.jsonl contains an oversized record.");
                }

                lines.Add(line);
            }
        }

        var expectedCount = ProductProofBenchmarkMatrix.Current.Runs.Count;
        if (lines.Count != expectedCount)
        {
            throw Invalid($"runs.jsonl must contain exactly {expectedCount} records; found {lines.Count}.");
        }

        var results = new BenchmarkRunResult[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            ValidateJsonObject(lines[index], $"runs.jsonl record {index + 1}");
            results[index] = JsonSerializer.Deserialize<BenchmarkRunResult>(lines[index], StrictJson)
                ?? throw Invalid($"runs.jsonl record {index + 1} was empty.");
        }

        return results;
    }

    private static void ValidateManifest(StoredProductProofBenchmarkManifest manifest)
    {
        var currentMatrix = ProductProofBenchmarkMatrix.Current;
        var currentConfiguration = ProductProofBenchmarkFixedConfiguration.Current;
        if (!string.Equals(
                manifest.HarnessVersion,
                ProductProofBenchmarkFixedConfiguration.HarnessVersion,
                StringComparison.Ordinal) ||
            manifest.StartedAtUtc == default ||
            manifest.Matrix is null ||
            manifest.Cohort is null ||
            manifest.Configuration is null)
        {
            throw Invalid("manifest.json does not identify the current fixed benchmark harness.");
        }

        if (!MatrixEquals(manifest.Matrix, currentMatrix))
        {
            throw Invalid("The stored manifest matrix is not exactly the current fixed product-proof matrix.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Configuration.PricingSnapshotId) ||
            !Equals(
                manifest.Configuration with
                {
                    PricingSnapshotId = currentConfiguration.PricingSnapshotId
                },
                currentConfiguration) ||
            !string.Equals(
                manifest.Cohort.MatrixVersion,
                currentMatrix.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Cohort.ProviderName,
                GeminiLlmClient.ProviderName,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Cohort.ModelId,
                GeminiModelId.Flash25,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Cohort.ConfigurationId,
                ProductProofBenchmarkFixedConfiguration.ConfigurationId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Cohort.CohortId))
        {
            throw Invalid("The stored cohort or configuration identity differs from the current fixed benchmark configuration.");
        }

        var storedPricingSnapshotId = ValidateStoredPricing(manifest.Pricing);
        if (!string.Equals(
                storedPricingSnapshotId,
                manifest.Configuration.PricingSnapshotId,
                StringComparison.Ordinal))
        {
            throw Invalid("The original manifest pricing and configuration pricing identities differ.");
        }
    }

    private static void ValidateResultCohort(
        IReadOnlyList<BenchmarkRunResult> results,
        BenchmarkCohortIdentity manifestCohort)
    {
        for (var index = 0; index < results.Count; index++)
        {
            if (results[index].Cohort is null || !Equals(results[index].Cohort, manifestCohort))
            {
                throw Invalid($"runs.jsonl record {index + 1} does not match the original manifest cohort.");
            }
        }
    }

    private static bool MatrixEquals(BenchmarkMatrix left, BenchmarkMatrix right)
    {
        if (!string.Equals(left.Version, right.Version, StringComparison.Ordinal) ||
            left.Cases is null ||
            left.Runs is null ||
            left.Cases.Count != right.Cases.Count ||
            left.Runs.Count != right.Runs.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Cases.Count; index++)
        {
            var leftCase = left.Cases[index];
            var rightCase = right.Cases[index];
            if (leftCase is null ||
                !string.Equals(leftCase.CaseId, rightCase.CaseId, StringComparison.Ordinal) ||
                leftCase.Suite != rightCase.Suite ||
                !string.Equals(leftCase.ScenarioId, rightCase.ScenarioId, StringComparison.Ordinal) ||
                !ParametersEqual(leftCase.Parameters, rightCase.Parameters))
            {
                return false;
            }
        }

        for (var index = 0; index < left.Runs.Count; index++)
        {
            var leftRun = left.Runs[index];
            var rightRun = right.Runs[index];
            if (leftRun is null ||
                !string.Equals(leftRun.RunId, rightRun.RunId, StringComparison.Ordinal) ||
                !string.Equals(leftRun.MatrixVersion, rightRun.MatrixVersion, StringComparison.Ordinal) ||
                !string.Equals(leftRun.CaseId, rightRun.CaseId, StringComparison.Ordinal) ||
                leftRun.Suite != rightRun.Suite ||
                !string.Equals(leftRun.ScenarioId, rightRun.ScenarioId, StringComparison.Ordinal) ||
                leftRun.Repetition != rightRun.Repetition ||
                !ParametersEqual(leftRun.Parameters, rightRun.Parameters))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParametersEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right) =>
        left is not null &&
        right is not null &&
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static string ValidateStoredPricing(JsonElement pricing)
    {
        if (pricing.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("manifest.json pricing is missing or invalid.");
        }

        RequireExactProperties(
            pricing,
            "manifest pricing",
            "snapshotId",
            "reviewedOn",
            "sourceUrl",
            "models");
        var snapshotId = pricing.GetProperty("snapshotId").GetString();
        var isLegacyPricing = string.Equals(snapshotId, LegacyPricingSnapshotId, StringComparison.Ordinal);
        var isCurrentPricing = string.Equals(snapshotId, ProductProofPricing.SnapshotId, StringComparison.Ordinal);
        if ((!isLegacyPricing && !isCurrentPricing) ||
            !DateOnly.TryParseExact(
                pricing.GetProperty("reviewedOn").GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var reviewedOn) ||
            reviewedOn != ProductProofPricing.Current.ReviewedOn ||
            !Uri.TryCreate(pricing.GetProperty("sourceUrl").GetString(), UriKind.Absolute, out var source) ||
            !string.Equals(source.AbsoluteUri, ProductProofPricing.SourceUrl, StringComparison.Ordinal) ||
            pricing.GetProperty("models").ValueKind != JsonValueKind.Array ||
            pricing.GetProperty("models").GetArrayLength() != 1)
        {
            throw Invalid("manifest.json pricing identity is invalid.");
        }

        foreach (var model in pricing.GetProperty("models").EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("manifest.json contains an invalid pricing model.");
            }

            var names = model.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var isV1 = names.SetEquals(
            [
                "modelId",
                "inputUsdPerMillionTokens",
                "outputUsdPerMillionTokens"
            ]);
            var isV2 = names.SetEquals(
            [
                "modelId",
                "uncachedInputUsdPerMillionTokens",
                "cachedInputUsdPerMillionTokens",
                "outputUsdPerMillionTokens"
            ]);
            if ((isLegacyPricing && !isV1) ||
                (isCurrentPricing && !isV2) ||
                !string.Equals(
                    model.GetProperty("modelId").GetString(),
                    GeminiModelId.Flash25,
                    StringComparison.Ordinal))
            {
                throw Invalid("manifest.json contains an unknown pricing model shape.");
            }

            foreach (var property in model.EnumerateObject().Where(item => item.Name != "modelId"))
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetDecimal(out var value) ||
                    value < 0)
                {
                    throw Invalid("manifest.json contains an invalid pricing value.");
                }
            }
        }

        return snapshotId!;
    }

    private static void RequireExactProperties(
        JsonElement element,
        string description,
        params string[] expected)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals(expected))
        {
            throw Invalid($"{description} contains missing or extra properties.");
        }
    }

    private static void ValidateJsonObject(string json, string description)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new ProductProofBenchmarkCohortException($"{description} is invalid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"{description} must contain one JSON object.");
            }

            RequireNoDuplicateProperties(document.RootElement, description);
        }
    }

    private static void RequireNoDuplicateProperties(JsonElement element, string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid($"{description} contains duplicate property '{property.Name}'.");
                }

                RequireNoDuplicateProperties(property.Value, description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RequireNoDuplicateProperties(item, description);
            }
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw Invalid($"'{Path.GetFileName(path)}' is empty or exceeds its safe size limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string DecodeUtf8(byte[] bytes, string description)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProductProofBenchmarkCohortException($"{description} is not valid UTF-8.", exception);
        }
    }

    private static void RequireOrdinaryDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw Invalid($"Cohort directory '{directory}' does not exist.");
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid("The cohort directory cannot be a symbolic link or junction.");
        }
    }

    private static string RequireOrdinaryFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw Invalid($"The cohort is missing required file '{fileName}'.");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw Invalid($"Required cohort file '{fileName}' must be an ordinary file.");
        }

        return path;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static ProductProofBenchmarkCohortException Invalid(string message) =>
        new(message);
}

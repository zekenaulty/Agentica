extern alias AgenticaLab;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Tools;
using BenchmarkOptions = AgenticaLab::Agentica.Lab.Benchmarks.ProductProofBenchmarkCommandOptions;
using BenchmarkCommand = AgenticaLab::Agentica.Lab.Benchmarks.ProductProofBenchmarkCommand;
using BenchmarkOracles = AgenticaLab::Agentica.Lab.Benchmarks.ProductProofBenchmarkOracles;
using BenchmarkStore = AgenticaLab::Agentica.Lab.Benchmarks.ProductProofBenchmarkStore;
using LabBenchmarks = AgenticaLab::Agentica.Lab.Benchmarks;
using WorkbenchQuestBoard = AgenticaLab::Agentica.Lab.Scenarios.WorkbenchQuest.WorkbenchQuestBoard;
using WorkbenchQuestSession = AgenticaLab::Agentica.Lab.Scenarios.WorkbenchQuest.WorkbenchQuestSession;
using WorkbenchQuestToolIds = AgenticaLab::Agentica.Lab.Scenarios.WorkbenchQuest.WorkbenchQuestToolIds;

namespace Agentica.Tests;

public sealed class ProductProofBenchmarkCommandTests
{
    [Fact]
    public void Options_are_narrow_and_require_explicit_live_intent()
    {
        var options = BenchmarkOptions.Parse(
        [
            "--live",
            "--output-dir",
            "proof-output"
        ]);

        Assert.True(options.IsValid);
        Assert.True(options.Live);
        Assert.Equal("proof-output", options.OutputDirectory);

        var unknown = BenchmarkOptions.Parse(["--model", "anything"]);
        Assert.False(unknown.IsValid);
        Assert.Contains("Unknown benchmark option", unknown.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void Vertex_surface_selection_is_detected_without_reading_credentials(
        string? value,
        bool expected)
    {
        Assert.Equal(expected, BenchmarkCommand.IsVertexAiEnabled(value));
    }

    [Fact]
    public void Workbench_oracle_rechecks_current_state_after_reported_completion()
    {
        var session = new WorkbenchQuestSession(new WorkbenchQuestBoard().Load("broken_check"));
        session.Execute(Invocation(WorkbenchQuestToolIds.RunCheck));
        session.Execute(Invocation(
            WorkbenchQuestToolIds.ReadFile,
            ("path", "src/Calculator.txt")));
        session.Execute(Invocation(
            WorkbenchQuestToolIds.ApplyPatch,
            ("path", "src/Calculator.txt"),
            ("find", "return left - right"),
            ("replace", "return left + right"),
            ("rationale", "Repair the failing implementation.")));
        session.Execute(Invocation(WorkbenchQuestToolIds.RunCheck));
        session.Execute(Invocation(WorkbenchQuestToolIds.Complete));

        Assert.True(BenchmarkOracles.Evaluate(session).Success);

        session.State.Files["src/Calculator.txt"] = session.State.Files["src/Calculator.txt"]
            .Replace("return left + right", "return left - right", StringComparison.Ordinal);

        var staleSuccess = BenchmarkOracles.Evaluate(session);
        Assert.False(staleSuccess.Success);
        Assert.Contains("authoritative_current_check_failed", staleSuccess.Failure, StringComparison.Ordinal);
        Assert.Contains("currentCheckPassed=False", staleSuccess.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_flushes_each_json_line_and_replaces_aggregate_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentica-benchmark-{Guid.NewGuid():N}");
        try
        {
            var store = BenchmarkStore.Create(root, "cohort-1");
            store.WriteManifest(new { version = "v1" });
            store.AppendRun(new { runId = "run-1", success = false });
            store.AppendRun(new { runId = "run-2", success = true });
            store.WriteAggregate(new { gatePassed = true, runCount = 2 });

            var lines = File.ReadAllLines(Path.Combine(store.DirectoryPath, "runs.jsonl"));
            Assert.Equal(2, lines.Length);
            Assert.Equal("run-1", JsonDocument.Parse(lines[0]).RootElement.GetProperty("runId").GetString());
            Assert.Equal("run-2", JsonDocument.Parse(lines[1]).RootElement.GetProperty("runId").GetString());

            using var aggregate = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(store.DirectoryPath, "aggregate.json")));
            Assert.True(aggregate.RootElement.GetProperty("gatePassed").GetBoolean());
            Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "aggregate.json.tmp")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Offline_reaggregation_accepts_the_original_v1_pricing_manifest_without_credentials()
    {
        var root = CreateOldPricingCohort();
        try
        {
            var manifestPath = Path.Combine(root, "manifest.json");
            var manifestBefore = File.ReadAllBytes(manifestPath);
            var expectedRunsHash = $"sha256-v1:{Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(root, "runs.jsonl"))))}";
            var credentialsChecked = false;

            var exitCode = await BenchmarkCommand.RunAsync(
                ["product-proof", "aggregate", root],
                () =>
                {
                    credentialsChecked = true;
                    throw new InvalidOperationException("Offline aggregation must not inspect credentials.");
                });

            Assert.Equal(0, exitCode);
            Assert.False(credentialsChecked);
            Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));

            using var aggregate = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "aggregate.json")));
            Assert.Equal(
                LabBenchmarks.ProductProofPricing.SnapshotId,
                aggregate.RootElement.GetProperty("pricingSnapshotId").GetString());
            Assert.True(aggregate.RootElement.GetProperty("gatePassed").GetBoolean());
            Assert.True(aggregate.RootElement.GetProperty("overall").GetProperty("costAvailable").GetBoolean());

            using var receipt = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "reaggregation.json")));
            Assert.Equal(
                LabBenchmarks.ProductProofPricing.SnapshotId,
                receipt.RootElement.GetProperty("pricingSnapshotId").GetString());
            Assert.Equal(
                LabBenchmarks.ProductProofBenchmarkMatrix.Current.Runs.Count,
                receipt.RootElement.GetProperty("runCount").GetInt32());
            Assert.Equal(expectedRunsHash, receipt.RootElement.GetProperty("runsSha256").GetString());
            Assert.Equal(
                LabBenchmarks.ProductProofBenchmarkFixedConfiguration.ConfigurationId,
                receipt.RootElement
                    .GetProperty("originalManifest")
                    .GetProperty("configurationId")
                    .GetString());
            Assert.Equal(
                LabBenchmarks.ProductProofBenchmarkMatrix.Version,
                receipt.RootElement
                    .GetProperty("originalManifest")
                    .GetProperty("matrixVersion")
                    .GetString());
            Assert.True(receipt.RootElement.GetProperty("gatePassed").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_reaggregation_rejects_an_extra_run_without_changing_aggregate()
    {
        var root = CreateOldPricingCohort();
        try
        {
            var runsPath = Path.Combine(root, "runs.jsonl");
            File.AppendAllText(runsPath, File.ReadLines(runsPath).First() + Environment.NewLine);
            var aggregatePath = Path.Combine(root, "aggregate.json");
            var aggregateBefore = File.ReadAllBytes(aggregatePath);

            var exitCode = await BenchmarkCommand.RunAsync(
                ["product-proof", "aggregate", root],
                () => throw new InvalidOperationException("Credentials must not be checked."));

            Assert.Equal(2, exitCode);
            Assert.Equal(aggregateBefore, File.ReadAllBytes(aggregatePath));
            Assert.False(File.Exists(Path.Combine(root, "reaggregation.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_reaggregation_rejects_manifest_matrix_drift_and_invalid_run_json()
    {
        var driftRoot = CreateOldPricingCohort(matrixVersionOverride: "untrusted-matrix");
        var invalidJsonRoot = CreateOldPricingCohort();
        try
        {
            var driftAggregate = File.ReadAllBytes(Path.Combine(driftRoot, "aggregate.json"));
            var driftExit = await BenchmarkCommand.RunAsync(
                ["product-proof", "aggregate", driftRoot],
                () => throw new InvalidOperationException("Credentials must not be checked."));
            Assert.Equal(2, driftExit);
            Assert.Equal(driftAggregate, File.ReadAllBytes(Path.Combine(driftRoot, "aggregate.json")));

            var invalidRuns = Path.Combine(invalidJsonRoot, "runs.jsonl");
            var lines = File.ReadAllLines(invalidRuns);
            lines[0] = lines[0][..^1] + ",\"unexpected\":true}";
            File.WriteAllLines(invalidRuns, lines);
            var invalidAggregate = File.ReadAllBytes(Path.Combine(invalidJsonRoot, "aggregate.json"));
            var invalidExit = await BenchmarkCommand.RunAsync(
                ["product-proof", "aggregate", invalidJsonRoot],
                () => throw new InvalidOperationException("Credentials must not be checked."));
            Assert.Equal(2, invalidExit);
            Assert.Equal(invalidAggregate, File.ReadAllBytes(Path.Combine(invalidJsonRoot, "aggregate.json")));

            lines[0] = "{";
            File.WriteAllLines(invalidRuns, lines);
            var malformedExit = await BenchmarkCommand.RunAsync(
                ["product-proof", "aggregate", invalidJsonRoot],
                () => throw new InvalidOperationException("Credentials must not be checked."));
            Assert.Equal(2, malformedExit);
            Assert.Equal(invalidAggregate, File.ReadAllBytes(Path.Combine(invalidJsonRoot, "aggregate.json")));
        }
        finally
        {
            Directory.Delete(driftRoot, recursive: true);
            Directory.Delete(invalidJsonRoot, recursive: true);
        }
    }

    private static string CreateOldPricingCohort(string? matrixVersionOverride = null)
    {
        const string oldPricingSnapshotId = "gemini-api-standard-pricing-2026-07-21-v1";
        var root = Path.Combine(Path.GetTempPath(), $"agentica-reaggregate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;
        var cohort = new LabBenchmarks.BenchmarkCohortIdentity(
            CohortId: $"test-cohort-{Guid.NewGuid():N}",
            MatrixVersion: matrix.Version,
            ProviderName: GeminiLlmClient.ProviderName,
            ModelId: GeminiModelId.Flash25,
            ConfigurationId: LabBenchmarks.ProductProofBenchmarkFixedConfiguration.ConfigurationId);
        var storedMatrix = matrixVersionOverride is null
            ? matrix
            : matrix with { Version = matrixVersionOverride };
        var configuration = LabBenchmarks.ProductProofBenchmarkFixedConfiguration.Current with
        {
            PricingSnapshotId = oldPricingSnapshotId
        };
        var manifest = new
        {
            harnessVersion = LabBenchmarks.ProductProofBenchmarkFixedConfiguration.HarnessVersion,
            startedAtUtc = DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            matrix = storedMatrix,
            cohort,
            configuration,
            pricing = new
            {
                snapshotId = oldPricingSnapshotId,
                reviewedOn = new DateOnly(2026, 7, 21),
                sourceUrl = LabBenchmarks.ProductProofPricing.SourceUrl,
                models = new[]
                {
                    new
                    {
                        modelId = GeminiModelId.Flash25,
                        inputUsdPerMillionTokens = 0.30m,
                        outputUsdPerMillionTokens = 2.50m
                    }
                }
            }
        };

        var jsonOptions = BenchmarkJsonOptions(writeIndented: true);
        File.WriteAllText(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(manifest, jsonOptions) + Environment.NewLine);

        var jsonLineOptions = BenchmarkJsonOptions(writeIndented: false);
        var results = matrix.Runs.Select((run, index) =>
            new LabBenchmarks.BenchmarkRunResult(
                Cohort: cohort,
                RunId: run.RunId,
                RunOutcomeStatus: "Succeeded",
                ReportedSuccess: true,
                OracleSuccess: true,
                InvalidPlan: false,
                Elapsed: TimeSpan.FromSeconds(1),
                RuntimeRetryCount: 0,
                OracleEvidence: "authoritative_host_oracle=true",
                OracleFailure: null,
                LlmCalls:
                [
                    new LabBenchmarks.BenchmarkLlmCallTelemetry(
                        CallId: $"test-call-{index:00}",
                        StartedAtUtc: DateTimeOffset.Parse("2026-07-21T12:00:00Z").AddSeconds(index),
                        Latency: TimeSpan.FromMilliseconds(200),
                        Succeeded: true,
                        RequestedModelId: GeminiModelId.Flash25,
                        ProviderName: GeminiLlmClient.ProviderName,
                        ResponseModelId: GeminiModelId.Flash25,
                        FinishReason: LlmFinishReason.Stop,
                        Usage: new LlmUsage(
                            PromptTokens: 100,
                            OutputTokens: 20,
                            ThinkingTokens: 0,
                            TotalTokens: 120,
                            CachedPromptTokens: 40),
                        PromptVersion: "workflow-plan-initial-prompt-v1",
                        SchemaVersion: "workflow-plan-initial-schema-v1",
                        RequestKind: "initial_plan",
                        IsRepair: false,
                        RetryAttempts: 1,
                        ErrorClass: null)
                ]));
        File.WriteAllLines(
            Path.Combine(root, "runs.jsonl"),
            results.Select(result => JsonSerializer.Serialize(result, jsonLineOptions)));
        File.WriteAllText(Path.Combine(root, "aggregate.json"), "{\"original\":true}" + Environment.NewLine);
        return root;
    }

    private static JsonSerializerOptions BenchmarkJsonOptions(bool writeIndented)
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

    private static ToolInvocation Invocation(
        string toolId,
        params (string Key, object? Value)[] input) =>
        new(
            "benchmark_test_run",
            $"step_{Guid.NewGuid():N}",
            toolId,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

using System.Collections.ObjectModel;

namespace Agentica.Lab.Benchmarks;

public static class ProductProofBenchmarkMatrix
{
    public const string Version = "agentica-product-proof-v1";
    public const int RepetitionsPerCase = 5;

    public static BenchmarkMatrix Current { get; } = Create();

    private static BenchmarkMatrix Create()
    {
        var cases = new[]
        {
            WorkbenchCase("broken_check"),
            WorkbenchCase("missing_mapping"),
            WorkbenchCase("structured_doc_merge"),
            WorkbenchCase("word_ladder"),
            WorkbenchCase("release_gate"),
            MazeHoldoutCase()
        };

        var runs = cases
            .SelectMany(benchmarkCase => Enumerable.Range(1, RepetitionsPerCase)
                .Select(repetition => new BenchmarkRunDefinition(
                    RunId: $"{benchmarkCase.CaseId}/run-{repetition:00}",
                    MatrixVersion: Version,
                    CaseId: benchmarkCase.CaseId,
                    Suite: benchmarkCase.Suite,
                    ScenarioId: benchmarkCase.ScenarioId,
                    Repetition: repetition,
                    Parameters: benchmarkCase.Parameters)))
            .ToArray();

        return new BenchmarkMatrix(
            Version,
            Array.AsReadOnly(cases),
            Array.AsReadOnly(runs));
    }

    private static BenchmarkCaseDefinition WorkbenchCase(string scenarioId) =>
        new(
            CaseId: $"{Version}/workbench/{scenarioId}",
            Suite: BenchmarkSuiteKind.PrimaryWorkbench,
            ScenarioId: scenarioId,
            Parameters: ReadOnlyParameters(
                ("scenario", scenarioId),
                ("suite", "workbench")));

    private static BenchmarkCaseDefinition MazeHoldoutCase() =>
        new(
            CaseId: $"{Version}/maze/unlock-seed173-7x7-visibility2",
            Suite: BenchmarkSuiteKind.GeneralizationHoldout,
            ScenarioId: "unlock",
            Parameters: ReadOnlyParameters(
                ("questType", "unlock"),
                ("seed", "173"),
                ("width", "7"),
                ("height", "7"),
                ("visibility", "2"),
                ("suite", "maze")));

    private static IReadOnlyDictionary<string, string> ReadOnlyParameters(
        params (string Key, string Value)[] values) =>
        new ReadOnlyDictionary<string, string>(
            values.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal));
}

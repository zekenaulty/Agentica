namespace Agentica.CLI.Scenarios.WorkbenchQuest;

public sealed record WorkbenchScenarioDescriptor(
    string ScenarioId,
    string Title,
    string Objective,
    string Description,
    string Difficulty,
    int EstimatedSteps);

public sealed record WorkbenchScenario(
    WorkbenchScenarioDescriptor Descriptor,
    IReadOnlyDictionary<string, WorkbenchFile> Files,
    IReadOnlyList<string> RelevantPaths);

public sealed record WorkbenchFile(
    string Path,
    string Content,
    bool ReadOnly = false,
    int MaxBytes = 12000);

public sealed class WorkbenchRunState
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> InitialFiles { get; } = new(StringComparer.Ordinal);

    public List<WorkbenchCheckRecord> CheckHistory { get; } = [];

    public List<WorkbenchPatchRecord> PatchHistory { get; } = [];

    public List<WorkbenchNoteRecord> Notes { get; } = [];

    public HashSet<string> ReadPaths { get; } = new(StringComparer.Ordinal);

    public HashSet<string> SearchQueries { get; } = new(StringComparer.Ordinal);

    public bool Completed { get; set; }

    public int NextActionOrder { get; set; } = 1;
}

public sealed record WorkbenchCheckRecord(
    int Number,
    int ActionOrder,
    bool Passed,
    string Output);

public sealed record WorkbenchPatchRecord(
    int Number,
    int ActionOrder,
    string Path,
    string Find,
    string Replace,
    bool Applied,
    string Message);

public sealed record WorkbenchNoteRecord(
    int Number,
    string Note);

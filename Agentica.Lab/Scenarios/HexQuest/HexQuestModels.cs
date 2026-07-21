namespace Agentica.Lab.Scenarios.HexQuest;

public sealed record HexQuestScenarioDescriptor(
    string ScenarioId,
    string Title,
    string Objective,
    string Description,
    string Difficulty,
    int EstimatedSteps);

public sealed record HexQuestScenario(
    HexQuestScenarioDescriptor Descriptor,
    HexQuestCodecProfile CodecProfile,
    HexQuestDecodedState InitialState,
    HexQuestGoal Goal,
    IReadOnlyList<HexQuestDecodedState> FewShotExamples);

public enum HexQuestCodecProfile
{
    IntroXorChecksum,
    RecordScopeConflict,
    RecordScopeConflictV2
}

public sealed record HexQuestDecodedState(
    int Strength,
    int Dexterity,
    int Gold,
    IReadOnlyList<HexQuestCharacterState>? Characters = null);

public sealed record HexQuestCharacterState(
    string EntityId,
    int Strength,
    int Dexterity,
    int Gold,
    int DisplayStrength = 0);

public sealed record HexQuestGoal(
    string Field,
    int TargetValue,
    IReadOnlyList<string> ProtectedFields,
    string? EntityId = null,
    int? MaxPatchBytes = null,
    IReadOnlyList<int>? ForbiddenOffsets = null,
    bool ExposePatchConstraints = true,
    bool TerseValidation = false,
    int RequiredContrastiveProbes = 0);

public sealed record HexPatchOperation(
    int Offset,
    byte OldByte,
    byte NewByte);

public sealed class HexQuestRunState
{
    public byte[] Encoded { get; set; } = [];

    public byte[] InitialEncoded { get; set; } = [];

    public HexQuestDecodedState InitialDecoded { get; set; } = new(0, 0, 0);

    public List<HexQuestExampleRecord> Examples { get; } = [];

    public List<HexQuestPatchRecord> Validations { get; } = [];

    public List<HexQuestPatchRecord> Commits { get; } = [];

    public List<HexQuestSandboxRecord> SandboxProbes { get; } = [];

    public bool Completed { get; set; }
}

public sealed record HexQuestExampleRecord(
    int Number,
    HexQuestDecodedState Decoded,
    string Encoded);

public sealed record HexQuestPatchRecord(
    int Number,
    string Patch,
    bool Accepted,
    string Message);

public sealed record HexQuestSandboxRecord(
    int Number,
    string? EntityId,
    string Field,
    int Value,
    bool Contrastive);

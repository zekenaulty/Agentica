namespace Agentica.CLI.Scenarios.HexQuest;

public interface IHexQuestBoard
{
    IReadOnlyList<HexQuestScenarioDescriptor> ListScenarios();

    HexQuestScenario Load(string scenarioId);
}

public sealed class HexQuestBoard : IHexQuestBoard
{
    private readonly HexQuestScenarioDescriptor[] _descriptors =
    [
        new(
            ScenarioId: "xor_checksum_strength",
            Title: "Strength Byte With Checksum",
            Objective: "Set Strength from 12 to 18 by committing an encoded payload patch while Dexterity and Gold stay unchanged.",
            Description: "An introductory transform-inference puzzle with XOR-masked fields and a checksum byte.",
            Difficulty: "Intro",
            EstimatedSteps: 7),
        new(
            ScenarioId: "record_scope_conflict",
            Title: "Record Scope Conflict",
            Objective: "Set character B Strength from 12 to 18 by committing a bounded encoded patch without touching other records or derived cache bytes.",
            Description: "A noisy payload puzzle where repeated values, derived cache bytes, and checksum repair force scope narrowing.",
            Difficulty: "Moderate",
            EstimatedSteps: 10),
        new(
            ScenarioId: "record_scope_conflict_v2",
            Title: "Record Scope Conflict V2",
            Objective: "Set character B Strength from 12 to 18 by committing a bounded encoded patch after isolating authoritative bytes from derived metadata.",
            Description: "A larger noisy payload with repeated records, display-strength decoys, checksum-like bytes, derived indexes, hidden forbidden offsets, and partial validation feedback.",
            Difficulty: "Hard",
            EstimatedSteps: 14)
    ];

    public IReadOnlyList<HexQuestScenarioDescriptor> ListScenarios() => _descriptors;

    public HexQuestScenario Load(string scenarioId)
    {
        var descriptor = _descriptors.FirstOrDefault(item =>
            string.Equals(item.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new InvalidOperationException($"Unknown HexQuest scenario '{scenarioId}'.");
        }

        return descriptor.ScenarioId switch
        {
            "record_scope_conflict_v2" => CreateRecordScopeConflictV2(descriptor),
            "record_scope_conflict" => CreateRecordScopeConflict(descriptor),
            _ => CreateIntro(descriptor)
        };
    }

    private static HexQuestScenario CreateIntro(HexQuestScenarioDescriptor descriptor) =>
        new(
            descriptor,
            HexQuestCodecProfile.IntroXorChecksum,
            new HexQuestDecodedState(Strength: 12, Dexterity: 9, Gold: 250),
            new HexQuestGoal(
                Field: "Strength",
                TargetValue: 18,
                ProtectedFields: ["Dexterity", "Gold"]),
            FewShotExamples:
            [
                new HexQuestDecodedState(Strength: 5, Dexterity: 8, Gold: 125),
                new HexQuestDecodedState(Strength: 21, Dexterity: 8, Gold: 125),
                new HexQuestDecodedState(Strength: 5, Dexterity: 11, Gold: 300),
                new HexQuestDecodedState(Strength: 18, Dexterity: 9, Gold: 250)
            ]);

    private static HexQuestScenario CreateRecordScopeConflict(HexQuestScenarioDescriptor descriptor) =>
        new(
            descriptor,
            HexQuestCodecProfile.RecordScopeConflict,
            RecordScopeState(12, 9, 250),
            new HexQuestGoal(
                Field: "Strength",
                TargetValue: 18,
                ProtectedFields:
                [
                    "A.Strength",
                    "A.Dexterity",
                    "A.Gold",
                    "B.Dexterity",
                    "B.Gold",
                    "C.Strength",
                    "C.Dexterity",
                    "C.Gold"
                ],
                EntityId: "B",
                MaxPatchBytes: 2,
                ForbiddenOffsets: [32, 40, 55]),
            FewShotExamples:
            [
                RecordScopeState(5, 9, 250),
                RecordScopeState(21, 9, 250),
                RecordScopeState(12, 11, 250),
                RecordScopeState(12, 9, 300)
            ]);

    private static HexQuestScenario CreateRecordScopeConflictV2(HexQuestScenarioDescriptor descriptor) =>
        new(
            descriptor,
            HexQuestCodecProfile.RecordScopeConflictV2,
            RecordScopeStateV2(12, 9, 250, displayStrength: 12),
            new HexQuestGoal(
                Field: "Strength",
                TargetValue: 18,
                ProtectedFields:
                [
                    "A.Strength",
                    "A.Dexterity",
                    "A.Gold",
                    "A.DisplayStrength",
                    "B.Dexterity",
                    "B.Gold",
                    "B.DisplayStrength",
                    "C.Strength",
                    "C.Dexterity",
                    "C.Gold",
                    "C.DisplayStrength",
                    "D.Strength",
                    "D.Dexterity",
                    "D.Gold",
                    "D.DisplayStrength"
                ],
                EntityId: "B",
                MaxPatchBytes: 2,
                ForbiddenOffsets: [5, 6, 36, 76, 80, 88, 96, 104, 112, 113, 121],
                ExposePatchConstraints: false,
                TerseValidation: true,
                RequiredContrastiveProbes: 1),
            FewShotExamples:
            [
                RecordScopeStateV2(5, 9, 250, displayStrength: 12),
                RecordScopeStateV2(21, 9, 250, displayStrength: 12),
                RecordScopeStateV2(12, 11, 250, displayStrength: 12),
                RecordScopeStateV2(12, 9, 300, displayStrength: 12)
            ]);

    private static HexQuestDecodedState RecordScopeState(int strength, int dexterity, int gold) =>
        new(
            strength,
            dexterity,
            gold,
            [
                new HexQuestCharacterState("A", 12, 9, 250),
                new HexQuestCharacterState("B", strength, dexterity, gold),
                new HexQuestCharacterState("C", 12, 9, 250)
            ]);

    private static HexQuestDecodedState RecordScopeStateV2(int strength, int dexterity, int gold, int displayStrength) =>
        new(
            strength,
            dexterity,
            gold,
            [
                new HexQuestCharacterState("A", 12, 9, 250, 12),
                new HexQuestCharacterState("B", strength, dexterity, gold, displayStrength),
                new HexQuestCharacterState("C", 12, 9, 250, 12),
                new HexQuestCharacterState("D", 12, 9, 250, 12)
            ]);
}

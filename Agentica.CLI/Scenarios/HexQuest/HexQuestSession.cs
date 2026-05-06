using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.HexQuest;

public sealed class HexQuestSession
{
    public HexQuestSession(HexQuestScenario scenario)
    {
        Scenario = scenario;
        State = new HexQuestRunState
        {
            Encoded = HexQuestCodec.Encode(scenario, scenario.InitialState),
            InitialEncoded = HexQuestCodec.Encode(scenario, scenario.InitialState),
            InitialDecoded = scenario.InitialState
        };
    }

    public HexQuestScenario Scenario { get; }

    public HexQuestRunState State { get; }

    public ToolResult Execute(ToolInvocation invocation) =>
        invocation.ToolId switch
        {
            HexQuestToolIds.InspectEncoded => InspectEncoded(invocation),
            HexQuestToolIds.InspectDecoded => InspectDecoded(invocation),
            HexQuestToolIds.RequestExample => RequestExample(invocation),
            HexQuestToolIds.SandboxSetDecoded => SandboxSetDecoded(invocation),
            HexQuestToolIds.ValidatePatch => ValidatePatch(invocation),
            HexQuestToolIds.CommitPatch => CommitPatch(invocation),
            _ => Refused(invocation, "unknown_hexquest_tool", $"Unknown HexQuest tool '{invocation.ToolId}'.")
        };

    public IReadOnlyDictionary<string, object?> PublicSnapshot() => Snapshot("snapshot");

    private ToolResult InspectEncoded(ToolInvocation invocation)
    {
        var data = Snapshot("inspect_encoded");
        data["encoded"] = HexQuestCodec.ToHex(State.Encoded);
        data["byteCount"] = State.Encoded.Length;
        data["byteOffsets"] = Enumerable.Range(0, State.Encoded.Length).ToArray();
        return Observed(invocation, ReceiptStatus.Succeeded, "Encoded payload inspected.", "Encoded payload bytes observed.", data);
    }

    private ToolResult InspectDecoded(ToolInvocation invocation)
    {
        var data = Snapshot("inspect_decoded");
        data["decoded"] = DecodedPayload(HexQuestCodec.Decode(Scenario, State.Encoded));
        data["goal"] = GoalPayload();
        return Observed(invocation, ReceiptStatus.Succeeded, "Decoded view inspected.", "Decoded view and goal observed.", data);
    }

    private ToolResult RequestExample(ToolInvocation invocation)
    {
        if (State.Examples.Count >= Scenario.FewShotExamples.Count)
        {
            return Refused(invocation, "no_more_examples", "No more few-shot examples are available.");
        }

        var decoded = Scenario.FewShotExamples[State.Examples.Count];
        var encoded = HexQuestCodec.Encode(Scenario, decoded);
        var record = new HexQuestExampleRecord(State.Examples.Count + 1, decoded, HexQuestCodec.ToHex(encoded));
        State.Examples.Add(record);

        var data = Snapshot("request_example");
        data["exampleNumber"] = record.Number;
        data["decoded"] = DecodedPayload(record.Decoded);
        data["encoded"] = record.Encoded;

        return Observed(invocation, ReceiptStatus.Succeeded, $"Few-shot example {record.Number} returned.", "Decoded/encoded example pair observed.", data);
    }

    private ToolResult SandboxSetDecoded(ToolInvocation invocation)
    {
        var field = ReadString(invocation, "field");
        var entity = ReadString(invocation, "entity") ?? Scenario.Goal.EntityId;
        var value = ReadInt(invocation, "value");
        if (field is null || value is null)
        {
            return Refused(invocation, "missing_sandbox_input", "Sandbox decoded edit requires field and value.");
        }

        var before = HexQuestCodec.Decode(Scenario, State.Encoded);
        var after = WithField(before, field, value.Value, entity);
        if (after is null)
        {
            return Refused(invocation, "unknown_field", $"Unknown decoded field '{field}'.");
        }

        var sandboxEncoded = HexQuestCodec.Encode(Scenario, after);
        var data = Snapshot("sandbox_set_decoded");
        data["sandboxOnly"] = true;
        data["field"] = field;
        data["entity"] = entity;
        data["value"] = value.Value;
        data["beforeDecoded"] = DecodedPayload(before);
        data["afterDecoded"] = DecodedPayload(after);
        data["beforeEncoded"] = HexQuestCodec.ToHex(State.Encoded);
        data["afterEncoded"] = HexQuestCodec.ToHex(sandboxEncoded);
        data["diff"] = HexQuestCodec.Diff(State.Encoded, sandboxEncoded);

        return Observed(invocation, ReceiptStatus.Succeeded, "Sandbox decoded edit diff returned.", "Sandbox encoded diff observed without mutating authoritative payload.", data);
    }

    private ToolResult ValidatePatch(ToolInvocation invocation)
    {
        var patchText = ReadString(invocation, "patch");
        if (!TryParsePatch(patchText, out var operations, out var error))
        {
            return Refused(invocation, "invalid_patch", error);
        }

        var validation = EvaluatePatch(operations);
        var record = new HexQuestPatchRecord(State.Validations.Count + 1, patchText ?? string.Empty, validation.Accepted, validation.Message);
        State.Validations.Add(record);

        var data = Snapshot("validate_patch");
        AddEvaluationData(data, validation);
        data["patch"] = patchText;
        data["validationNumber"] = record.Number;

        return Observed(invocation, ReceiptStatus.Succeeded, "Encoded patch dry-run completed.", "Encoded patch validation observed.", data);
    }

    private ToolResult CommitPatch(ToolInvocation invocation)
    {
        var patchText = ReadString(invocation, "patch");
        if (!TryParsePatch(patchText, out var operations, out var error))
        {
            return Refused(invocation, "invalid_patch", error);
        }

        var validation = EvaluatePatch(operations);
        if (!validation.Accepted)
        {
            var record = new HexQuestPatchRecord(State.Commits.Count + 1, patchText ?? string.Empty, Accepted: false, validation.Message);
            State.Commits.Add(record);
            return Refused(
                invocation,
                "patch_does_not_satisfy_goal",
                validation.Message,
                EvaluationData(validation, patchText, record.Number));
        }

        var acceptedRecord = new HexQuestPatchRecord(State.Commits.Count + 1, patchText ?? string.Empty, Accepted: true, validation.Message);
        State.Commits.Add(acceptedRecord);

        var data = Snapshot("commit_patch");
        AddEvaluationData(data, validation);
        data["patch"] = patchText;
        data["commitNumber"] = acceptedRecord.Number;
        data["objectiveCompleted"] = true;

        State.Encoded = validation.PatchedEncoded;
        State.Completed = true;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "HexQuest objective completed by encoded payload patch.", data);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            "hexquest.objective_completed",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, Artifact: artifact);
    }

    private HexPatchEvaluation EvaluatePatch(IReadOnlyList<HexPatchOperation> operations)
    {
        var patched = State.Encoded.ToArray();
        if (Scenario.Goal.MaxPatchBytes is not null && operations.Count > Scenario.Goal.MaxPatchBytes.Value)
        {
            return HexPatchEvaluation.Reject(
                patched,
                $"Patch edits {operations.Count} byte(s), but this scenario allows at most {Scenario.Goal.MaxPatchBytes.Value} committed byte edits.");
        }

        var forbiddenOffsets = Scenario.Goal.ForbiddenOffsets?.ToHashSet() ?? [];
        var forbiddenTouched = operations.Where(operation => forbiddenOffsets.Contains(operation.Offset)).Select(operation => operation.Offset).ToArray();
        if (forbiddenTouched.Length > 0)
        {
            return HexPatchEvaluation.Reject(
                patched,
                $"Patch touches forbidden derived/cache offset(s): {string.Join(", ", forbiddenTouched)}.");
        }

        foreach (var operation in operations)
        {
            if (operation.Offset < 0 || operation.Offset >= patched.Length)
            {
                return HexPatchEvaluation.Reject(patched, $"Patch offset {operation.Offset} is outside the encoded payload.");
            }

            if (patched[operation.Offset] != operation.OldByte)
            {
                return HexPatchEvaluation.Reject(
                    patched,
                    $"Patch offset {operation.Offset} expected old byte {operation.OldByte:X2}, but current byte is {patched[operation.Offset]:X2}.");
            }

            patched[operation.Offset] = operation.NewByte;
        }

        var decoded = HexQuestCodec.Decode(Scenario, patched);
        var checksumValid = HexQuestCodec.HasValidChecksum(Scenario, patched);
        var goalSatisfied = FieldValue(decoded, Scenario.Goal.Field, Scenario.Goal.EntityId) == Scenario.Goal.TargetValue;
        var protectedUnchanged = Scenario.Goal.ProtectedFields.All(field =>
            FieldValue(decoded, field, null) == FieldValue(State.InitialDecoded, field, null));

        if (!checksumValid)
        {
            return HexPatchEvaluation.Reject(patched, "Patch changed the payload but checksum validation failed.");
        }

        if (!goalSatisfied)
        {
            return HexPatchEvaluation.Reject(patched, $"Patch does not set {Scenario.Goal.Field} to {Scenario.Goal.TargetValue}.");
        }

        if (!protectedUnchanged)
        {
            return HexPatchEvaluation.Reject(patched, "Patch changes at least one protected decoded field.");
        }

        return HexPatchEvaluation.Accept(patched, decoded, "Patch satisfies the decoded goal, preserves protected fields, and keeps checksum valid.");
    }

    private Dictionary<string, object?> EvaluationData(HexPatchEvaluation validation, string? patchText, int commitNumber)
    {
        var data = Snapshot("commit_refused");
        AddEvaluationData(data, validation);
        data["patch"] = patchText;
        data["commitNumber"] = commitNumber;
        return data;
    }

    private void AddEvaluationData(Dictionary<string, object?> data, HexPatchEvaluation validation)
    {
        data["accepted"] = validation.Accepted;
        data["message"] = validation.Message;
        data["beforeEncoded"] = HexQuestCodec.ToHex(State.Encoded);
        data["afterEncoded"] = HexQuestCodec.ToHex(validation.PatchedEncoded);
        data["diff"] = HexQuestCodec.Diff(State.Encoded, validation.PatchedEncoded);
        data["afterDecoded"] = DecodedPayload(HexQuestCodec.Decode(Scenario, validation.PatchedEncoded));
        data["checksumValid"] = HexQuestCodec.HasValidChecksum(Scenario, validation.PatchedEncoded);
        data["goalSatisfied"] = FieldValue(HexQuestCodec.Decode(Scenario, validation.PatchedEncoded), Scenario.Goal.Field, Scenario.Goal.EntityId) == Scenario.Goal.TargetValue;
        data["protectedFieldsUnchanged"] = Scenario.Goal.ProtectedFields.All(field =>
            FieldValue(HexQuestCodec.Decode(Scenario, validation.PatchedEncoded), field, null) == FieldValue(State.InitialDecoded, field, null));
        data["maxPatchBytes"] = Scenario.Goal.MaxPatchBytes;
        data["forbiddenOffsets"] = Scenario.Goal.ForbiddenOffsets;
    }

    private Dictionary<string, object?> Snapshot(string action) =>
        new(StringComparer.Ordinal)
        {
            ["scenarioId"] = Scenario.Descriptor.ScenarioId,
            ["title"] = Scenario.Descriptor.Title,
            ["objective"] = Scenario.Descriptor.Objective,
            ["action"] = action,
            ["examplesRequested"] = State.Examples.Count,
            ["validations"] = State.Validations.Count,
            ["commits"] = State.Commits.Count,
            ["objectiveCompleted"] = State.Completed
        };

    private Dictionary<string, object?> GoalPayload() =>
        new(StringComparer.Ordinal)
        {
            ["field"] = Scenario.Goal.Field,
            ["targetValue"] = Scenario.Goal.TargetValue,
            ["protectedFields"] = Scenario.Goal.ProtectedFields
                ,
            ["entityId"] = Scenario.Goal.EntityId,
            ["maxPatchBytes"] = Scenario.Goal.MaxPatchBytes,
            ["forbiddenOffsets"] = Scenario.Goal.ForbiddenOffsets
        };

    private static Dictionary<string, object?> DecodedPayload(HexQuestDecodedState state) =>
        new(StringComparer.Ordinal)
        {
            ["Strength"] = state.Strength,
            ["Dexterity"] = state.Dexterity,
            ["Gold"] = state.Gold,
            ["characters"] = state.Characters?.Select(character => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["entityId"] = character.EntityId,
                ["Strength"] = character.Strength,
                ["Dexterity"] = character.Dexterity,
                ["Gold"] = character.Gold
            }).ToArray()
        };

    private static HexQuestDecodedState? WithField(HexQuestDecodedState state, string field, int value, string? entityId)
    {
        if (entityId is not null && state.Characters is not null)
        {
            var characters = state.Characters.Select(character =>
                string.Equals(character.EntityId, entityId, StringComparison.OrdinalIgnoreCase)
                    ? WithCharacterField(character, field, value)
                    : character).ToArray();
            if (characters.SequenceEqual(state.Characters))
            {
                return null;
            }

            var target = characters.First(character => string.Equals(character.EntityId, entityId, StringComparison.OrdinalIgnoreCase));
            return new HexQuestDecodedState(target.Strength, target.Dexterity, target.Gold, characters);
        }

        return field.ToLowerInvariant() switch
        {
            "strength" when value is >= 0 and <= 255 => state with { Strength = value },
            "dexterity" when value is >= 0 and <= 255 => state with { Dexterity = value },
            "gold" when value is >= 0 and <= 65535 => state with { Gold = value },
            _ => null
        };
    }

    private static HexQuestCharacterState WithCharacterField(HexQuestCharacterState character, string field, int value) =>
        field.ToLowerInvariant() switch
        {
            "strength" when value is >= 0 and <= 255 => character with { Strength = value },
            "dexterity" when value is >= 0 and <= 255 => character with { Dexterity = value },
            "gold" when value is >= 0 and <= 65535 => character with { Gold = value },
            _ => character
        };

    private static int? FieldValue(HexQuestDecodedState state, string field, string? entityId)
    {
        var parts = field.Split('.', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            entityId = parts[0];
            field = parts[1];
        }

        if (entityId is not null && state.Characters is not null)
        {
            var character = state.Characters.FirstOrDefault(item => string.Equals(item.EntityId, entityId, StringComparison.OrdinalIgnoreCase));
            if (character is null)
            {
                return null;
            }

            return field.ToLowerInvariant() switch
            {
                "strength" => character.Strength,
                "dexterity" => character.Dexterity,
                "gold" => character.Gold,
                _ => null
            };
        }

        return field.ToLowerInvariant() switch
        {
            "strength" => state.Strength,
            "dexterity" => state.Dexterity,
            "gold" => state.Gold,
            _ => null
        };
    }

    private static bool TryParsePatch(string? patch, out IReadOnlyList<HexPatchOperation> operations, out string error)
    {
        operations = [];
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(patch))
        {
            error = "Patch is required.";
            return false;
        }

        var parsed = new List<HexPatchOperation>();
        var seenOffsets = new HashSet<int>();
        foreach (var rawPart in patch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var offsetSplit = rawPart.Split(':', 2, StringSplitOptions.TrimEntries);
            if (offsetSplit.Length != 2 || !int.TryParse(offsetSplit[0], out var offset))
            {
                error = $"Patch segment '{rawPart}' must start with a numeric offset.";
                return false;
            }

            if (!seenOffsets.Add(offset))
            {
                error = $"Patch offset {offset} appears more than once.";
                return false;
            }

            var byteSplit = offsetSplit[1].Split('>', 2, StringSplitOptions.TrimEntries);
            if (byteSplit.Length != 2 ||
                !TryParseHexByte(byteSplit[0], out var oldByte) ||
                !TryParseHexByte(byteSplit[1], out var newByte))
            {
                error = $"Patch segment '{rawPart}' must use offset:old>new with two hex bytes.";
                return false;
            }

            parsed.Add(new HexPatchOperation(offset, oldByte, newByte));
        }

        if (parsed.Count == 0)
        {
            error = "Patch must include at least one byte operation.";
            return false;
        }

        operations = parsed;
        return true;
    }

    private static bool TryParseHexByte(string value, out byte result) =>
        byte.TryParse(
            value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);

    private ToolResult Observed(
        ToolInvocation invocation,
        ReceiptStatus status,
        string receiptMessage,
        string observationSummary,
        IReadOnlyDictionary<string, object?> data)
    {
        var receipt = Receipt(invocation, status, receiptMessage, data);
        return new ToolResult(receipt, Observation(invocation, receipt, observationSummary, data));
    }

    private ToolResult Refused(
        ToolInvocation invocation,
        string reason,
        string message,
        IReadOnlyDictionary<string, object?>? extraData = null)
    {
        var data = Snapshot("refused");
        data["reason"] = reason;
        data["blocker"] = reason;
        if (extraData is not null)
        {
            foreach (var pair in extraData)
            {
                data[pair.Key] = pair.Value;
            }
        }

        var receipt = Receipt(invocation, ReceiptStatus.Refused, message, data);
        return new ToolResult(receipt, Observation(invocation, receipt, message, data));
    }

    private static string? ReadString(ToolInvocation invocation, string key) =>
        invocation.Input.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? ReadInt(ToolInvocation invocation, string key)
    {
        if (!invocation.Input.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        string message,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            data);

    private static Observation Observation(
        ToolInvocation invocation,
        Receipt receipt,
        string summary,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.ToolResult,
            summary,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);

    private sealed record HexPatchEvaluation(
        bool Accepted,
        byte[] PatchedEncoded,
        HexQuestDecodedState? Decoded,
        string Message)
    {
        public static HexPatchEvaluation Accept(byte[] patchedEncoded, HexQuestDecodedState decoded, string message) =>
            new(true, patchedEncoded, decoded, message);

        public static HexPatchEvaluation Reject(byte[] patchedEncoded, string message) =>
            new(false, patchedEncoded, null, message);
    }
}

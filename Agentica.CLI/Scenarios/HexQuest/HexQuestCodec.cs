namespace Agentica.CLI.Scenarios.HexQuest;

public static class HexQuestCodec
{
    private const byte StrengthMask = 0xA5;
    private const byte DexterityMask = 0x5A;
    private const byte GoldLowMask = 0xC3;
    private const byte GoldHighMask = 0x3C;
    private const byte ChecksumMask = 0x99;
    private const byte RecordChecksumMask = 0x6D;

    public static byte[] Encode(HexQuestScenario scenario, HexQuestDecodedState state) =>
        scenario.CodecProfile switch
        {
            HexQuestCodecProfile.RecordScopeConflict => EncodeRecordScopeConflict(state),
            _ => Encode(state)
        };

    public static byte[] Encode(HexQuestDecodedState state)
    {
        if (state.Strength is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Strength must fit in one byte.");
        }

        if (state.Dexterity is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Dexterity must fit in one byte.");
        }

        if (state.Gold is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Gold must fit in two bytes.");
        }

        var bytes = new byte[8];
        bytes[0] = (byte)(state.Strength ^ StrengthMask);
        bytes[1] = (byte)(state.Dexterity ^ DexterityMask);
        bytes[2] = (byte)((state.Gold & 0xFF) ^ GoldLowMask);
        bytes[3] = (byte)(((state.Gold >> 8) & 0xFF) ^ GoldHighMask);
        bytes[4] = Checksum(bytes);
        bytes[5] = 0x42;
        bytes[6] = 0x10;
        bytes[7] = 0xE1;
        return bytes;
    }

    public static HexQuestDecodedState Decode(HexQuestScenario scenario, IReadOnlyList<byte> bytes) =>
        scenario.CodecProfile switch
        {
            HexQuestCodecProfile.RecordScopeConflict => DecodeRecordScopeConflict(bytes),
            _ => Decode(bytes)
        };

    public static HexQuestDecodedState Decode(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count < 8)
        {
            throw new ArgumentException("HexQuest payload must contain 8 bytes.", nameof(bytes));
        }

        var strength = bytes[0] ^ StrengthMask;
        var dexterity = bytes[1] ^ DexterityMask;
        var goldLow = bytes[2] ^ GoldLowMask;
        var goldHigh = bytes[3] ^ GoldHighMask;
        return new HexQuestDecodedState(strength, dexterity, goldLow | (goldHigh << 8));
    }

    public static bool HasValidChecksum(HexQuestScenario scenario, IReadOnlyList<byte> bytes) =>
        scenario.CodecProfile switch
        {
            HexQuestCodecProfile.RecordScopeConflict => HasValidRecordChecksum(bytes),
            _ => HasValidChecksum(bytes)
        };

    public static bool HasValidChecksum(IReadOnlyList<byte> bytes) =>
        bytes.Count >= 5 && bytes[4] == Checksum(bytes);

    public static string ToHex(IReadOnlyList<byte> bytes) =>
        string.Join(' ', bytes.Select(item => item.ToString("X2")));

    public static IReadOnlyList<Dictionary<string, object?>> Diff(IReadOnlyList<byte> before, IReadOnlyList<byte> after)
    {
        var count = Math.Min(before.Count, after.Count);
        var changes = new List<Dictionary<string, object?>>();
        for (var index = 0; index < count; index++)
        {
            if (before[index] == after[index])
            {
                continue;
            }

            changes.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["offset"] = index,
                ["old"] = before[index].ToString("X2"),
                ["new"] = after[index].ToString("X2")
            });
        }

        return changes;
    }

    private static byte Checksum(IReadOnlyList<byte> bytes)
    {
        var sum = 0;
        for (var index = 0; index < 4; index++)
        {
            sum = (sum + bytes[index]) & 0xFF;
        }

        return (byte)(sum ^ ChecksumMask);
    }

    private static byte[] EncodeRecordScopeConflict(HexQuestDecodedState state)
    {
        var characters = state.Characters ?? DefaultCharacters(state);
        var a = characters.FirstOrDefault(item => item.EntityId == "A") ?? new HexQuestCharacterState("A", 12, 9, 250);
        var b = characters.FirstOrDefault(item => item.EntityId == "B") ?? new HexQuestCharacterState("B", state.Strength, state.Dexterity, state.Gold);
        var c = characters.FirstOrDefault(item => item.EntityId == "C") ?? new HexQuestCharacterState("C", 12, 9, 250);

        var bytes = Enumerable.Repeat((byte)0x00, 64).ToArray();
        bytes[0] = 0x48;
        bytes[1] = 0x51;
        bytes[2] = 0x32;
        bytes[3] = 0x10;
        WriteRecord(bytes, 8, a);
        WriteRecord(bytes, 16, b);
        WriteRecord(bytes, 24, c);

        // Derived projections intentionally move with sandbox decoded edits but are not authoritative.
        bytes[32] = (byte)(b.Strength ^ StrengthMask);
        bytes[40] = (byte)(b.Strength ^ StrengthMask);
        bytes[48] = RecordChecksum(bytes);
        bytes[55] = (byte)(b.Strength ^ StrengthMask);
        bytes[56] = 0xA9;
        bytes[57] = 0xB7;
        bytes[63] = 0xEE;
        return bytes;
    }

    private static HexQuestDecodedState DecodeRecordScopeConflict(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count < 64)
        {
            throw new ArgumentException("Record-scope HexQuest payload must contain 64 bytes.", nameof(bytes));
        }

        var characters = new[]
        {
            ReadRecord(bytes, 8, "A"),
            ReadRecord(bytes, 16, "B"),
            ReadRecord(bytes, 24, "C")
        };
        var target = characters[1];
        return new HexQuestDecodedState(target.Strength, target.Dexterity, target.Gold, characters);
    }

    private static IReadOnlyList<HexQuestCharacterState> DefaultCharacters(HexQuestDecodedState target) =>
    [
        new("A", 12, 9, 250),
        new("B", target.Strength, target.Dexterity, target.Gold),
        new("C", 12, 9, 250)
    ];

    private static void WriteRecord(byte[] bytes, int offset, HexQuestCharacterState state)
    {
        bytes[offset] = (byte)(state.Strength ^ StrengthMask);
        bytes[offset + 1] = (byte)(state.Dexterity ^ DexterityMask);
        bytes[offset + 2] = (byte)((state.Gold & 0xFF) ^ GoldLowMask);
        bytes[offset + 3] = (byte)(((state.Gold >> 8) & 0xFF) ^ GoldHighMask);
    }

    private static HexQuestCharacterState ReadRecord(IReadOnlyList<byte> bytes, int offset, string entityId)
    {
        var strength = bytes[offset] ^ StrengthMask;
        var dexterity = bytes[offset + 1] ^ DexterityMask;
        var goldLow = bytes[offset + 2] ^ GoldLowMask;
        var goldHigh = bytes[offset + 3] ^ GoldHighMask;
        return new HexQuestCharacterState(entityId, strength, dexterity, goldLow | (goldHigh << 8));
    }

    private static bool HasValidRecordChecksum(IReadOnlyList<byte> bytes) =>
        bytes.Count >= 49 && bytes[48] == RecordChecksum(bytes);

    private static byte RecordChecksum(IReadOnlyList<byte> bytes)
    {
        var sum = 0;
        for (var index = 16; index <= 19; index++)
        {
            sum = (sum + bytes[index]) & 0xFF;
        }

        return (byte)(sum ^ RecordChecksumMask);
    }
}

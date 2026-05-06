namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed class MazeQuestDecorators
{
    private static readonly string[] WarmAffixes =
    [
        "warm",
        "etched",
        "brass",
        "faint"
    ];

    private static readonly string[] KeySuffixes =
    [
        "of dawn",
        "of the lintel",
        "of quiet light",
        "of the north lock"
    ];

    private static readonly string[] RelicNames =
    [
        "sun shard",
        "gate sigil",
        "amber token",
        "brass mote"
    ];

    private static readonly string[] PlaceNames =
    [
        "quiet shrine",
        "low arch",
        "east marker",
        "stone alcove"
    ];

    private static readonly string[] ActivatorNames =
    [
        "moon obelisk",
        "sun obelisk",
        "star switch",
        "brass lever"
    ];

    private static readonly string[] RuneNames =
    [
        "moon rune",
        "sun rune",
        "star rune"
    ];

    private readonly Random _random;

    public MazeQuestDecorators(Random random)
    {
        _random = random;
    }

    public string DecorateObjectiveItem(MazeObjectiveItem item)
    {
        if (item == MazeObjectiveItem.None)
        {
            return string.Empty;
        }

        var affix = WarmAffixes[_random.Next(WarmAffixes.Length)];
        var suffix = KeySuffixes[_random.Next(KeySuffixes.Length)];
        return $"{affix} sun key {suffix}";
    }

    public string DecorateReward(MazeReward reward)
    {
        return reward switch
        {
            MazeReward.Health => "small mending draught",
            MazeReward.Energy => "clear focus ember",
            MazeReward.Clue => "creased direction note",
            _ => string.Empty
        };
    }

    public string Collectible(int index) =>
        $"{RelicNames[_random.Next(RelicNames.Length)]} {index}";

    public string DeliveryPickup() =>
        "sealed courier charm";

    public string DeliveryDropoff() =>
        PlaceNames[_random.Next(PlaceNames.Length)];

    public string DiscoveryMarker(int index) =>
        $"{PlaceNames[_random.Next(PlaceNames.Length)]} {index}";

    public string Activator(int index) =>
        $"{ActivatorNames[_random.Next(ActivatorNames.Length)]} {index}";

    public string PuzzleRune(int index) =>
        RuneNames[Math.Clamp(index - 1, 0, RuneNames.Length - 1)];

    public string RescueTarget() =>
        "lost scout marker";

    public string Refuge() =>
        "safe return mark";

    public string ResourceCache() =>
        "steady focus cache";
}

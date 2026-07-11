using Agentica.Tools;

namespace Agentica.Execution;

public sealed record ToolEffectPolicy(IReadOnlySet<ToolEffect> AllowedEffects)
{
    public static ToolEffectPolicy LocalOnly { get; } = new(
        new HashSet<ToolEffect>
        {
            ToolEffect.ReadOnly,
            ToolEffect.WritesLocalState
        });

    public static ToolEffectPolicy AllowKnown { get; } = new(
        Enum.GetValues<ToolEffect>()
            .Where(effect => effect != ToolEffect.Unknown)
            .ToHashSet());

    public static ToolEffectPolicy AllowAll { get; } = AllowKnown;

    public static ToolEffectPolicy UnsafeAllowUnknown { get; } = new(
        Enum.GetValues<ToolEffect>().ToHashSet());

    public bool Allows(ToolEffect effect) =>
        AllowedEffects.Contains(effect);
}

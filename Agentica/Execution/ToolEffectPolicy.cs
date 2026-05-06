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

    public static ToolEffectPolicy AllowAll { get; } = new(
        Enum.GetValues<ToolEffect>().ToHashSet());

    public bool Allows(ToolEffect effect) =>
        AllowedEffects.Contains(effect);
}

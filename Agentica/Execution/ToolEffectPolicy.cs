using System.Collections.Frozen;
using Agentica.Tools;

namespace Agentica.Execution;

public sealed record ToolEffectPolicy
{
    public ToolEffectPolicy(IEnumerable<ToolEffect> AllowedEffects)
    {
        ArgumentNullException.ThrowIfNull(AllowedEffects);
        this.AllowedEffects = AllowedEffects.ToFrozenSet();
    }

    public IReadOnlySet<ToolEffect> AllowedEffects { get; }

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

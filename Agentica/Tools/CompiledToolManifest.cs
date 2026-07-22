using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace Agentica.Tools;

/// <summary>
/// Immutable runtime registration compiled from caller-owned registration data.
/// PlannerProjection and Security are deep snapshots; Tool is the dispatch target.
/// </summary>
public sealed class CompiledToolRegistration
{
    internal CompiledToolRegistration(
        ToolDescriptor plannerProjection,
        ToolSecurityDeclaration security,
        ITool tool)
    {
        PlannerProjection = plannerProjection;
        Security = security;
        Tool = tool;
    }

    public ToolDescriptor PlannerProjection { get; }

    public ToolSecurityDeclaration Security { get; }

    public ITool Tool { get; }

    // Kept as a concise runtime alias; the planner-facing name remains explicit.
    public ToolDescriptor Descriptor => PlannerProjection;
}

/// <summary>
/// One canonical, immutable tool surface used for both planning and dispatch.
/// </summary>
public sealed class CompiledToolManifest
{
    private readonly FrozenDictionary<string, CompiledToolRegistration> _registrations;

    internal CompiledToolManifest(
        string manifestHash,
        IReadOnlyList<CompiledToolRegistration> registrations)
    {
        ManifestHash = manifestHash;
        Registrations = new ReadOnlyCollection<CompiledToolRegistration>(registrations.ToArray());
        PlannerProjection = new ReadOnlyCollection<ToolDescriptor>(
            registrations.Select(registration => registration.PlannerProjection).ToArray());
        _registrations = registrations.ToFrozenDictionary(
            registration => registration.PlannerProjection.ToolId,
            StringComparer.Ordinal);
    }

    public string ManifestHash { get; }

    public IReadOnlyList<CompiledToolRegistration> Registrations { get; }

    public IReadOnlyList<ToolDescriptor> PlannerProjection { get; }

    public CompiledToolRegistration? Resolve(string toolId) =>
        _registrations.TryGetValue(toolId, out var registration)
            ? registration
            : null;
}

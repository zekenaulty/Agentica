namespace Agentica.Tools;

public sealed class ToolCatalog
{
    private readonly ToolRegistration[] _sourceRegistrations;
    private readonly CompiledToolManifest _manifest;

    private ToolCatalog(IEnumerable<ToolRegistration> registrations)
    {
        _sourceRegistrations = registrations.ToArray();
        _manifest = ToolManifestCompiler.Compile(_sourceRegistrations);
    }

    public IReadOnlyList<ToolDescriptor> Descriptors => _manifest.PlannerProjection;

    public CompiledToolManifest Manifest => _manifest;

    public string ManifestHash => _manifest.ManifestHash;

    public static ToolCatalog Create(params ToolRegistration[] registrations) =>
        new(registrations);

    public CompiledToolRegistration? Resolve(string toolId) => _manifest.Resolve(toolId);

    /// <summary>
    /// Recompiles the caller-owned sources so dispatch can detect post-planning drift.
    /// </summary>
    internal CompiledToolManifest CompileCurrentManifest() =>
        ToolManifestCompiler.Compile(_sourceRegistrations);
}

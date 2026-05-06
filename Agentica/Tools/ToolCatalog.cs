namespace Agentica.Tools;

public sealed class ToolCatalog
{
    private readonly Dictionary<string, ToolRegistration> _registrations;

    private ToolCatalog(IEnumerable<ToolRegistration> registrations)
    {
        _registrations = registrations.ToDictionary(
            registration => registration.Descriptor.ToolId,
            registration => registration,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<ToolDescriptor> Descriptors =>
        _registrations.Values.Select(registration => registration.Descriptor).ToArray();

    public static ToolCatalog Create(params ToolRegistration[] registrations) =>
        new(registrations);

    public ToolRegistration? Resolve(string toolId) =>
        _registrations.TryGetValue(toolId, out var registration)
            ? registration
            : null;
}

namespace Agentica.Tools;

public sealed record ToolInputField(
    string Name,
    ToolInputValueType Type = ToolInputValueType.String,
    bool Required = false,
    string? Description = null,
    IReadOnlyList<string>? AllowedValues = null,
    object? Example = null,
    double? Minimum = null,
    double? Maximum = null);

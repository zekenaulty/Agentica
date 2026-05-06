namespace Agentica.Tools;

public sealed record ToolInputSchema(
    IReadOnlyList<ToolInputField> Fields,
    bool AllowAdditionalProperties = false)
{
    public static ToolInputSchema Create(params ToolInputField[] fields) =>
        new(fields);
}

using System.Text.Json;
using Agentica.Execution;
using Agentica.Planning;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Tests;

public sealed class JsonNumberSnapshotTests
{
    [Theory]
    [InlineData(
        "123456789012345678901234567890123456789",
        "123456789012345678901234567890123456790")]
    [InlineData(
        "0.100000000000000000000000000001",
        "0.100000000000000000000000000002")]
    public void Input_digest_distinguishes_json_numbers_beyond_clr_precision(
        string first,
        string second)
    {
        Assert.NotEqual(Digest(first), Digest(second));
    }

    [Theory]
    [InlineData("1e1025")]
    [InlineData("1e-1025")]
    public void Public_validation_fails_closed_for_unsupported_json_number_range(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var step = new PlanStep(
            "step_number",
            "number.validate",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            new Dictionary<string, object?>
            {
                ["value"] = document.RootElement
            });
        var schema = ToolInputSchema.Create(new ToolInputField(
            "value",
            ToolInputValueType.Number,
            Required: true));

        var issues = ToolInputValidator.Validate(step, schema);

        Assert.Contains(issues, issue => issue.Code == "plan.step.input.type");
    }

    private static string Digest(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return ToolInvocationAuthorization.ComputeInputDigest(
            new Dictionary<string, object?>
            {
                ["value"] = document.RootElement
            });
    }
}

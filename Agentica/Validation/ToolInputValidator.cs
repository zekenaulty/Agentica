using System.Collections;
using System.Text.Json;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Validation;

public static class ToolInputValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(PlanStep step, ToolInputSchema? schema)
    {
        if (schema is null)
        {
            return [];
        }

        var issues = new List<ValidationIssue>();
        var fields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);

        foreach (var field in schema.Fields.Where(field => field.Required))
        {
            if (!step.Input.TryGetValue(field.Name, out var value) || value is null)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.required",
                    $"Step '{step.StepId}' is missing required input '{field.Name}'.",
                    step.StepId));
            }
        }

        foreach (var input in step.Input)
        {
            if (!fields.TryGetValue(input.Key, out var field))
            {
                if (!schema.AllowAdditionalProperties)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.unknown",
                        $"Step '{step.StepId}' includes unknown input '{input.Key}'.",
                        step.StepId));
                }

                continue;
            }

            if (input.Value is null)
            {
                continue;
            }

            if (!MatchesType(input.Value, field.Type))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.type",
                    $"Step '{step.StepId}' input '{field.Name}' must be {field.Type}.",
                    step.StepId));
                continue;
            }

            if (field.AllowedValues is { Count: > 0 } allowedValues &&
                !allowedValues.Contains(input.Value.ToString(), StringComparer.Ordinal))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.enum",
                    $"Step '{step.StepId}' input '{field.Name}' has value '{input.Value}' which is not allowed.",
                    step.StepId));
            }

            if ((field.Minimum is not null || field.Maximum is not null) &&
                TryGetNumber(input.Value, out var number))
            {
                if (field.Minimum is { } minimum && number < minimum)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.range",
                        $"Step '{step.StepId}' input '{field.Name}' is below minimum {minimum}.",
                        step.StepId));
                }

                if (field.Maximum is { } maximum && number > maximum)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.range",
                        $"Step '{step.StepId}' input '{field.Name}' is above maximum {maximum}.",
                        step.StepId));
                }
            }
        }

        return issues;
    }

    private static bool MatchesType(object value, ToolInputValueType type) =>
        type switch
        {
            ToolInputValueType.Any => true,
            ToolInputValueType.String => value is string,
            ToolInputValueType.Integer => IsInteger(value),
            ToolInputValueType.Number => TryGetNumber(value, out _),
            ToolInputValueType.Boolean => value is bool,
            ToolInputValueType.Object => value is IReadOnlyDictionary<string, object?> or IDictionary<string, object?> or IDictionary,
            ToolInputValueType.Array => value is not string &&
                                        value is not IReadOnlyDictionary<string, object?> &&
                                        value is not IDictionary<string, object?> &&
                                        value is not IDictionary &&
                                        value is IEnumerable,
            _ => false
        };

    private static bool IsInteger(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong;

    private static bool TryGetNumber(object value, out double number)
    {
        switch (value)
        {
            case byte typed:
                number = typed;
                return true;
            case sbyte typed:
                number = typed;
                return true;
            case short typed:
                number = typed;
                return true;
            case ushort typed:
                number = typed;
                return true;
            case int typed:
                number = typed;
                return true;
            case uint typed:
                number = typed;
                return true;
            case long typed:
                number = typed;
                return true;
            case ulong typed:
                number = typed;
                return true;
            case float typed:
                number = typed;
                return true;
            case double typed:
                number = typed;
                return true;
            case decimal typed:
                number = (double)typed;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return element.TryGetDouble(out number);
            default:
                number = 0;
                return false;
        }
    }
}

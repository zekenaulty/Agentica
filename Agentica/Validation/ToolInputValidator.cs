using System.Collections;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using Agentica.Execution;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Validation;

public static class ToolInputValidator
{
    private const int MaxPublicInputEntries = 16_384;

    public static IReadOnlyList<ValidationIssue> Validate(PlanStep step, ToolInputSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(step);
        var issues = new ValidationIssueCollector();
        var work = new ValidationWorkBudget();

        ToolInputSchema? frozenSchema;
        try
        {
            frozenSchema = schema is null
                ? null
                : ToolManifestCompiler.SnapshotDescriptor(new ToolDescriptor(
                    "tool.input_validation",
                    "Tool input validation",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    InputSchema: schema,
                    RetrySafety: ToolRetrySafety.Idempotent)).InputSchema;
        }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            issues.Add(new ValidationIssue(
                "plan.step.input.schema_invalid",
                "Tool input schema could not be safely snapshotted."));
            return issues.Complete();
        }

        var compiled = CompileSchema(frozenSchema, work, issues);
        if (issues.IsFull)
        {
            return issues.Complete();
        }

        try
        {
            var inputCount = 0;
            foreach (var input in step.Input)
            {
                if (inputCount >= MaxPublicInputEntries)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.snapshot_invalid",
                        "Tool input exceeds the bounded public validation entry limit."));
                    issues.Exhaust();
                    break;
                }

                if (!work.TryConsume(issues))
                {
                    break;
                }

                inputCount++;
                if (input.Value is double number && !double.IsFinite(number) ||
                    input.Value is float single && !float.IsFinite(single))
                {
                    var stepId = ValidationIssueCollector.Display(step.StepId);
                    issues.Add(new ValidationIssue(
                        "plan.step.input.type",
                        $"Step '{stepId}' input '{ValidationIssueCollector.Display(input.Key)}' " +
                        "must be a finite JSON-compatible number.",
                        stepId));
                }
            }
        }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            issues.Add(new ValidationIssue(
                "plan.step.input.snapshot_invalid",
                "Tool input could not be safely enumerated."));
            return issues.Complete();
        }

        var preflightIssues = issues.Complete();
        if (preflightIssues.Count > 0)
        {
            return preflightIssues;
        }

        PlanStep frozenStep;
        try
        {
            frozenStep = ExecutionRecordSnapshot.Plan(new WorkflowPlan(
                "plan.input_validation",
                1,
                [step],
                "Bound public tool-input validation.")).Steps[0];
        }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            issues.Add(new ValidationIssue(
                "plan.step.input.snapshot_invalid",
                "Tool input could not be safely snapshotted."));
            return issues.Complete();
        }

        if (!issues.IsFull)
        {
            ValidateCompiled(frozenStep, compiled, work, issues);
        }

        return issues.Complete();
    }

    internal static ToolInputValidationSchema? CompileSchema(
        ToolInputSchema? schema,
        ValidationWorkBudget work,
        ValidationIssueCollector issues)
    {
        if (schema is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(schema.Fields);
        var fields = new Dictionary<string, ToolInputValidationField>(StringComparer.Ordinal);
        var requiredFields = new List<ToolInputValidationField>();
        foreach (var field in schema.Fields)
        {
            if (!work.TryConsume(issues))
            {
                return null;
            }

            ArgumentNullException.ThrowIfNull(field);
            if (!ValidationIssueCollector.IsDisplayBounded(field.Name))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.schema_identifier_too_long",
                    "Tool input schema contains a field name longer than the validation identifier limit."));
                continue;
            }

            IReadOnlySet<string>? allowedValues = null;
            if (field.AllowedValues is not null)
            {
                var values = new HashSet<string>(StringComparer.Ordinal);
                foreach (var allowedValue in field.AllowedValues)
                {
                    if (!work.TryConsume(issues))
                    {
                        return null;
                    }

                    ArgumentNullException.ThrowIfNull(allowedValue);
                    values.Add(allowedValue);
                }

                allowedValues = values.ToFrozenSet(StringComparer.Ordinal);
            }

            var compiledField = new ToolInputValidationField(field, allowedValues);
            if (!fields.TryAdd(field.Name, compiledField))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.schema_duplicate",
                    $"Tool input schema contains duplicate field '{ValidationIssueCollector.Display(field.Name)}'."));
                continue;
            }

            if (field.Required)
            {
                requiredFields.Add(compiledField);
            }
        }

        return new ToolInputValidationSchema(
            new ReadOnlyDictionary<string, ToolInputValidationField>(fields),
            new ReadOnlyCollection<ToolInputValidationField>(requiredFields),
            schema.AllowAdditionalProperties);
    }

    internal static void ValidateCompiled(
        PlanStep step,
        ToolInputValidationSchema? schema,
        ValidationWorkBudget work,
        ValidationIssueCollector issues)
    {
        if (schema is null)
        {
            return;
        }

        var stepId = ValidationIssueCollector.Display(step.StepId);

        foreach (var field in schema.RequiredFields)
        {
            if (issues.IsFull || !work.TryConsume(issues))
            {
                break;
            }

            if (!step.Input.TryGetValue(field.Field.Name, out var value) || value is null)
            {
                var fieldName = ValidationIssueCollector.Display(field.Field.Name);
                issues.Add(new ValidationIssue(
                    "plan.step.input.required",
                    $"Step '{stepId}' is missing required input '{fieldName}'.",
                    stepId));
            }
        }

        foreach (var input in step.Input)
        {
            if (issues.IsFull || !work.TryConsume(issues))
            {
                break;
            }

            if (!ValidationIssueCollector.IsDisplayBounded(input.Key))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.identifier_too_long",
                    $"Step '{stepId}' contains an input name longer than the validation identifier limit.",
                    stepId));
                continue;
            }

            var inputName = ValidationIssueCollector.Display(input.Key);
            if (!schema.Fields.TryGetValue(input.Key, out var field))
            {
                if (!schema.AllowAdditionalProperties)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.unknown",
                        $"Step '{stepId}' includes unknown input '{inputName}'.",
                        stepId));
                }

                continue;
            }

            if (input.Value is null)
            {
                continue;
            }

            if (!MatchesType(input.Value, field.Field.Type))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.input.type",
                    $"Step '{stepId}' input '{ValidationIssueCollector.Display(field.Field.Name)}' must be {field.Field.Type}.",
                    stepId));
                continue;
            }

            if (field.AllowedValues is { Count: > 0 } allowedValues &&
                !allowedValues.Contains(input.Value.ToString() ?? string.Empty))
            {
                var displayValue = ValidationIssueCollector.Display(input.Value.ToString() ?? string.Empty);
                issues.Add(new ValidationIssue(
                    "plan.step.input.enum",
                    $"Step '{stepId}' input '{ValidationIssueCollector.Display(field.Field.Name)}' has value " +
                    $"'{displayValue}' which is not allowed.",
                    stepId));
            }

            if ((field.Field.Minimum is not null || field.Field.Maximum is not null) &&
                TryGetExactNumber(input.Value, out var number))
            {
                if (field.Field.Minimum is { } minimum &&
                    number.CompareTo(ExactNumber.FromDouble(minimum)) < 0)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.range",
                        $"Step '{stepId}' input '{ValidationIssueCollector.Display(field.Field.Name)}' is below minimum {minimum}.",
                        stepId));
                }

                if (field.Field.Maximum is { } maximum &&
                    number.CompareTo(ExactNumber.FromDouble(maximum)) > 0)
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.input.range",
                        $"Step '{stepId}' input '{ValidationIssueCollector.Display(field.Field.Name)}' is above maximum {maximum}.",
                        stepId));
                }
            }
        }
    }

    private static bool MatchesType(object value, ToolInputValueType type) =>
        type switch
        {
            ToolInputValueType.Any => true,
            ToolInputValueType.String => value is string,
            ToolInputValueType.Integer => IsInteger(value),
            ToolInputValueType.Number => TryGetExactNumber(value, out _),
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
        value is byte or sbyte or short or ushort or int or uint or long or ulong ||
        value is JsonElement { ValueKind: JsonValueKind.Number } element &&
        ExactNumber.TryParseJson(element.GetRawText(), out var number) &&
        number.IsInteger;

    private static bool TryGetExactNumber(object value, out ExactNumber number)
    {
        switch (value)
        {
            case byte typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case sbyte typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case short typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case ushort typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case int typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case uint typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case long typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case ulong typed:
                number = ExactNumber.FromInteger(typed);
                return true;
            case float typed when float.IsFinite(typed):
                number = ExactNumber.FromDouble(typed);
                return true;
            case double typed when double.IsFinite(typed):
                number = ExactNumber.FromDouble(typed);
                return true;
            case decimal typed:
                number = ExactNumber.FromDecimal(typed);
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return ExactNumber.TryParseJson(element.GetRawText(), out number);
            default:
                number = default;
                return false;
        }
    }

    private readonly record struct ExactNumber(BigInteger Numerator, BigInteger Denominator)
    {
        private const int MaxJsonNumberLength = 1_024;
        private const int MaxJsonExponentMagnitude = 1_024;

        public static ExactNumber FromInteger(BigInteger value) =>
            new(value, BigInteger.One);

        public static ExactNumber FromDecimal(decimal value)
        {
            var bits = decimal.GetBits(value);
            var numerator = new BigInteger((uint)bits[0]) |
                            (new BigInteger((uint)bits[1]) << 32) |
                            (new BigInteger((uint)bits[2]) << 64);
            if ((bits[3] & int.MinValue) != 0)
            {
                numerator = -numerator;
            }

            var scale = (bits[3] >> 16) & 0x7f;
            return new ExactNumber(numerator, BigInteger.Pow(10, scale));
        }

        public static ExactNumber FromDouble(double value)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Numeric bounds must be finite.");
            }

            var bits = BitConverter.DoubleToInt64Bits(value);
            var negative = bits < 0;
            var exponentBits = (int)((bits >> 52) & 0x7ffL);
            var fraction = (ulong)bits & 0x000f_ffff_ffff_ffffUL;
            var significand = exponentBits == 0
                ? fraction
                : fraction | (1UL << 52);
            var exponent = exponentBits == 0
                ? -1_074
                : exponentBits - 1_023 - 52;
            var numerator = new BigInteger(significand);
            if (negative)
            {
                numerator = -numerator;
            }

            return exponent >= 0
                ? new ExactNumber(numerator << exponent, BigInteger.One)
                : new ExactNumber(numerator, BigInteger.One << -exponent);
        }

        public static bool TryParseJson(string text, out ExactNumber number)
        {
            if (text.Length == 0 || text.Length > MaxJsonNumberLength)
            {
                number = default;
                return false;
            }

            var index = 0;
            var negative = text[index] == '-';
            if (negative && ++index == text.Length)
            {
                number = default;
                return false;
            }

            var numerator = BigInteger.Zero;
            var integerDigits = 0;
            while (index < text.Length && text[index] is >= '0' and <= '9')
            {
                numerator = (numerator * 10) + (text[index] - '0');
                index++;
                integerDigits++;
            }

            if (integerDigits == 0)
            {
                number = default;
                return false;
            }

            var fractionalDigits = 0;
            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && text[index] is >= '0' and <= '9')
                {
                    numerator = (numerator * 10) + (text[index] - '0');
                    index++;
                    fractionalDigits++;
                }

                if (fractionalDigits == 0)
                {
                    number = default;
                    return false;
                }
            }

            var exponent = 0;
            if (index < text.Length && text[index] is 'e' or 'E')
            {
                index++;
                var exponentNegative = index < text.Length && text[index] == '-';
                if (index < text.Length && text[index] is '+' or '-')
                {
                    index++;
                }

                var exponentDigits = 0;
                while (index < text.Length && text[index] is >= '0' and <= '9')
                {
                    exponent = Math.Min(
                        MaxJsonExponentMagnitude + 1,
                        (exponent * 10) + (text[index] - '0'));
                    index++;
                    exponentDigits++;
                }

                if (exponentDigits == 0 || exponent > MaxJsonExponentMagnitude)
                {
                    number = default;
                    return false;
                }

                if (exponentNegative)
                {
                    exponent = -exponent;
                }
            }

            if (index != text.Length)
            {
                number = default;
                return false;
            }

            if (negative)
            {
                numerator = -numerator;
            }

            var power = exponent - fractionalDigits;
            number = power >= 0
                ? new ExactNumber(numerator * BigInteger.Pow(10, power), BigInteger.One)
                : new ExactNumber(numerator, BigInteger.Pow(10, -power));
            return true;
        }

        public int CompareTo(ExactNumber other) =>
            (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

        public bool IsInteger =>
            Denominator == BigInteger.One ||
            Numerator % Denominator == BigInteger.Zero;
    }
}

internal sealed record ToolInputValidationSchema(
    IReadOnlyDictionary<string, ToolInputValidationField> Fields,
    IReadOnlyList<ToolInputValidationField> RequiredFields,
    bool AllowAdditionalProperties);

internal sealed record ToolInputValidationField(
    ToolInputField Field,
    IReadOnlySet<string>? AllowedValues);

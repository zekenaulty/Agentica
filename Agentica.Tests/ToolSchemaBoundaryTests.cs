using System.Collections;
using System.Text.Json;
using Agentica.Clients.Planning;
using Agentica.Planning;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Tests;

public sealed class ToolSchemaBoundaryTests
{
    [Fact]
    public void Manifest_compiler_rejects_null_blank_and_duplicate_field_names()
    {
        Assert.Throws<ArgumentException>(() => Compile(new ToolInputField(null!)));
        Assert.Throws<ArgumentException>(() => Compile(new ToolInputField(string.Empty)));
        Assert.Throws<ArgumentException>(() => Compile(new ToolInputField("   ")));

        var duplicate = Assert.Throws<ArgumentException>(() => Compile(
            new ToolInputField("value"),
            new ToolInputField("value")));

        Assert.Contains("duplicate field name 'value'", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_compiler_normalizes_field_names_and_rejects_unicode_equivalent_duplicates()
    {
        const string composed = "caf\u00e9";
        const string decomposed = "cafe\u0301";

        var duplicate = Assert.Throws<ArgumentException>(() => Compile(
            new ToolInputField(composed),
            new ToolInputField(decomposed)));
        var manifest = CompileManifest(new ToolInputField(decomposed));

        Assert.Contains("after Unicode normalization", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(
            composed,
            Assert.Single(Assert.Single(manifest.PlannerProjection).InputSchema!.Fields).Name);
    }

    [Fact]
    public void Manifest_compiler_normalizes_tool_ids_and_rejects_unicode_equivalent_duplicates()
    {
        const string composed = "tool.caf\u00e9";
        const string decomposed = "tool.cafe\u0301";
        var tool = new NeverTool();

        var duplicate = Assert.Throws<ArgumentException>(() => ToolManifestCompiler.Compile(
        [
            TestToolRegistration.Create(
                new ToolDescriptor(
                    composed,
                    "Composed",
                    ToolKind.Query,
                    ToolEffect.ReadOnly),
                tool),
            TestToolRegistration.Create(
                new ToolDescriptor(
                    decomposed,
                    "Decomposed",
                    ToolKind.Query,
                    ToolEffect.ReadOnly),
                tool)
        ]));
        var manifest = ToolManifestCompiler.Compile(
        [
            TestToolRegistration.Create(
                new ToolDescriptor(
                    decomposed,
                    "Normalized",
                    ToolKind.Query,
                    ToolEffect.ReadOnly),
                tool)
        ]);

        Assert.Contains("duplicate tool id", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(composed, Assert.Single(manifest.PlannerProjection).ToolId);
    }

    [Fact]
    public void Manifest_compiler_rejects_nonfinite_reversed_and_nonnumeric_bounds()
    {
        var invalidFields = new[]
        {
            new ToolInputField("value", ToolInputValueType.Number, Minimum: double.NaN),
            new ToolInputField("value", ToolInputValueType.Number, Minimum: double.NegativeInfinity),
            new ToolInputField("value", ToolInputValueType.Number, Maximum: double.PositiveInfinity),
            new ToolInputField("value", ToolInputValueType.Number, Minimum: 2, Maximum: 1),
            new ToolInputField("value", ToolInputValueType.String, Minimum: 0)
        };

        foreach (var field in invalidFields)
        {
            Assert.Throws<ArgumentException>(() => Compile(field));
        }
    }

    [Fact]
    public void Manifest_compiler_rejects_undefined_input_value_types()
    {
        var exception = Assert.Throws<ArgumentException>(() => Compile(
            new ToolInputField("value", (ToolInputValueType)int.MaxValue)));

        Assert.Contains("undefined type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_validation_rejects_every_nonfinite_primitive()
    {
        var schema = NumberSchema();

        foreach (var value in new object[]
                 {
                     double.NaN,
                     double.PositiveInfinity,
                     double.NegativeInfinity,
                     float.NaN,
                     float.PositiveInfinity,
                     float.NegativeInfinity
                 })
        {
            var issue = Assert.Single(Validate(value, schema));
            Assert.Equal("plan.step.input.type", issue.Code);
        }
    }

    [Fact]
    public void Numeric_bounds_preserve_integer_decimal_and_json_precision()
    {
        const double largestExactInteger = 9_007_199_254_740_992d;
        var integerSchema = NumberSchema(maximum: largestExactInteger);
        var decimalSchema = NumberSchema(maximum: 0.1d);

        Assert.Empty(Validate(9_007_199_254_740_992L, integerSchema));
        Assert.Contains(
            Validate(9_007_199_254_740_993L, integerSchema),
            issue => issue.Code == "plan.step.input.range");
        Assert.Empty(Validate(0.1m, decimalSchema));
        Assert.Contains(
            Validate(0.100000000000000006m, decimalSchema),
            issue => issue.Code == "plan.step.input.range");

        using var integerJson = JsonDocument.Parse("9007199254740993");
        using var decimalJson = JsonDocument.Parse("0.100000000000000006");
        using var highPrecisionJson = JsonDocument.Parse(
            "0.100000000000000006000000000001");
        Assert.Contains(
            Validate(integerJson.RootElement, integerSchema),
            issue => issue.Code == "plan.step.input.range");
        Assert.Contains(
            Validate(decimalJson.RootElement, decimalSchema),
            issue => issue.Code == "plan.step.input.range");
        Assert.Contains(
            Validate(highPrecisionJson.RootElement, decimalSchema),
            issue => issue.Code == "plan.step.input.range");
    }

    [Fact]
    public void Integer_validation_accepts_exact_json_integer_beyond_uint64()
    {
        using var integerJson = JsonDocument.Parse(
            "123456789012345678901234567890");
        var schema = ToolInputSchema.Create(new ToolInputField(
            "value",
            ToolInputValueType.Integer,
            Required: true));

        Assert.Empty(Validate(integerJson.RootElement, schema));
    }

    [Fact]
    public void Llm_plan_mapping_preserves_exact_noninteger_json_for_schema_validation()
    {
        using var document = JsonDocument.Parse("0.100000000000000006");
        var contract = new WorkflowPlanJsonContract(
            "plan_numeric",
            "Preserve exact planner numbers.",
            [
                new WorkflowPlanStepJsonContract(
                    "step_numeric",
                    "tool.numeric",
                    nameof(ToolKind.Query),
                    nameof(ToolEffect.ReadOnly),
                    new Dictionary<string, JsonElement>
                    {
                        ["value"] = document.RootElement
                    },
                    Reason: null,
                    Intent: null,
                    DependsOn: [],
                    BatchId: null)
            ],
            CompletionCondition: null);

        var step = Assert.Single(contract.ToWorkflowPlan(version: 1).Steps);
        var mapped = Assert.IsType<JsonElement>(step.Input["value"]);

        Assert.Equal("0.100000000000000006", mapped.GetRawText());
        Assert.Contains(
            ToolInputValidator.Validate(step, NumberSchema(maximum: 0.1d)),
            issue => issue.Code == "plan.step.input.range");
    }

    [Fact]
    public void Manifest_compiler_aligns_tool_and_schema_identifiers_to_512_utf8_bytes()
    {
        var oversizedToolId = new string('t', 513);
        var oversizedFieldId = new string('f', 513);

        Assert.Throws<InvalidOperationException>(() => ToolManifestCompiler.Compile(
        [
            TestToolRegistration.Create(
                new ToolDescriptor(
                    oversizedToolId,
                    "Oversized tool id",
                    ToolKind.Query,
                    ToolEffect.ReadOnly),
                new NeverTool())
        ]));
        Assert.Throws<InvalidOperationException>(() => Compile(
            new ToolInputField(oversizedFieldId)));
    }

    [Fact]
    public void Manifest_compiler_enforces_explicit_registration_field_and_allowed_value_caps()
    {
        var registrations = Enumerable.Range(0, 257)
            .Select(index => TestToolRegistration.Create(
                new ToolDescriptor(
                    $"tool.cap.{index:D3}",
                    "Capped tool",
                    ToolKind.Query,
                    ToolEffect.ReadOnly),
                new NeverTool()))
            .ToArray();
        var fields = Enumerable.Range(0, 4_097)
            .Select(index => new ToolInputField($"field_{index:D4}"))
            .ToArray();
        var allowedValues = Enumerable.Range(0, 1_025)
            .Select(index => $"allowed_{index:D4}")
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => ToolManifestCompiler.Compile(registrations));
        Assert.Throws<InvalidOperationException>(() => Compile(fields));
        Assert.Throws<InvalidOperationException>(() => Compile(
            new ToolInputField("value", AllowedValues: allowedValues)));
    }

    [Fact]
    public void Public_input_validation_bounds_dishonest_schema_enumeration()
    {
        var fields = new DishonestReadOnlyList<ToolInputField>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => new ToolInputField($"field_{index:D5}"));
        var issues = ToolInputValidator.Validate(
            new PlanStep(
                "step_schema",
                "tool.schema",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new Dictionary<string, object?>()),
            new ToolInputSchema(fields));

        Assert.Contains(issues, issue => issue.Code == "plan.step.input.schema_invalid");
        Assert.InRange(fields.EnumerationCount, 1, 4_097);
    }

    [Fact]
    public void Public_input_validation_bounds_dishonest_input_enumeration()
    {
        var input = new DishonestReadOnlyDictionary(
            reportedCount: 1,
            yieldedCount: 20_000);
        var issues = ToolInputValidator.Validate(
            new PlanStep(
                "step_input",
                "tool.schema",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                input),
            schema: null);

        Assert.Contains(issues, issue => issue.Code == "plan.step.input.snapshot_invalid");
        Assert.InRange(input.EnumerationCount, 1, 16_385);
    }

    private static ToolInputSchema NumberSchema(double? minimum = null, double? maximum = null) =>
        ToolInputSchema.Create(new ToolInputField(
            "value",
            ToolInputValueType.Number,
            Required: true,
            Minimum: minimum,
            Maximum: maximum));

    private static IReadOnlyList<ValidationIssue> Validate(object value, ToolInputSchema schema) =>
        ToolInputValidator.Validate(
            new PlanStep(
                "step_numeric",
                "tool.numeric",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new Dictionary<string, object?> { ["value"] = value }),
            schema);

    private static void Compile(params ToolInputField[] fields) =>
        _ = CompileManifest(fields);

    private static CompiledToolManifest CompileManifest(params ToolInputField[] fields) =>
        ToolManifestCompiler.Compile(
        [
            TestToolRegistration.Create(
                new ToolDescriptor(
                    "tool.schema",
                    "Schema Tool",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    InputSchema: ToolInputSchema.Create(fields)),
                new NeverTool())
        ]);

    private sealed class NeverTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Schema compilation must not execute the tool.");
    }

    private sealed class DishonestReadOnlyList<T>(
        int reportedCount,
        int yieldedCount,
        Func<int, T> itemFactory) : IReadOnlyList<T>
    {
        public int Count => reportedCount;

        public int EnumerationCount { get; private set; }

        public T this[int index] => itemFactory(index);

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < yieldedCount; index++)
            {
                EnumerationCount++;
                yield return itemFactory(index);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DishonestReadOnlyDictionary(
        int reportedCount,
        int yieldedCount) : IReadOnlyDictionary<string, object?>
    {
        public int Count => reportedCount;

        public int EnumerationCount { get; private set; }

        public IEnumerable<string> Keys => Enumerable.Range(0, yieldedCount)
            .Select(index => $"key_{index:D5}");

        public IEnumerable<object?> Values => Enumerable.Repeat<object?>(null, yieldedCount);

        public object? this[string key] => null;

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out object? value)
        {
            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; index < yieldedCount; index++)
            {
                EnumerationCount++;
                yield return new KeyValuePair<string, object?>($"key_{index:D5}", null);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

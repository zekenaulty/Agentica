using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Execution;

namespace Agentica.Tools;

/// <summary>
/// Validates and deep-snapshots raw registrations, then computes one canonical
/// SHA-256 hash over the complete planner/security/provenance surface.
/// </summary>
public static class ToolManifestCompiler
{
    private const int MaxDepth = 32;
    private const int MaxItems = 16_384;
    private const int MaxNodes = 16_384;
    private const int MaxBytes = 1024 * 1024;
    private const int MaxStringBytes = 256 * 1024;
    private const int MaxIdentifierBytes = 512;
    private const int MaxRegistrations = 256;
    private const int MaxInputFields = 4_096;
    private const int MaxAllowedValues = 1_024;
    private const int MaxToolReferences = 256;

    private static readonly ConditionalWeakTable<CompiledToolManifest, ToolManifestComplexity>
        ManifestComplexities = new();

    public static CompiledToolManifest Compile(IEnumerable<ToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var budget = new SnapshotBudget();
        var sources = SnapshotItems(
            registrations,
            registration => registration,
            budget,
            depth: 0,
            "tool registrations",
            MaxRegistrations);
        if (sources.Any(registration => registration is null))
        {
            throw new ArgumentException("Tool registrations cannot contain null entries.", nameof(registrations));
        }

        var compiled = sources
            .Select(registration => CompileRegistration(registration, budget, depth: 1))
            .ToArray();
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in compiled)
        {
            if (!toolIds.Add(registration.PlannerProjection.ToolId))
            {
                throw new ArgumentException(
                    $"Tool registrations contain duplicate tool id " +
                    $"'{registration.PlannerProjection.ToolId}'.",
                    nameof(registrations));
            }
        }

        var hash = ComputeManifestHash(compiled);
        var manifest = new CompiledToolManifest(hash, compiled);
        ManifestComplexities.Add(manifest, budget.Complexity);
        return manifest;
    }

    internal static ToolManifestComplexity ComplexityOf(CompiledToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!ManifestComplexities.TryGetValue(manifest, out var complexity))
        {
            throw new InvalidOperationException(
                "Compiled tool manifest is missing its bounded complexity measurement.");
        }

        return complexity;
    }

    private static CompiledToolRegistration CompileRegistration(
        ToolRegistration registration,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(registration.Descriptor);
        ArgumentNullException.ThrowIfNull(registration.Tool);
        ArgumentNullException.ThrowIfNull(registration.Security);

        budget.Visit(depth);

        var descriptor = SnapshotDescriptor(registration.Descriptor, budget, depth + 1);
        var security = SnapshotSecurity(registration.Security, budget, depth + 1);

        if (string.IsNullOrWhiteSpace(descriptor.ToolId))
        {
            throw Invalid(descriptor.ToolId, "descriptor ToolId is required");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Name))
        {
            throw Invalid(descriptor.ToolId, "descriptor Name is required");
        }

        if (!Enum.IsDefined(descriptor.Kind))
        {
            throw Invalid(descriptor.ToolId, $"descriptor Kind '{(int)descriptor.Kind}' is undefined");
        }

        if (!Enum.IsDefined(descriptor.Effect))
        {
            throw Invalid(descriptor.ToolId, $"descriptor Effect '{(int)descriptor.Effect}' is undefined");
        }

        if (!Enum.IsDefined(descriptor.RetrySafety))
        {
            throw Invalid(descriptor.ToolId, $"descriptor RetrySafety '{(int)descriptor.RetrySafety}' is undefined");
        }

        if (!Enum.IsDefined(security.Effect))
        {
            throw Invalid(descriptor.ToolId, $"security Effect '{(int)security.Effect}' is undefined");
        }

        if (!Enum.IsDefined(security.ExternalOutput))
        {
            throw Invalid(
                descriptor.ToolId,
                $"security ExternalOutput '{(int)security.ExternalOutput}' is undefined");
        }

        if (!Enum.IsDefined(security.ApprovalRequirement))
        {
            throw Invalid(
                descriptor.ToolId,
                $"security ApprovalRequirement '{(int)security.ApprovalRequirement}' is undefined");
        }

        if (!Enum.IsDefined(security.RetrySafety))
        {
            throw Invalid(descriptor.ToolId, $"security RetrySafety '{(int)security.RetrySafety}' is undefined");
        }

        if (!Enum.IsDefined(security.Provenance.Kind))
        {
            throw Invalid(
                descriptor.ToolId,
                $"security Provenance.Kind '{(int)security.Provenance.Kind}' is undefined");
        }

        if (descriptor.Effect == ToolEffect.Unknown)
        {
            throw Invalid(descriptor.ToolId, "descriptor Effect cannot be Unknown");
        }

        if (security.Effect == ToolEffect.Unknown)
        {
            throw Invalid(descriptor.ToolId, "security Effect cannot be Unknown");
        }

        if (security.ExternalOutput == ToolExternalOutputClassification.Unknown)
        {
            throw Invalid(descriptor.ToolId, "security ExternalOutput cannot be Unknown");
        }

        if (security.ApprovalRequirement == ToolApprovalRequirement.Unknown)
        {
            throw Invalid(descriptor.ToolId, "security ApprovalRequirement cannot be Unknown");
        }

        if (security.RetrySafety == ToolRetrySafety.Unknown)
        {
            throw Invalid(descriptor.ToolId, "security RetrySafety cannot be Unknown");
        }

        if (security.Provenance.Kind == ToolProvenanceKind.Unknown)
        {
            throw Invalid(descriptor.ToolId, "security Provenance.Kind cannot be Unknown");
        }

        if (string.IsNullOrWhiteSpace(security.Provenance.Source))
        {
            throw Invalid(descriptor.ToolId, "security Provenance.Source is required");
        }

        if (security.Reads.Any(boundary => !Enum.IsDefined(boundary)) ||
            security.ExposesToPlanner.Any(boundary => !Enum.IsDefined(boundary)))
        {
            throw Invalid(descriptor.ToolId, "security boundary sets cannot contain undefined values");
        }

        if (security.Reads.Contains(ToolDataBoundary.Unknown) ||
            security.ExposesToPlanner.Contains(ToolDataBoundary.Unknown))
        {
            throw Invalid(descriptor.ToolId, "security boundary sets cannot contain Unknown");
        }

        if (descriptor.Effect != security.Effect)
        {
            throw Invalid(descriptor.ToolId, "descriptor Effect does not match authoritative security Effect");
        }

        var securityRequiresApproval = security.ApprovalRequirement != ToolApprovalRequirement.None;
        if (descriptor.RequiresApproval != securityRequiresApproval)
        {
            throw Invalid(descriptor.ToolId, "descriptor RequiresApproval does not match security ApprovalRequirement");
        }

        if (descriptor.RetrySafety != security.RetrySafety)
        {
            throw Invalid(descriptor.ToolId, "descriptor RetrySafety does not match authoritative security RetrySafety");
        }

        return new CompiledToolRegistration(descriptor, security, registration.Tool);
    }

    internal static ToolDescriptor SnapshotDescriptor(ToolDescriptor descriptor) =>
        SnapshotDescriptor(descriptor, new SnapshotBudget(), depth: 0);

    internal static IReadOnlyList<ToolDescriptor> SnapshotDescriptors(
        IEnumerable<ToolDescriptor> descriptors)
    {
        var budget = new SnapshotBudget();
        return SnapshotItems(
            descriptors,
            descriptor => SnapshotDescriptor(descriptor, budget, depth: 1),
            budget,
            depth: 0,
            "tool descriptors");
    }

    private static ToolDescriptor SnapshotDescriptor(
        ToolDescriptor descriptor,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        budget.Visit(depth);
        return
        new(
            budget.Identifier(descriptor.ToolId, "tool id"),
            budget.Text(descriptor.Name, "tool name"),
            descriptor.Kind,
            descriptor.Effect,
            descriptor.RequiresApproval,
            SnapshotSchema(descriptor.InputSchema, budget, depth + 1),
            budget.OptionalText(descriptor.Description, "tool description"),
            SnapshotContextHint(descriptor.ContextHint, budget, depth + 1),
            SnapshotCooldown(descriptor.Cooldown, budget, depth + 1),
            descriptor.RetrySafety);
    }

    private static ToolInputSchema? SnapshotSchema(
        ToolInputSchema? schema,
        SnapshotBudget budget,
        int depth)
    {
        if (schema is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(schema.Fields);
        budget.Visit(depth);
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        var fields = SnapshotItems(
            schema.Fields,
            field =>
            {
                ArgumentNullException.ThrowIfNull(field);
                budget.Visit(depth + 2);
                if (string.IsNullOrWhiteSpace(field.Name))
                {
                    throw new ArgumentException("Tool input schema field names are required.");
                }

                budget.PreflightIdentifier(field.Name, "input field name");
                if (!Enum.IsDefined(field.Type))
                {
                    throw new ArgumentException(
                        "Tool input schema contains a field with an undefined type value.");
                }

                var normalizedName = budget.Identifier(Normalize(field.Name), "input field name");
                if (!fieldNames.Add(normalizedName))
                {
                    throw new ArgumentException(
                        $"Tool input schema contains duplicate field name '{normalizedName}' after Unicode normalization.");
                }

                if (field.Minimum is { } minimum && !double.IsFinite(minimum))
                {
                    throw new ArgumentException(
                        $"Tool input schema field '{normalizedName}' minimum must be finite.");
                }

                if (field.Maximum is { } maximum && !double.IsFinite(maximum))
                {
                    throw new ArgumentException(
                        $"Tool input schema field '{normalizedName}' maximum must be finite.");
                }

                if (field.Minimum is { } lower && field.Maximum is { } upper && lower > upper)
                {
                    throw new ArgumentException(
                        $"Tool input schema field '{normalizedName}' minimum cannot exceed its maximum.");
                }

                if ((field.Minimum is not null || field.Maximum is not null) &&
                    field.Type is not (ToolInputValueType.Integer or ToolInputValueType.Number))
                {
                    throw new ArgumentException(
                        $"Tool input schema field '{normalizedName}' can only declare bounds for numeric types.");
                }

                var allowedValues = field.AllowedValues is null
                    ? null
                    : SnapshotStrings(
                        field.AllowedValues,
                        budget,
                        depth + 3,
                        "input allowed values",
                        MaxAllowedValues);
                return new ToolInputField(
                    normalizedName,
                    field.Type,
                    field.Required,
                    budget.OptionalText(field.Description, "input field description"),
                    allowedValues,
                    SnapshotExample(field.Example, budget, depth + 3),
                    field.Minimum,
                    field.Maximum);
            },
            budget,
            depth + 1,
            "tool input fields",
            MaxInputFields);

        return new ToolInputSchema(
            fields,
            schema.AllowAdditionalProperties);
    }

    private static object? SnapshotExample(
        object? example,
        SnapshotBudget budget,
        int depth)
    {
        if (example is null)
        {
            return null;
        }

        if (example is JsonElement { ValueKind: JsonValueKind.Undefined })
        {
            budget.Visit(depth);
            budget.Consume(4, "null input example");
            return null;
        }

        var wrapped = ToolResultNormalizer.SnapshotStructuredData(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["example"] = example
            });
        var canonical = wrapped["example"];
        budget.Structured(canonical, depth);
        var element = JsonSerializer.SerializeToElement(canonical);

        return element.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => element.Clone(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetUInt64(out var integer) => integer,
            JsonValueKind.Number => element.Clone(),
            _ => element.Clone()
        };
    }

    private static ToolContextHint? SnapshotContextHint(
        ToolContextHint? hint,
        SnapshotBudget budget,
        int depth)
    {
        if (hint is null)
        {
            return null;
        }

        budget.Visit(depth);
        return new ToolContextHint(
            budget.Text(hint.Produces, "context-hint production"),
            SnapshotIdentifiers(
                hint.Complements,
                budget,
                depth + 1,
                "complementary tool ids",
                MaxToolReferences),
            SnapshotIdentifiers(
                hint.CanBatchWith,
                budget,
                depth + 1,
                "batch-compatible tool ids",
                MaxToolReferences),
            SnapshotIdentifiers(
                hint.ShouldPrecede,
                budget,
                depth + 1,
                "preceded tool ids",
                MaxToolReferences))
        {
            UseWhen = budget.OptionalText(hint.UseWhen, "context-hint use condition"),
            NotEnoughWhen = budget.OptionalText(hint.NotEnoughWhen, "context-hint insufficiency condition")
        };
    }

    private static ToolCooldownPolicy? SnapshotCooldown(
        ToolCooldownPolicy? cooldown,
        SnapshotBudget budget,
        int depth)
    {
        if (cooldown is null)
        {
            return null;
        }

        budget.Visit(depth);
        return new ToolCooldownPolicy(
            cooldown.PlanStepCount,
            cooldown.Duration,
            cooldown.ScopeInputKeys is null
                ? null
                : SnapshotStrings(
                    cooldown.ScopeInputKeys,
                    budget,
                    depth + 1,
                    "cooldown scope keys",
                    MaxInputFields,
                    identifiers: true),
            budget.OptionalText(cooldown.Reason, "cooldown reason"),
            cooldown.ResetOnMutation);
    }

    private static ToolSecurityDeclaration SnapshotSecurity(
        ToolSecurityDeclaration security,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(security.Provenance);
        budget.Visit(depth);
        var snapshot = new ToolSecurityDeclaration(
            security.Effect,
            security.Reads,
            security.ExposesToPlanner,
            security.ExternalOutput,
            security.ApprovalRequirement,
            security.RetrySafety,
            new ToolProvenance(
                security.Provenance.Kind,
                budget.Text(security.Provenance.Source, "tool provenance source"),
                budget.OptionalText(security.Provenance.Version, "tool provenance version")));
        foreach (var boundary in snapshot.Reads)
        {
            budget.Visit(depth + 1);
            budget.Text(boundary.ToString(), "tool read boundary");
        }

        foreach (var boundary in snapshot.ExposesToPlanner)
        {
            budget.Visit(depth + 1);
            budget.Text(boundary.ToString(), "tool planner-output boundary");
        }

        return snapshot;
    }

    private static IReadOnlyList<string> SnapshotStrings(
        IEnumerable<string> values,
        SnapshotBudget budget,
        int depth,
        string description,
        int maximumItems = MaxItems,
        bool identifiers = false) =>
        SnapshotItems(
            values,
            value =>
            {
                budget.Visit(depth + 1);
                return identifiers
                    ? budget.Identifier(value, description)
                    : budget.Text(value, description);
            },
            budget,
            depth,
            description,
            maximumItems);

    private static IReadOnlyList<string> SnapshotIdentifiers(
        IEnumerable<string> values,
        SnapshotBudget budget,
        int depth,
        string description,
        int maximumItems) =>
        SnapshotStrings(
            values,
            budget,
            depth,
            description,
            maximumItems,
            identifiers: true);

    private static IReadOnlyList<TResult> SnapshotItems<TSource, TResult>(
        IEnumerable<TSource> values,
        Func<TSource, TResult> snapshot,
        SnapshotBudget budget,
        int depth,
        string description,
        int maximumItems = MaxItems)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(snapshot);
        budget.Visit(depth);
        var result = new List<TResult>();
        foreach (var value in values)
        {
            if (result.Count >= maximumItems)
            {
                throw new InvalidOperationException(
                    $"{description} exceeds the maximum of {maximumItems} items.");
            }

            result.Add(snapshot(value));
        }

        return new ReadOnlyCollection<TResult>(result);
    }

    private static string ComputeManifestHash(IReadOnlyList<CompiledToolRegistration> registrations)
    {
        var model = registrations
            .OrderBy(registration => registration.PlannerProjection.ToolId, StringComparer.Ordinal)
            .Select(ManifestRegistration)
            .ToArray();
        var element = JsonSerializer.SerializeToElement(model);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, element);
        }

        var digest = Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        return $"sha256-v1:{digest}";
    }

    private static object ManifestRegistration(CompiledToolRegistration registration)
    {
        var descriptor = registration.PlannerProjection;
        var security = registration.Security;
        return new
        {
            descriptor = new
            {
                descriptor.ToolId,
                descriptor.Name,
                kind = descriptor.Kind.ToString(),
                effect = descriptor.Effect.ToString(),
                descriptor.RequiresApproval,
                inputSchema = descriptor.InputSchema is null
                    ? null
                    : new
                    {
                        descriptor.InputSchema.AllowAdditionalProperties,
                        fields = descriptor.InputSchema.Fields.Select(field => new
                        {
                            field.Name,
                            type = field.Type.ToString(),
                            field.Required,
                            field.Description,
                            field.AllowedValues,
                            field.Example,
                            field.Minimum,
                            field.Maximum
                        }).ToArray()
                    },
                descriptor.Description,
                contextHint = descriptor.ContextHint is null
                    ? null
                    : new
                    {
                        descriptor.ContextHint.Produces,
                        descriptor.ContextHint.Complements,
                        descriptor.ContextHint.CanBatchWith,
                        descriptor.ContextHint.ShouldPrecede,
                        descriptor.ContextHint.UseWhen,
                        descriptor.ContextHint.NotEnoughWhen
                    },
                cooldown = descriptor.Cooldown is null
                    ? null
                    : new
                    {
                        descriptor.Cooldown.PlanStepCount,
                        durationTicks = descriptor.Cooldown.Duration?.Ticks,
                        descriptor.Cooldown.ScopeInputKeys,
                        descriptor.Cooldown.Reason,
                        descriptor.Cooldown.ResetOnMutation
                    },
                retrySafety = descriptor.RetrySafety.ToString()
            },
            security = new
            {
                effect = security.Effect.ToString(),
                reads = security.Reads.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray(),
                exposesToPlanner = security.ExposesToPlanner.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray(),
                externalOutput = security.ExternalOutput.ToString(),
                approvalRequirement = security.ApprovalRequirement.ToString(),
                retrySafety = security.RetrySafety.ToString(),
                provenance = new
                {
                    kind = security.Provenance.Kind.ToString(),
                    security.Provenance.Source,
                    security.Provenance.Version
                }
            }
        };
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(Normalize(property.Name));
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Normalize(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);

    private static ArgumentException Invalid(string? toolId, string reason) =>
        new($"Tool registration '{toolId ?? "<null>"}' is invalid: {reason}.");

    private sealed class SnapshotBudget
    {
        private int _remainingNodes = MaxNodes;
        private int _remainingBytes = MaxBytes;

        public ToolManifestComplexity Complexity =>
            new(MaxNodes - _remainingNodes, MaxBytes - _remainingBytes);

        public void Visit(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException(
                    $"Tool manifest exceeds the maximum depth of {MaxDepth}.");
            }

            if (_remainingNodes <= 0)
            {
                throw new InvalidOperationException(
                    $"Tool manifest exceeds the global maximum of {MaxNodes} nodes.");
            }

            _remainingNodes--;
        }

        public string Text(string value, string description)
        {
            var bytes = PreflightText(value, description);

            Consume(bytes, description);
            return value;
        }

        public int PreflightText(string value, string description)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > MaxStringBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-manifest {description} exceeds the maximum of {MaxStringBytes} UTF-8 bytes.");
            }

            var bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxStringBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-manifest {description} exceeds the maximum of {MaxStringBytes} UTF-8 bytes.");
            }

            return bytes;
        }

        public string Identifier(string value, string description)
        {
            PreflightIdentifier(value, description);
            var normalized = Normalize(value);
            var bytes = PreflightIdentifier(normalized, description);
            Consume(bytes, description);
            return normalized;
        }

        public int PreflightIdentifier(string value, string description)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > MaxIdentifierBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-manifest {description} exceeds the maximum of " +
                    $"{MaxIdentifierBytes} UTF-8 bytes.");
            }

            var bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxIdentifierBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-manifest {description} exceeds the maximum of " +
                    $"{MaxIdentifierBytes} UTF-8 bytes.");
            }

            return bytes;
        }

        public string? OptionalText(string? value, string description) =>
            value is null ? null : Text(value, description);

        public void Structured(object? value, int depth)
        {
            Visit(depth);
            switch (value)
            {
                case null:
                    Consume(4, "null structured value");
                    return;
                case string text:
                    Text(text, "structured string");
                    return;
                case IReadOnlyDictionary<string, object?> dictionary:
                    var dictionaryItems = 0;
                    foreach (var pair in dictionary)
                    {
                        if (dictionaryItems >= MaxItems)
                        {
                            throw new InvalidOperationException(
                                $"Tool-manifest structured data exceeds the maximum of {MaxItems} entries.");
                        }

                        Text(pair.Key, "structured-data key");
                        Structured(pair.Value, depth + 1);
                        dictionaryItems++;
                    }

                    return;
                case IEnumerable sequence:
                    var sequenceItems = 0;
                    foreach (var item in sequence)
                    {
                        if (sequenceItems >= MaxItems)
                        {
                            throw new InvalidOperationException(
                                $"Tool-manifest structured data exceeds the maximum of {MaxItems} items.");
                        }

                        Structured(item, depth + 1);
                        sequenceItems++;
                    }

                    return;
                case bool boolean:
                    Consume(boolean ? 4 : 5, "Boolean structured value");
                    return;
                case byte or sbyte or short or ushort or int or uint or long or ulong:
                    Text(Convert.ToString(value, CultureInfo.InvariantCulture)!, "integer structured value");
                    return;
                case float single when float.IsFinite(single):
                    Text(single.ToString("R", CultureInfo.InvariantCulture), "numeric structured value");
                    return;
                case double number when double.IsFinite(number):
                    Text(number.ToString("R", CultureInfo.InvariantCulture), "numeric structured value");
                    return;
                case decimal number:
                    Text(number.ToString(CultureInfo.InvariantCulture), "numeric structured value");
                    return;
                case JsonElement { ValueKind: JsonValueKind.Number } jsonNumber:
                    Text(jsonNumber.GetRawText(), "exact JSON numeric structured value");
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Canonical tool-manifest data contains unsupported value type '{value.GetType().FullName}'.");
            }
        }

        public void Consume(int bytes, string description)
        {
            if (bytes < 0 || bytes > _remainingBytes)
            {
                throw new InvalidOperationException(
                    $"Tool-manifest {description} exceeds the global maximum of {MaxBytes} bytes.");
            }

            _remainingBytes -= bytes;
        }
    }
}

internal sealed record ToolManifestComplexity(int Nodes, int Utf8Bytes);

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.Tools;

/// <summary>
/// Validates and deep-snapshots raw registrations, then computes one canonical
/// SHA-256 hash over the complete planner/security/provenance surface.
/// </summary>
public static class ToolManifestCompiler
{
    public static CompiledToolManifest Compile(IEnumerable<ToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var sources = registrations.ToArray();
        if (sources.Any(registration => registration is null))
        {
            throw new ArgumentException("Tool registrations cannot contain null entries.", nameof(registrations));
        }

        var duplicateIds = sources
            .GroupBy(registration => registration.Descriptor?.ToolId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new ArgumentException(
                $"Tool registrations contain duplicate tool id '{duplicateIds[0]}'.",
                nameof(registrations));
        }

        var compiled = sources
            .Select(CompileRegistration)
            .ToArray();
        var hash = ComputeManifestHash(compiled);
        return new CompiledToolManifest(hash, compiled);
    }

    private static CompiledToolRegistration CompileRegistration(ToolRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration.Descriptor);
        ArgumentNullException.ThrowIfNull(registration.Tool);
        ArgumentNullException.ThrowIfNull(registration.Security);

        var descriptor = registration.Descriptor;
        var security = registration.Security;

        if (string.IsNullOrWhiteSpace(descriptor.ToolId))
        {
            throw Invalid(descriptor.ToolId, "descriptor ToolId is required");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Name))
        {
            throw Invalid(descriptor.ToolId, "descriptor Name is required");
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

        var plannerProjection = SnapshotDescriptor(descriptor);
        var securitySnapshot = new ToolSecurityDeclaration(
            security.Effect,
            security.Reads,
            security.ExposesToPlanner,
            security.ExternalOutput,
            security.ApprovalRequirement,
            security.RetrySafety,
            security.Provenance with { });

        return new CompiledToolRegistration(plannerProjection, securitySnapshot, registration.Tool);
    }

    private static ToolDescriptor SnapshotDescriptor(ToolDescriptor descriptor) =>
        new(
            descriptor.ToolId,
            descriptor.Name,
            descriptor.Kind,
            descriptor.Effect,
            descriptor.RequiresApproval,
            SnapshotSchema(descriptor.InputSchema),
            descriptor.Description,
            SnapshotContextHint(descriptor.ContextHint),
            SnapshotCooldown(descriptor.Cooldown),
            descriptor.RetrySafety);

    private static ToolInputSchema? SnapshotSchema(ToolInputSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(schema.Fields);
        var fields = schema.Fields.Select(field =>
        {
            ArgumentNullException.ThrowIfNull(field);
            var allowedValues = field.AllowedValues is null
                ? null
                : new ReadOnlyCollection<string>(field.AllowedValues.ToArray());
            return new ToolInputField(
                field.Name,
                field.Type,
                field.Required,
                field.Description,
                allowedValues,
                SnapshotExample(field.Example),
                field.Minimum,
                field.Maximum);
        }).ToArray();

        return new ToolInputSchema(
            new ReadOnlyCollection<ToolInputField>(fields),
            schema.AllowAdditionalProperties);
    }

    private static object? SnapshotExample(object? example)
    {
        if (example is null)
        {
            return null;
        }

        var element = example is JsonElement jsonElement
            ? jsonElement.Clone()
            : JsonSerializer.SerializeToElement(example);

        return element.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => element.Clone(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => element.GetDouble(),
            _ => element.Clone()
        };
    }

    private static ToolContextHint? SnapshotContextHint(ToolContextHint? hint) =>
        hint is null
            ? null
            : new ToolContextHint(
                hint.Produces,
                SnapshotStrings(hint.Complements),
                SnapshotStrings(hint.CanBatchWith),
                SnapshotStrings(hint.ShouldPrecede))
            {
                UseWhen = hint.UseWhen,
                NotEnoughWhen = hint.NotEnoughWhen
            };

    private static ToolCooldownPolicy? SnapshotCooldown(ToolCooldownPolicy? cooldown) =>
        cooldown is null
            ? null
            : new ToolCooldownPolicy(
                cooldown.PlanStepCount,
                cooldown.Duration,
                cooldown.ScopeInputKeys is null ? null : SnapshotStrings(cooldown.ScopeInputKeys),
                cooldown.Reason,
                cooldown.ResetOnMutation);

    private static IReadOnlyList<string> SnapshotStrings(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<string>(values.ToArray());
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
}

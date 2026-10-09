using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Tools;

namespace Agentica.Lab.Web.Contracts;

public sealed class HostProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ProtocolValidation
{
    public static void Validate(HostRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != HostProtocol.Version) Fail("protocol.version", "Unsupported protocol version.");
        foreach (var value in new[] { request.HostId, request.SessionId, request.SessionEpoch,
                     request.ScopeId, request.PerspectiveId, request.ObjectiveId }) Identifier(value);
        Text(request.Objective, 8000, "objective");
        Observation(request.Observation);
        if (request.Capabilities is null || request.Capabilities.Count is < 1 or > 32)
            Fail("capabilities.bounds", "Bind between one and 32 capabilities.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in request.Capabilities!)
        {
            if (capability is null) Fail("capability.invalid", "A capability is missing.");
            Identifier(capability!.Id);
            if (capability.Id.StartsWith("lab.", StringComparison.Ordinal) || !ids.Add(capability.Id))
                Fail("capability.id", "Capability ids must be unique and cannot use the lab. namespace.");
            Text(capability.Name, 128, "capability name");
            Text(capability.Description, 2000, "capability description");
            if (!Enum.IsDefined(capability.Kind) || !Enum.IsDefined(capability.Effect) || capability.Effect == ToolEffect.Unknown)
                Fail("capability.classification", "Declare a known capability kind and effect.");
            if (capability.InputSchema?.Fields is null || capability.InputSchema.Fields.Count > 32)
                Fail("capability.schema", "A bounded input schema is required.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in capability.InputSchema!.Fields)
            {
                if (field is null) Fail("capability.schema", "A schema field is missing.");
                Identifier(field!.Name);
                if (!names.Add(field.Name) || !Enum.IsDefined(field.Type) || field.AllowedValues?.Count > 64)
                    Fail("capability.schema", "Invalid or duplicate input field.");
            }
        }
        var limits = request.Limits ?? new HostRunLimits();
        if (limits.MaxSteps is < 1 or > 128 || limits.MaxRefinements is < 0 or > 128 ||
            limits.MaxPlanContinuations is < 0 or > 64 || limits.TimeoutSeconds is < 1 or > 3600 ||
            limits.ActionTimeoutSeconds is < 1 or > 300 || limits.MaxRecentObservations is < 1 or > 32 ||
            limits.MaxRecentReceipts is < 1 or > 32)
            Fail("limits.invalid", "Run limits exceed the supported bounds.");
        if (JsonSerializer.SerializeToUtf8Bytes(request, HostProtocol.Json).Length > HostProtocol.MaxMessageBytes)
            Fail("request.bounds", "Run request is too large.");
    }

    public static void Observation(HostObservation observation)
    {
        if (observation is null) Fail("observation.required", "A scoped observation is required.");
        Identifier(observation!.ObservationId);
        if (observation.Revision < 0 || observation.ObservedAt == default ||
            observation.Data.ValueKind != JsonValueKind.Object || observation.Facts?.Count > 64 ||
            JsonSerializer.SerializeToUtf8Bytes(observation, HostProtocol.Json).Length > 65_536)
            Fail("observation.bounds", "Invalid or oversized scoped observation.");
    }

    public static void Identifier(string value)
    {
        Text(value, 128, "identifier");
        if (value.Any(char.IsControl)) Fail("input.invalid", "Identifiers cannot contain control characters.");
    }

    public static void Text(string value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            Fail("input.invalid", $"Invalid {field}.");
    }

    public static string Digest(object value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, JsonSerializer.SerializeToElement(value, HostProtocol.Json));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static void Fail(string code, string message) => throw new HostProtocolException(code, message);

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray()) WriteCanonical(writer, element);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

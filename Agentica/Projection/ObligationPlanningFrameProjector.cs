using Agentica.Planning;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.Projection;

/// <summary>Projects current host-owned obligation evidence through the existing frame boundary.</summary>
public sealed class ObligationPlanningFrameProjector : IPlanningFrameProjector
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Func<PlanningFrameProjectionRequest, ObligationFulfillment> _hostState;
    private readonly int _maximumPayloadBytes;

    public ObligationPlanningFrameProjector(
        Func<PlanningFrameProjectionRequest, ObligationFulfillment> hostState,
        int maximumPayloadBytes = 256 * 1024)
    {
        ArgumentNullException.ThrowIfNull(hostState);
        if (maximumPayloadBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        _hostState = hostState;
        _maximumPayloadBytes = maximumPayloadBytes;
    }

    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = _hostState(request) ??
            throw new InvalidOperationException("The host supplied no obligation state.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions);
        if (bytes.Length > _maximumPayloadBytes)
            throw new InvalidOperationException("Obligation planning frame exceeds its payload budget.");
        using var json = JsonDocument.Parse(bytes);
        var payload = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = ObligationInputManifest.SchemaVersion,
            ["fulfillment"] = json.RootElement.Clone()
        });
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (payloadBytes.Length > _maximumPayloadBytes)
            throw new InvalidOperationException("Obligation planning frame exceeds its payload budget.");
        var references = Array.AsReadOnly(current.Manifest.PresentedEvidence
            .Select(segment => segment.Source with { }).Distinct().ToArray());
        return Array.AsReadOnly(new[]
        {
            new PlanningFrame("obligation:" + Convert.ToHexStringLower(SHA256.HashData(payloadBytes)),
                "obligation-activation", "1", DateTimeOffset.UtcNow, payload, references)
            {
                ToolSurfaceId = request.ToolSurface?.SurfaceId
            }
        });
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}


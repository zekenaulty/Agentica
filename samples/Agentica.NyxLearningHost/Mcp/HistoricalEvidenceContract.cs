using Agentica.Mcp;
using Agentica.Projection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.NyxLearningHost.Mcp;

public sealed record HistoricalEvidenceRequest(
    string SegmentId, string SourceId, string SourceRevision, string ScopeId,
    string PerspectiveId, int StartUtf16, int LengthUtf16);

public enum HistoricalSourceReadState { Available, Unavailable, Error }

public sealed record HistoricalSourceRead(
    HistoricalSourceReadState State, PresentedEvidenceSegment? Evidence = null);

public sealed record HistoricalEvidenceResult(
    int SchemaVersion, string OutputRevision, string ManifestContentIdentity,
    ObligationResolutionState Resolution, string ReasonCode,
    HistoricalEvidenceRequest Request, PresentedEvidenceSegment? Evidence,
    ObligationCoverage Coverage);

/// <summary>Sample-owned semantic metadata, not additional MCP permission fields.</summary>
public sealed record HistoricalFacultySemantics(
    string Requires, string Produces, string Applies, string Effects);

public static class HistoricalEvidenceContract
{
    public const string ServerId = "learning.history";
    public const string ImplementationName = "agentica-historical-evidence-fixture";
    public const string ImplementationRevision = "1.0.0";
    public const string ProtocolVersion = "2026-07-28";
    public const string SdkPackageVersion = "2.2.0";
    public const string ToolId = "history.read_exact";
    public const string RemoteName = "read_exact";
    public const string DescriptorRevision = "history.descriptor.v1";
    public const string InputRevision = "history.read.input.v1";
    public const string OutputRevision = "history.read.output.v1";

    public const string InputSchemaJson = """
        {"type":"object","properties":{
          "segmentId":{"type":"string"},"sourceId":{"type":"string"},
          "sourceRevision":{"type":"string"},"scopeId":{"type":"string"},
          "perspectiveId":{"type":"string"},"startUtf16":{"type":"integer","minimum":0},
          "lengthUtf16":{"type":"integer","minimum":1}},
         "required":["segmentId","sourceId","sourceRevision","scopeId","perspectiveId","startUtf16","lengthUtf16"],
         "additionalProperties":false}
        """;

    public const string OutputSchemaJson = """
        {"type":"object","properties":{
          "schemaVersion":{"type":"integer","const":1},
          "outputRevision":{"type":"string","const":"history.read.output.v1"},
          "manifestContentIdentity":{"type":"string"},
          "resolution":{"type":"string","enum":["Ready","Unavailable","Indeterminate"]},
          "reasonCode":{"type":"string"},"request":{"type":"object"},
          "evidence":{"type":["object","null"]},"coverage":{"type":"object"}},
         "required":["schemaVersion","outputRevision","manifestContentIdentity","resolution",
                     "reasonCode","request","evidence","coverage"],"additionalProperties":false}
        """;

    public static HistoricalFacultySemantics Semantics { get; } = new(
        "Exact installed source/segment revision, full UTF-16 cut, scope and perspective; current host scope.",
        "Versioned exact attributable evidence and declared coverage, or typed unavailable/error without evidence.",
        "One host-installed immutable historical cut; no cross-conversation or arbitrary-source lookup.",
        "ReadOnly; no memory promotion, source mutation, speech or domain effect.");

    public static string InputSchemaSha256 { get; } = McpSchemaFingerprint.Sha256(InputSchemaJson);
    public static string OutputSchemaSha256 { get; } = McpSchemaFingerprint.Sha256(OutputSchemaJson);

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(HistoricalEvidenceResult result) => JsonSerializer.Serialize(result, WireOptions);

    public static HistoricalEvidenceResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 262_144) throw new ArgumentException("Historical result exceeds its wire bound.", nameof(json));
        return JsonSerializer.Deserialize<HistoricalEvidenceResult>(json, WireOptions)
            ?? throw new ArgumentException("Historical result was null.", nameof(json));
    }

    public static IReadOnlyDictionary<string, object?> Arguments(HistoricalEvidenceRequest request) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["segmentId"] = request.SegmentId,
            ["sourceId"] = request.SourceId,
            ["sourceRevision"] = request.SourceRevision,
            ["scopeId"] = request.ScopeId,
            ["perspectiveId"] = request.PerspectiveId,
            ["startUtf16"] = request.StartUtf16,
            ["lengthUtf16"] = request.LengthUtf16
        };
}

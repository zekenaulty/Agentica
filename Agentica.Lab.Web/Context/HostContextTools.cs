using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Lab.Web.Contracts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.Lab.Web.Context;

public sealed partial class HostContextSession
{
    public IReadOnlyList<ToolRegistration> CreateTools() =>
    [
        Registration("lab.evidence.read", "Read exact host observation", ToolKind.Query, ToolEffect.ReadOnly,
            "Read one exact retained observation by ID. A content hash can be supplied to bind the read to a specific source. Unknown and retention-pruned observations are explicitly unavailable.",
            ToolInputSchema.Create(
                new("observationId", Required: true),
                new("contentHash", Description: "Optional expected source hash from the planning frame.")),
            ReadEvidenceTool),
        Registration("lab.knowledge.query", "Query retained knowledge", ToolKind.Query, ToolEffect.ReadOnly,
            "Retrieve bounded pages of current retained knowledge, including omitted planning facts and their source references. Query an exact key or prefix, optionally filter state. Retention losses and remaining matches are explicit; cursors reject changed knowledge snapshots.",
            ToolInputSchema.Create(
                new("key", Description: "Optional exact knowledge key; mutually exclusive with keyPrefix."),
                new("keyPrefix", Description: "Optional ordinal key prefix; mutually exclusive with key."),
                new("state", AllowedValues: ["observed", "inferred", "supported", "refuted", "stale"]),
                new("limit", ToolInputValueType.Integer, Minimum: 1, Maximum: 32, Description: "Maximum entries, default 16."),
                new("cursor", Description: "Continuation cursor returned by the previous page with the same filters.")),
            QueryKnowledgeTool),
        Registration("lab.hypothesis.record", "Record a sourced hypothesis", ToolKind.PlannerAssist, ToolEffect.WritesLocalState,
            "Record or revise a model hypothesis with retained observation evidence. State is inferred, refuted or stale. This does not alter host observations, authorize actions, or establish completion.",
            ToolInputSchema.Create(
                new("key", Required: true), new("summary", Required: true),
                new("value", ToolInputValueType.Any, Required: true),
                new("evidenceObservationIds", ToolInputValueType.Array, Required: true),
                new("state", AllowedValues: ["inferred", "refuted", "stale"]),
                new("supersedes", Description: "Optional current model hypothesis ID or key to correct.")),
            RecordHypothesisTool)
    ];

    public HostKnowledgeQueryResult QueryKnowledge(string? key = null, string? keyPrefix = null, string? state = null, int limit = 16, string? cursor = null)
    {
        if (key is not null) ValidateText(key, 256, nameof(key));
        if (keyPrefix is not null) ValidateText(keyPrefix, 256, nameof(keyPrefix));
        if (key is not null && keyPrefix is not null) throw new ArgumentException("Choose either key or keyPrefix.");
        if (state is not null and not ("observed" or "inferred" or "supported" or "refuted" or "stale")) throw new ArgumentException("Unknown knowledge state.", nameof(state));
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit), "Knowledge pages contain between one and 32 entries.");
        lock (_gate)
        {
            var matches = _facts.Where(fact => fact.IsCurrent && (key is null || fact.Key == key) &&
                (keyPrefix is null || fact.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) && (state is null || fact.State == state))
                .OrderBy(fact => fact.Key, StringComparer.Ordinal).ThenBy(fact => fact.Source, StringComparer.Ordinal).ThenBy(fact => fact.Id, StringComparer.Ordinal).ToArray();
            var queryHash = ContextJson.Hash(new { _identity, key, keyPrefix, state });
            var knowledgeHash = ContextJson.Hash(matches);
            var offset = 0;
            if (cursor is not null)
            {
                ValidateText(cursor, 512, nameof(cursor));
                KnowledgeCursor decoded;
                try
                {
                    decoded = JsonSerializer.Deserialize<KnowledgeCursor>(Convert.FromBase64String(cursor), HostProtocol.Json)
                        ?? throw new ArgumentException("Knowledge cursor is empty.", nameof(cursor));
                }
                catch (Exception exception) when (exception is JsonException or FormatException)
                {
                    throw new ArgumentException("Knowledge cursor is invalid.", nameof(cursor), exception);
                }
                if (decoded.QueryHash != queryHash || decoded.KnowledgeHash != knowledgeHash)
                    throw new InvalidOperationException("Knowledge query or snapshot changed; restart pagination without a cursor.");
                if (decoded.Offset < 0 || decoded.Offset > matches.Length) throw new ArgumentException("Knowledge cursor offset is invalid.", nameof(cursor));
                offset = decoded.Offset;
            }
            var available = _observations.Select(item => item.Observation.ObservationId).ToHashSet(StringComparer.Ordinal);
            var page = new List<HostKnowledgeQueryItem>();
            var bytes = 0;
            foreach (var fact in matches.Skip(offset).Take(limit))
            {
                var item = new HostKnowledgeQueryItem(Copy(fact), fact.Evidence.Select(reference => new HostEvidenceManifest(reference,
                    available.Contains(reference.ObservationId))).ToArray());
                var itemBytes = ContextJson.Size(item);
                if (page.Count > 0 && bytes + itemBytes > 32_768) break;
                page.Add(item);
                bytes += itemBytes;
            }
            var nextOffset = offset + page.Count;
            var next = nextOffset < matches.Length
                ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new KnowledgeCursor(nextOffset, queryHash, knowledgeHash), HostProtocol.Json)) : null;
            return new(page.ToArray(), matches.Length, matches.Length - nextOffset, next, _prunedFactCount);
        }
    }

    private ToolResult QueryKnowledgeTool(ToolInvocation invocation)
    {
        var limit = 16;
        if (invocation.Input.ContainsKey("limit"))
        {
            var value = InputElement(invocation, "limit");
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out limit)) throw new ArgumentException("limit must be an integer.");
        }
        var result = QueryKnowledge(InputString(invocation, "key", false), InputString(invocation, "keyPrefix", false),
            InputString(invocation, "state", false), limit, InputString(invocation, "cursor", false));
        return Result(invocation, ReceiptStatus.Succeeded, "Bounded retained knowledge page retrieved; source availability and remaining entries are explicit.", result,
            result.Entries.SelectMany(item => item.Fact.Evidence).DistinctBy(reference => reference.ObservationId)
                .Select(reference => new EvidenceRef("host_observation", reference.ObservationId)).ToArray());
    }

    private ToolResult ReadEvidenceTool(ToolInvocation invocation)
    {
        var id = InputString(invocation, "observationId", required: true)!;
        var result = ReadEvidence(id);
        var expected = InputString(invocation, "contentHash", required: false);
        if (expected is not null && result.Reference?.ContentHash != expected)
            return Result(invocation, ReceiptStatus.Refused, "Requested content hash does not match the observation.", new { Status = "hash_mismatch", ObservationId = id });
        return Result(invocation, result.Status == "available" ? ReceiptStatus.Succeeded : ReceiptStatus.Unavailable,
            result.Status == "available" ? "Exact host observation retrieved." : "Exact host observation is unavailable; do not infer missing content.",
            result, result.Reference is null ? [] : [new("host_observation", id)]);
    }

    private ToolResult RecordHypothesisTool(ToolInvocation invocation)
    {
        var key = InputString(invocation, "key", required: true)!;
        var summary = InputString(invocation, "summary", required: true)!;
        var state = InputString(invocation, "state", required: false) ?? "inferred";
        var supersedes = InputString(invocation, "supersedes", required: false);
        ValidateText(key, 256, nameof(key));
        ValidateText(summary, 1_024, nameof(summary));
        if (state is not ("inferred" or "refuted" or "stale")) throw new ArgumentException("Models can only record inferred, refuted or stale hypotheses.");
        var value = InputElement(invocation, "value");
        if (ContextJson.Size(value) > 8_192) throw new ArgumentException("Hypothesis value exceeds its size bound.");
        var evidenceInput = InputElement(invocation, "evidenceObservationIds");
        if (evidenceInput.ValueKind != JsonValueKind.Array) throw new ArgumentException("evidenceObservationIds must be an array.");
        var evidenceIds = evidenceInput.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new ArgumentException("Evidence IDs must be strings.")).ToArray();
        HostKnowledgeEntry entry;
        lock (_gate)
        {
            var evidence = ResolveReferences(_observations, evidenceIds);
            var id = "hypothesis_" + ContextJson.Hash(new { invocation.RunId, invocation.StepId, key, summary, value, state, supersedes, evidenceIds });
            var duplicate = _facts.Find(fact => fact.Id == id);
            if (duplicate is not null) entry = duplicate;
            else
            {
                var old = _facts.LastOrDefault(fact => fact.Source == "model" && fact.IsCurrent && fact.Key == key);
                if (supersedes is not null)
                {
                    var explicitOld = _facts.LastOrDefault(fact => fact.Source == "model" && fact.IsCurrent && (fact.Id == supersedes || fact.Key == supersedes));
                    if (explicitOld is null) throw new InvalidOperationException("A model can supersede only its current hypotheses in this context.");
                    if (old is not null && old.Id != explicitOld.Id) throw new InvalidOperationException("A correction cannot replace two current hypotheses.");
                    old = explicitOld;
                }
                entry = new(id, key, summary, value.Clone(), state, "model", evidence, old?.Id,
                    old is null ? state : "revised", _observations[^1].Observation.Revision, DateTimeOffset.UtcNow, true);
                var facts = new List<HostKnowledgeEntry>(_facts);
                if (old is not null) Retire(facts, old);
                facts.Add(entry);
                Commit(new(_observations), facts);
            }
        }
        return Result(invocation, ReceiptStatus.Succeeded, "Sourced model hypothesis recorded; host truth and completion are unchanged.",
            Copy(entry), entry.Evidence.Select(reference => new EvidenceRef("host_observation", reference.ObservationId)).ToArray());
    }

    private static ToolRegistration Registration(string id, string name, ToolKind kind, ToolEffect effect, string description,
        ToolInputSchema schema, Func<ToolInvocation, ToolResult> operation) => new(
            new(id, name, kind, effect, InputSchema: schema, Description: description, RetrySafety: ToolRetrySafety.Idempotent),
            new ContextTool(operation),
            new(effect, [ToolDataBoundary.HostState], [ToolDataBoundary.HostState], ToolExternalOutputClassification.UntrustedStructuredData,
                ToolApprovalRequirement.None, ToolRetrySafety.Idempotent, new(ToolProvenanceKind.HostAuthored, "Agentica.Lab.Web.Context", "1")));

    private static ToolResult Result(ToolInvocation invocation, ReceiptStatus status, string summary, object value, IReadOnlyList<EvidenceRef>? evidence = null)
    {
        var receiptId = "context_receipt_" + Guid.NewGuid().ToString("N");
        var observationId = "context_result_" + Guid.NewGuid().ToString("N");
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = HostProtocol.Element(value) };
        var receiptData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["observationId"] = observationId,
            ["resultHash"] = ContextJson.Hash(value)
        };
        return new(
            new(receiptId, invocation.StepId, invocation.ToolId, status, summary, DateTimeOffset.UtcNow, receiptData),
            new(observationId, invocation.StepId, ObservationKind.ToolResult, summary, data, (evidence ?? []).Append(new("receipt", receiptId)).ToArray()));
    }

    private static JsonElement InputElement(ToolInvocation invocation, string name)
    {
        if (!invocation.Input.TryGetValue(name, out var value)) throw new ArgumentException($"Required argument {name} is missing.");
        return value is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(value, HostProtocol.Json);
    }

    private static string? InputString(ToolInvocation invocation, string name, bool required)
    {
        if (!invocation.Input.ContainsKey(name))
        {
            if (required) throw new ArgumentException($"Required argument {name} is missing.");
            return null;
        }
        var value = InputElement(invocation, name);
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{name} must be a string.");
        var text = value.GetString();
        ValidateText(text!, name switch { "summary" => 1_024, "cursor" => 512, _ => 256 }, name);
        return text;
    }

    private sealed class ContextTool(Func<ToolInvocation, ToolResult> operation) : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return Task.FromResult(operation(invocation)); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Task.FromResult(Result(invocation, ReceiptStatus.Refused, exception.Message, new { Status = "invalid_context_request" }));
            }
        }
    }
}

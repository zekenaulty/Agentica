using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Lab.Web.Context;

public sealed partial class HostContextSession : IPlanningFrameProjector
{
    public const int MaximumObservations = 128;
    public const int MaximumFacts = 256;
    public const int MaximumObservationBytes = 65_536;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumProjectedFacts = 48;
    private const int MaximumCompactFacts = 12;
    private readonly object _gate = new();
    private readonly HostContextIdentity _identity;
    private readonly string _path;
    private List<StoredObservation> _observations = [];
    private List<HostKnowledgeEntry> _facts = [];
    private long _prunedObservationCount;
    private long _prunedFactCount;
    private List<HostActionEvidence> _actions = [];
    private List<HostThoughtTest> _thoughtTests = [];

    internal HostContextSession(HostContextIdentity identity, string path)
    {
        _identity = identity;
        _path = path;
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("Stored host context exceeds its size bound.");
        ContextFile stored;
        string payloadHash;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            stored = document.RootElement.Deserialize<ContextFile>(HostProtocol.Json)
                ?? throw new InvalidDataException("Stored host context is empty.");
            payloadHash = ContextJson.Hash(document.RootElement.GetProperty("payload"));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored host context is not valid JSON.", exception);
        }
        if (stored.Version is not (1 or 2) || stored.Payload.Identity != identity || payloadHash != stored.ContentHash)
            throw new InvalidDataException("Stored host context identity or content hash does not match.");
        if (stored.Payload.Observations.Count > MaximumObservations || stored.Payload.Facts.Count > MaximumFacts)
            throw new InvalidDataException("Stored host context exceeds its retention bounds.");
        _observations = stored.Payload.Observations.ToList();
        _facts = stored.Payload.Facts.ToList();
        _prunedObservationCount = stored.Payload.PrunedObservationCount;
        _prunedFactCount = stored.Payload.PrunedFactCount;
        _actions = stored.Payload.Actions?.ToList() ?? [];
        _thoughtTests = stored.Payload.ThoughtTests?.ToList() ?? [];
        if (_actions.Count > MaximumActionEvidence || _thoughtTests.Count > MaximumThoughtTests)
            throw new InvalidDataException("Stored thought testing exceeds its retention bounds.");
        if (_observations.Count == 0 || _observations.Select(item => item.Observation.ObservationId).Distinct(StringComparer.Ordinal).Count() != _observations.Count)
            throw new InvalidDataException("Stored host observation identities are invalid.");
        foreach (var observation in _observations)
        {
            ValidateObservation(observation.Observation);
            if (ContextJson.Hash(observation.Observation) != observation.ContentHash)
                throw new InvalidDataException("Stored observation content hash does not match.");
        }
        foreach (var fact in _facts)
            foreach (var reference in fact.Evidence)
            {
                var source = _observations.Find(item => item.Observation.ObservationId == reference.ObservationId);
                if (source is not null && Reference(source) != reference)
                    throw new InvalidDataException("Stored knowledge evidence does not match its source.");
            }
        ValidateThoughtEvidence();
    }

    public HostObservation CurrentObservation
    {
        get { lock (_gate) return Copy(_observations[^1].Observation); }
    }

    public void Record(HostObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateObservation(observation);
        observation = Copy(observation);
        lock (_gate)
        {
            var (observations, facts) = PrepareObservation(observation);
            Commit(observations, facts);
        }
    }

    private (List<StoredObservation> Observations, List<HostKnowledgeEntry> Facts) PrepareObservation(HostObservation observation)
    {
        var hash = ContextJson.Hash(observation);
        var duplicate = _observations.Find(item => item.Observation.ObservationId == observation.ObservationId);
        if (duplicate is not null)
        {
            if (duplicate.ContentHash != hash) throw new InvalidOperationException("An observation identity cannot be reused for different content.");
            if (_observations[^1].Observation.ObservationId != observation.ObservationId)
                throw new InvalidOperationException("An old observation cannot become the current observation again.");
            return (new(_observations), new(_facts));
        }
        if (_observations.Count > 0 && observation.Revision < _observations[^1].Observation.Revision)
            throw new InvalidOperationException("An observation cannot rewind the session revision.");
        // Retained fact references continue to fence reuse after the exact source payload is pruned.
        var known = ReferencedEvidence().FirstOrDefault(reference => reference.ObservationId == observation.ObservationId);
        if (known is not null) throw new InvalidOperationException("A previously retained observation identity cannot be reused.");
        var observations = new List<StoredObservation>(_observations) { new(observation, hash) };
        var facts = new List<HostKnowledgeEntry>(_facts);
        foreach (var fact in observation.Facts ?? []) AddHostFact(facts, observations, observation, fact);
        return (observations, facts);
    }

    public HostContextSnapshot Snapshot()
    {
        lock (_gate)
        {
            var evidence = _observations.Select(Reference).Concat(ReferencedEvidence())
                .DistinctBy(reference => reference.ObservationId)
                .OrderBy(reference => reference.Revision).ThenBy(reference => reference.ObservationId, StringComparer.Ordinal)
                .Select(reference => new HostEvidenceManifest(reference, _observations.Exists(item => item.Observation.ObservationId == reference.ObservationId)))
                .ToArray();
            return new(_identity, Copy(_observations[^1].Observation), _facts.Select(Copy).ToArray(), evidence, _observations.Count,
                _prunedObservationCount, _prunedFactCount, _thoughtTests.ToArray(), _actions.Select(Copy).ToArray());
        }
    }

    public HostEvidenceReadResult ReadEvidence(string observationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        lock (_gate)
        {
            var found = _observations.Find(item => item.Observation.ObservationId == observationId);
            if (found is not null) return new("available", Reference(found), Copy(found.Observation));
            var reference = ReferencedEvidence().FirstOrDefault(item => item.ObservationId == observationId);
            return new(reference is null ? "unknown" : "not_retained", reference, null);
        }
    }

    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var current = _observations[^1];
            var available = _observations.Select(item => item.Observation.ObservationId).ToHashSet(StringComparer.Ordinal);
            var facts = _facts.Where(fact => fact.IsCurrent).OrderByDescending(fact => fact.Revision)
                .ThenBy(fact => fact.Key, StringComparer.Ordinal).ThenBy(fact => fact.Id, StringComparer.Ordinal).ToArray();
            var selected = facts.Take(MaximumProjectedFacts).ToArray();
            var payload = Projection(current, selected, facts.Length, available, compact: false, out var included);
            var evidence = included.SelectMany(fact => fact.Evidence).Append(Reference(current))
                .DistinctBy(reference => reference.ObservationId).Select(reference => new EvidenceRef("host_observation", reference.ObservationId)).ToArray();
            var compact = Projection(current, selected.Take(MaximumCompactFacts).ToArray(), facts.Length, available, compact: true, out _);
            return [new PlanningFrame("host_frame_" + ContextJson.Hash(payload), "agentica.host_context", "1", current.Observation.ObservedAt, payload, evidence)
            {
                ToolSurfaceId = request.ToolSurface?.SurfaceId,
                CompactPayload = compact
            }];
        }
    }

    private Dictionary<string, object?> Projection(StoredObservation current, IReadOnlyList<HostKnowledgeEntry> selected, int total,
        HashSet<string> available, bool compact, out HostKnowledgeEntry[] included)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["identity"] = _identity,
            ["currentObservation"] = new { Reference = Reference(current), Data = compact ? (JsonElement?)null : current.Observation.Data },
            ["facts"] = Array.Empty<object>(),
            ["omittedCurrentFactCount"] = total,
            ["retention"] = new { PrunedObservationCount = _prunedObservationCount, PrunedFactCount = _prunedFactCount },
            ["evidenceReadTool"] = "lab.evidence.read",
            ["hypothesisTool"] = "lab.hypothesis.record",
            ["knowledgeQueryTool"] = "lab.knowledge.query",
            ["thoughtAssessmentTool"] = "lab.hypothesis.assess",
            ["guidance"] = "Host observations describe the permitted perspective. Model hypotheses, including supported assessments, are not host truth or completion proof. For a useful thought test, record an expectedResult, falsifier and capabilityId before requesting the action; then assess its actual host result. Query retained knowledge by key/prefix when facts are omitted. Read exact retained evidence by observation ID. Unavailable evidence and inferred, refuted or stale knowledge must not be presented as fresh observation. Actions and completion require host receipts.",
            ["representation"] = compact ? "compact; current observation data and fact values omitted, source references retained" : "bounded public host context"
        };
        var entries = new List<object>();
        var retained = new List<HostKnowledgeEntry>();
        var budget = compact ? 16_384 : 81_920;
        var bytes = ContextJson.Size(payload);
        foreach (var fact in selected)
        {
            var entry = new
            {
                fact.Id,
                fact.Key,
                fact.Summary,
                Value = compact ? (JsonElement?)null : fact.Value,
                fact.State,
                fact.Source,
                fact.Change,
                fact.Supersedes,
                ThoughtTest = FindThoughtTest(fact.Id),
                Evidence = fact.Evidence.Select(reference => new HostEvidenceManifest(reference, available.Contains(reference.ObservationId))).ToArray()
            };
            var entryBytes = ContextJson.Size(entry) + 1;
            if (bytes + entryBytes > budget) break;
            entries.Add(entry);
            retained.Add(fact);
            bytes += entryBytes;
        }
        payload["facts"] = entries.ToArray();
        payload["omittedCurrentFactCount"] = total - entries.Count;
        included = retained.ToArray();
        return payload;
    }

    private static void AddHostFact(List<HostKnowledgeEntry> facts, List<StoredObservation> observations, HostObservation observation, KnowledgeFact fact)
    {
        var ids = fact.EvidenceObservationIds is { Count: > 0 } ? fact.EvidenceObservationIds : [observation.ObservationId];
        var references = ResolveReferences(observations, ids);
        var old = facts.LastOrDefault(entry => entry.Source == "host" && entry.IsCurrent && entry.Key == fact.Key);
        if (fact.Supersedes is not null)
        {
            var explicitOld = facts.LastOrDefault(entry => entry.Source == "host" && entry.IsCurrent && (entry.Id == fact.Supersedes || entry.Key == fact.Supersedes));
            if (explicitOld is null) throw new InvalidOperationException("Supersedes must identify current host knowledge in this context.");
            if (old is not null && old.Id != explicitOld.Id) throw new InvalidOperationException("A correction cannot replace two current facts.");
            old = explicitOld;
        }
        var change = old is null ? fact.State : ContextJson.Hash(old.Value) == ContextJson.Hash(fact.Value) && old.State == fact.State ? "refreshed" : "corrected";
        if (old is not null) Retire(facts, old);
        facts.Add(new(
            "fact_" + ContextJson.Hash(new { observation.ObservationId, fact.Key }), fact.Key, fact.Summary, fact.Value.Clone(), fact.State, "host",
            references, old?.Id, change, observation.Revision, observation.ObservedAt, true));
    }

    private static HostEvidenceReference[] ResolveReferences(List<StoredObservation> observations, IReadOnlyList<string> ids)
    {
        if (ids.Count is 0 or > 8) throw new InvalidOperationException("Knowledge requires between one and eight retained source observations.");
        return ids.Distinct(StringComparer.Ordinal).Select(id =>
        {
            var source = observations.Find(item => item.Observation.ObservationId == id)
                ?? throw new InvalidOperationException("Knowledge refers to an observation outside the retained context.");
            return Reference(source);
        }).ToArray();
    }

    private static void Retire(List<HostKnowledgeEntry> facts, HostKnowledgeEntry old) =>
        facts[facts.IndexOf(old)] = old with { IsCurrent = false, State = old.State == "refuted" ? "refuted" : "stale" };

    private void Commit(List<StoredObservation> observations, List<HostKnowledgeEntry> facts,
        List<HostActionEvidence>? actions = null, List<HostThoughtTest>? thoughtTests = null)
    {
        var prunedObservations = _prunedObservationCount + Math.Max(0, observations.Count - MaximumObservations);
        var prunedFacts = _prunedFactCount + Math.Max(0, facts.Count - MaximumFacts);
        if (observations.Count > MaximumObservations) observations.RemoveRange(0, observations.Count - MaximumObservations);
        // Prefer retaining active knowledge. Historical revisions remain bounded and never replace current facts silently.
        if (facts.Count > MaximumFacts)
            facts = facts.OrderByDescending(fact => fact.IsCurrent).ThenByDescending(fact => fact.Revision)
                .ThenByDescending(fact => fact.UpdatedAt).Take(MaximumFacts).OrderBy(fact => fact.Revision).ToList();
        actions ??= new(_actions);
        thoughtTests ??= new(_thoughtTests);
        var payload = new ContextPayload(_identity, observations, facts, prunedObservations, prunedFacts, actions, thoughtTests);
        if (ContextJson.Size(payload) > MaximumFileBytes - 256)
            throw new InvalidOperationException("Public context reached its persisted size bound.");
        ContextJson.AtomicWrite(_path, payload);
        _observations = observations;
        _facts = facts;
        _prunedObservationCount = prunedObservations;
        _prunedFactCount = prunedFacts;
        _actions = actions;
        _thoughtTests = thoughtTests;
    }

    private static HostEvidenceReference Reference(StoredObservation item) => new(
        item.Observation.ObservationId, item.Observation.Revision, item.Observation.ObservedAt, item.ContentHash);

    private static HostObservation Copy(HostObservation observation) => observation with
    {
        Data = observation.Data.Clone(),
        Facts = observation.Facts?.Select(fact => fact with { Value = fact.Value.Clone(), EvidenceObservationIds = fact.EvidenceObservationIds?.ToArray() }).ToArray()
    };

    private static HostKnowledgeEntry Copy(HostKnowledgeEntry fact) => fact with { Value = fact.Value.Clone(), Evidence = fact.Evidence.ToArray() };

    private static void ValidateObservation(HostObservation observation)
    {
        ValidateText(observation.ObservationId, 256, nameof(observation.ObservationId));
        if (observation.Revision < 0 || observation.ObservedAt == default) throw new ArgumentException("Observation revision and timestamp are required.", nameof(observation));
        if (observation.Data.ValueKind != JsonValueKind.Object) throw new ArgumentException("Observation data must be an object.", nameof(observation));
        if (observation.Facts?.Count > 64) throw new ArgumentException("An observation can contain at most 64 facts.", nameof(observation));
        if (observation.Facts?.Any(fact => fact is null) == true) throw new ArgumentException("An observation cannot contain a null fact.", nameof(observation));
        if (observation.Facts?.Select(fact => fact.Key).Distinct(StringComparer.Ordinal).Count() != observation.Facts?.Count)
            throw new ArgumentException("An observation cannot contain duplicate fact keys.", nameof(observation));
        foreach (var fact in observation.Facts ?? [])
        {
            ValidateText(fact.Key, 256, nameof(fact.Key));
            ValidateText(fact.Summary, 1_024, nameof(fact.Summary));
            if (fact.State is not ("observed" or "inferred" or "supported" or "refuted" or "stale")) throw new ArgumentException("Unknown knowledge state.", nameof(observation));
            if (fact.Value.ValueKind == JsonValueKind.Undefined || ContextJson.Size(fact.Value) > 8_192) throw new ArgumentException("Fact value is missing or exceeds its bound.", nameof(observation));
            if (fact.EvidenceObservationIds?.Count > 8) throw new ArgumentException("A fact can reference at most eight observations.", nameof(observation));
        }
        if (ContextJson.Size(observation) > MaximumObservationBytes) throw new ArgumentException("Observation exceeds the public context size bound.", nameof(observation));
    }

    private static void ValidateText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException($"{name} must contain between 1 and {maximum} characters.", name);
    }
}

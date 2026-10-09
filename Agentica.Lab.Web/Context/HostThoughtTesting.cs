using Agentica.Artifacts;
using Agentica.Lab.Web.Contracts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.Lab.Web.Context;

public sealed partial class HostContextSession
{
    public const int MaximumActionEvidence = 128;
    public const int MaximumThoughtTests = 128;

    /// <summary>Bind predictions to the original action before any possible delivery.
    /// This records intent only; durable effect custody remains the runtime's responsibility.</summary>
    public void RecordActionDispatch(HostActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateActionScope(request);
        lock (_gate)
        {
            var hash = ProtocolValidation.Digest(request);
            var existing = _actions.Find(action => action.ActionId == request.ActionId);
            if (existing is not null)
            {
                if (existing.RequestHash != hash) throw new InvalidOperationException("Action identity cannot bind changed prediction input.");
                return;
            }
            if (request.ExpectedRevision != _observations[^1].Observation.Revision)
                throw new InvalidOperationException("Prediction dispatch must use the current host observation revision.");
            var bound = _thoughtTests.Where(test => test.ActionId is null && test.Assessment is null &&
                test.CapabilityId == request.CapabilityId && _facts.Exists(fact => fact.Id == test.HypothesisId && fact.IsCurrent)).ToArray();
            var ids = bound.Select(test => test.TestId).ToArray();
            var tests = _thoughtTests.Select(test => ids.Contains(test.TestId, StringComparer.Ordinal) ? test with { ActionId = request.ActionId } : test).ToList();
            var actions = new List<HostActionEvidence>(_actions)
            {
                new(request.ActionId, request.CapabilityId, request.RunId, hash, DateTimeOffset.UtcNow, ids)
            };
            while (actions.Count > MaximumActionEvidence)
            {
                var oldest = actions.Find(action => action.Result is { Disposition: not "unresolved" });
                if (oldest is null) throw new InvalidOperationException("Unresolved action evidence reached its context bound.");
                actions.Remove(oldest);
            }
            Commit(new(_observations), new(_facts), actions, tests);
        }
    }

    /// <summary>Called only after host result and durable custody prevalidation.
    /// Commits the permitted observation and its original-result linkage together.</summary>
    public void RecordActionResult(HostActionRequest request, HostActionResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ValidateActionScope(request);
        if (result.ActionId != request.ActionId || result.SessionId != request.SessionId || result.SessionEpoch != request.SessionEpoch)
            throw new InvalidOperationException("Result does not belong to the original scoped action.");
        if (result.Disposition is not ("applied" or "refused" or "conflict" or "unavailable" or "unresolved"))
            throw new ArgumentException("Unknown host result disposition.", nameof(result));
        ValidateText(result.EvidenceId, 128, nameof(result.EvidenceId));
        if (result.Observation is not null)
        {
            ValidateObservation(result.Observation);
            if (result.Observation.Revision != result.AfterRevision) throw new InvalidOperationException("Result observation revision does not match.");
        }
        lock (_gate)
        {
            var action = _actions.Find(item => item.ActionId == request.ActionId)
                ?? throw new InvalidOperationException("Original action intent is not retained; do not invent result linkage.");
            if (action.RequestHash != ProtocolValidation.Digest(request)) throw new InvalidOperationException("Original action input does not match its retained hash.");
            var hash = ProtocolValidation.Digest(result);
            if (action.Result?.ResultHash == hash) return;
            if (action.Result is { Disposition: not "unresolved" }) throw new InvalidOperationException("A terminal host result cannot change.");
            if (_actions.Any(other => other.ActionId != action.ActionId && other.Result?.EvidenceId == result.EvidenceId))
                throw new InvalidOperationException("Host result evidence already belongs to another action.");
            var (observations, facts) = result.Observation is null
                ? (new List<StoredObservation>(_observations), new List<HostKnowledgeEntry>(_facts))
                : PrepareObservation(Copy(result.Observation));
            var reference = result.Observation is null ? null : Reference(observations.Single(item => item.Observation.ObservationId == result.Observation.ObservationId));
            var actions = new List<HostActionEvidence>(_actions);
            actions[actions.IndexOf(action)] = action with
            {
                Result = new(result.EvidenceId, result.Disposition, result.BeforeRevision, result.AfterRevision, hash, reference)
            };
            Commit(observations, facts, actions);
        }
    }

    private ToolResult AssessHypothesisTool(ToolInvocation invocation)
    {
        var hypothesisId = InputString(invocation, "hypothesisId", true)!;
        var actionId = InputString(invocation, "actionId", true)!;
        var evidenceId = InputString(invocation, "hostEvidenceId", true)!;
        var observationId = InputString(invocation, "observationId", true)!;
        var assessment = InputString(invocation, "assessment", true)!;
        var summary = InputString(invocation, "summary", true)!;
        if (assessment is not ("supported" or "refuted" or "inconclusive")) throw new ArgumentException("Unknown thought assessment.");
        HostThoughtTest updated;
        lock (_gate)
        {
            var test = _thoughtTests.Find(item => item.HypothesisId == hypothesisId)
                ?? throw new InvalidOperationException("A retained expectation and falsifier must precede the tested action.");
            var action = _actions.Find(item => item.ActionId == actionId)
                ?? throw new InvalidOperationException("Original action evidence is not retained.");
            if (test.ActionId != actionId || !action.ThoughtTestIds.Contains(test.TestId, StringComparer.Ordinal))
                throw new InvalidOperationException("This expectation was not bound before the original action.");
            var actual = action.Result;
            if (actual is null || actual.Disposition == "unresolved" || actual.Observation is null)
                throw new InvalidOperationException("Assessment requires a resolved host result with a retained observation.");
            if (actual.EvidenceId != evidenceId || actual.Observation.ObservationId != observationId)
                throw new InvalidOperationException("Assessment evidence does not match the original host result and observation.");
            var observed = _observations.Find(item => item.Observation.ObservationId == observationId);
            if (observed is null || Reference(observed) != actual.Observation)
                throw new InvalidOperationException("Exact result observation is unavailable or changed.");
            if (test.Assessment is { } previous)
            {
                if (previous.Assessment != assessment || previous.Summary != summary)
                    throw new InvalidOperationException("An assessed test is immutable; record a revised hypothesis for a new test.");
                updated = test;
            }
            else
            {
                var original = _facts.Find(fact => fact.Id == hypothesisId && fact.Source == "model" && fact.IsCurrent)
                    ?? throw new InvalidOperationException("The original model hypothesis is no longer current.");
                var id = "assessment_" + ContextJson.Hash(new { test.TestId, actionId, actual.ResultHash, assessment, summary });
                var now = DateTimeOffset.UtcNow;
                var facts = new List<HostKnowledgeEntry>(_facts);
                Retire(facts, original);
                facts.Add(new(id, original.Key, summary, original.Value.Clone(), assessment == "inconclusive" ? "inferred" : assessment,
                    "model", original.Evidence.Append(actual.Observation).DistinctBy(reference => reference.ObservationId).ToArray(),
                    original.Id, "thought_test_assessed", actual.AfterRevision, now, true));
                updated = test with
                {
                    Assessment = new(assessment, summary, actionId, evidenceId, observationId,
                    actual.ResultHash, actual.Observation.ContentHash, id, now)
                };
                var tests = new List<HostThoughtTest>(_thoughtTests);
                tests[tests.IndexOf(test)] = updated;
                Commit(new(_observations), facts, thoughtTests: tests);
            }
        }
        return Result(invocation, ReceiptStatus.Succeeded,
            "Model assessment is linked to the pre-action expectation and exact host result; host truth and completion are unchanged.", updated,
            [new EvidenceRef("host_observation", observationId), new EvidenceRef("host_result", evidenceId)]);
    }

    private void ValidateActionScope(HostActionRequest request)
    {
        if (request.SessionId != _identity.SessionId || request.SessionEpoch != _identity.SessionEpoch)
            throw new InvalidOperationException("Action belongs to another context session epoch.");
        ValidateText(request.ActionId, 128, nameof(request.ActionId));
        ValidateText(request.CapabilityId, 128, nameof(request.CapabilityId));
    }

    private HostThoughtTest? FindThoughtTest(string factId) => _thoughtTests.Find(test =>
        test.HypothesisId == factId || test.Assessment?.AssessedHypothesisId == factId);

    private IEnumerable<HostEvidenceReference> ReferencedEvidence() => _facts.SelectMany(fact => fact.Evidence)
        .Concat(_thoughtTests.Select(test => test.PlannedObservation))
        .Concat(_actions.Select(action => action.Result?.Observation).OfType<HostEvidenceReference>());

    private List<HostThoughtTest> AddThoughtTest(HostKnowledgeEntry hypothesis, string capabilityId, string expectedResult, string falsifier,
        List<HostKnowledgeEntry> facts)
    {
        var tests = new List<HostThoughtTest>(_thoughtTests)
        {
            new("thought_" + hypothesis.Id, hypothesis.Id, capabilityId, expectedResult, falsifier, hypothesis.UpdatedAt, Reference(_observations[^1]))
        };
        while (tests.Count > MaximumThoughtTests)
        {
            var removable = tests.Find(test => test.Assessment is not null || !facts.Exists(fact => fact.Id == test.HypothesisId && fact.IsCurrent));
            if (removable is null) throw new InvalidOperationException("Pending thought tests reached their bound; assess or revise existing hypotheses.");
            tests.Remove(removable);
        }
        return tests;
    }

    private void ValidateThoughtEvidence()
    {
        if (_actions.Select(action => action.ActionId).Distinct(StringComparer.Ordinal).Count() != _actions.Count ||
            _thoughtTests.Select(test => test.TestId).Distinct(StringComparer.Ordinal).Count() != _thoughtTests.Count)
            throw new InvalidDataException("Stored thought/action identities are not unique.");
        foreach (var action in _actions)
        {
            if (action.Result?.Observation is { } reference)
            {
                var observation = _observations.Find(item => item.Observation.ObservationId == reference.ObservationId);
                if (observation is not null && Reference(observation) != reference)
                    throw new InvalidDataException("Stored action result source does not match its observation.");
            }
        }
        foreach (var test in _thoughtTests)
        {
            var action = _actions.Find(item => item.ActionId == test.ActionId);
            if (action is not null && (action.CapabilityId != test.CapabilityId || !action.ThoughtTestIds.Contains(test.TestId, StringComparer.Ordinal)))
                throw new InvalidDataException("Stored prediction does not match the original action binding.");
            if (test.Assessment is { } assessment && action is not null)
            {
                var result = action.Result;
                if (result is null || result.ResultHash != assessment.ResultHash || result.EvidenceId != assessment.HostEvidenceId ||
                    result.Observation is not { } source || source.ContentHash != assessment.ObservationHash || source.ObservationId != assessment.ObservationId)
                    throw new InvalidDataException("Stored thought assessment does not match host result evidence.");
            }
        }
    }

    private static HostActionEvidence Copy(HostActionEvidence action) => action with { ThoughtTestIds = action.ThoughtTestIds.ToArray() };
}

namespace Agentica.Clients.Llm;

/// <summary>
/// Single-use observation holder for one client invocation. Latest remains readable
/// after ordinary cancellation or stream disposal. It is not an observer callback,
/// host journal, admission decision, retry policy or persistence mechanism.
/// </summary>
public sealed class LlmAttemptCapture
{
    private LlmAttemptEvidence? _latest;
    private int _claimed;

    public LlmAttemptCapture(LlmAttemptReferences references)
    {
        ArgumentNullException.ThrowIfNull(references);
        References = references;
    }

    public LlmAttemptReferences References { get; }
    public LlmAttemptEvidence? Latest => Volatile.Read(ref _latest);

    internal void Claim()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("An attempt capture can be used by only one client invocation.");
    }

    internal void Publish(LlmAttemptEvidence evidence) => Volatile.Write(ref _latest, evidence);

    internal LlmAttemptEvidence? TryCorrectOwnedCompletion(LlmAttemptEvidence expected,
        LlmAttemptOutcome outcome, string reasonCode)
    {
        // The wrapper received this exact snapshot through its completed response.
        // Merely finding evidence in a supplied capture does not establish ownership:
        // a rejected second invocation must not rewrite the original attempt.
        if (!ReferenceEquals(expected.References, References) || expected.Outcome != LlmAttemptOutcome.Succeeded)
            return null;
        var corrected = CorrectCompletionSnapshot(expected, outcome, reasonCode);
        return ReferenceEquals(Interlocked.CompareExchange(ref _latest, corrected, expected), expected)
            ? corrected : null;
    }

    internal static LlmAttemptEvidence CorrectCompletionSnapshot(LlmAttemptEvidence previous,
        LlmAttemptOutcome outcome, string reasonCode)
    {
        if (previous.Outcome != LlmAttemptOutcome.Succeeded) return previous;
        return new(previous.References, previous.ProviderName, previous.EffectiveModelId,
            previous.RequestedModelId, checked(previous.Sequence + 1), previous.DispatchCertainty,
            outcome, previous.ProviderReportedModelId, previous.ProviderRequestId,
            previous.ProviderResponseId, previous.HttpStatusCode, previous.Usage,
            previous.ProviderStatus, previous.FinishReason, reasonCode);
    }
}

/// <summary>Adapter-only producer of detached snapshots; no host effects or durable storage.</summary>
internal sealed class LlmAttemptRecorder
{
    private readonly object _gate = new();
    private readonly LlmAttemptCapture _capture;
    private bool _usageSemanticsConflicted;

    private LlmAttemptRecorder(LlmAttemptCapture capture, LlmAttemptEvidence initial)
    {
        _capture = capture;
        capture.Claim();
        capture.Publish(initial);
    }

    public LlmAttemptEvidence Evidence => _capture.Latest!;

    public static LlmAttemptRecorder Start(LlmRequest request, string providerName, string effectiveModelId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capture = request.AttemptCapture ?? new LlmAttemptCapture(
            new LlmAttemptReferences("client-observation:" + Guid.NewGuid().ToString("N")));
        var initial = new LlmAttemptEvidence(capture.References, providerName, effectiveModelId,
            string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId);
        return new LlmAttemptRecorder(capture, initial);
    }

    public LlmAttemptEvidence Observe(string? providerReportedModelId = null,
        string? providerRequestId = null, string? providerResponseId = null,
        int? httpStatusCode = null, LlmUsage? usage = null, string? providerStatus = null,
        LlmFinishReason? finishReason = null, LlmDispatchCertainty? dispatchCertainty = null)
    {
        if (dispatchCertainty is { } certainty && !Enum.IsDefined(certainty))
            throw new ArgumentOutOfRangeException(nameof(dispatchCertainty));
        lock (_gate)
        {
            var previous = Evidence;
            var nextDispatch = dispatchCertainty is { } supplied && (int)supplied > (int)previous.DispatchCertainty
                ? supplied : previous.DispatchCertainty;
            var semanticsConflicted = _usageSemanticsConflicted;
            var mergedUsage = MergeUsage(previous.Usage, usage, ref semanticsConflicted);
            var next = new LlmAttemptEvidence(previous.References, previous.ProviderName,
                previous.EffectiveModelId, previous.RequestedModelId, checked(previous.Sequence + 1),
                nextDispatch, previous.Outcome,
                providerReportedModelId ?? previous.ProviderReportedModelId,
                providerRequestId ?? previous.ProviderRequestId,
                providerResponseId ?? previous.ProviderResponseId,
                httpStatusCode ?? previous.HttpStatusCode, mergedUsage,
                providerStatus ?? previous.ProviderStatus, finishReason ?? previous.FinishReason,
                previous.ReasonCode);
            _usageSemanticsConflicted = semanticsConflicted;
            _capture.Publish(next);
            return next;
        }
    }

    public LlmAttemptEvidence Finish(LlmAttemptOutcome outcome, string? reasonCode = null)
    {
        if (!Enum.IsDefined(outcome) || outcome == LlmAttemptOutcome.InProgress)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        LlmAttemptGuard.Code(reasonCode, nameof(reasonCode));
        lock (_gate)
        {
            var previous = Evidence;
            // A late failure can invalidate successful client delivery. Final cleanup
            // must not replace an already recorded failure with success or abandonment.
            if (previous.Outcome is not (LlmAttemptOutcome.InProgress or LlmAttemptOutcome.Succeeded))
                return previous;
            if (previous.Outcome == outcome && (reasonCode is null || reasonCode == previous.ReasonCode))
                return previous;
            var next = new LlmAttemptEvidence(previous.References, previous.ProviderName,
                previous.EffectiveModelId, previous.RequestedModelId, checked(previous.Sequence + 1),
                previous.DispatchCertainty, outcome, previous.ProviderReportedModelId,
                previous.ProviderRequestId, previous.ProviderResponseId, previous.HttpStatusCode,
                previous.Usage, previous.ProviderStatus, previous.FinishReason,
                reasonCode ?? previous.ReasonCode);
            _capture.Publish(next);
            return next;
        }
    }

    private static LlmUsage? MergeUsage(LlmUsage? previous, LlmUsage? observed,
        ref bool semanticsConflicted)
    {
        if (observed is null) return previous;
        if (!Enum.IsDefined(observed.OutputTokenSemantics))
            throw new ArgumentOutOfRangeException(nameof(observed));
        if (previous is null) return observed;
        var oldSemantics = previous.OutputTokenSemantics;
        var newSemantics = observed.OutputTokenSemantics;
        if (oldSemantics != LlmOutputTokenSemantics.Unknown &&
            newSemantics != LlmOutputTokenSemantics.Unknown && oldSemantics != newSemantics)
            semanticsConflicted = true;
        var semantics = semanticsConflicted ? LlmOutputTokenSemantics.Unknown :
            newSemantics == LlmOutputTokenSemantics.Unknown ? oldSemantics : newSemantics;
        // Provider updates are cumulative observations. Omitted counters retain the
        // earlier value; supplied counters replace it and are never added as deltas.
        return new LlmUsage(observed.PromptTokens ?? previous.PromptTokens,
            observed.OutputTokens ?? previous.OutputTokens,
            observed.ThinkingTokens ?? previous.ThinkingTokens,
            observed.TotalTokens ?? previous.TotalTokens,
            observed.CachedPromptTokens ?? previous.CachedPromptTokens,
            observed.ToolUsePromptTokens ?? previous.ToolUsePromptTokens)
        { OutputTokenSemantics = semantics };
    }
}

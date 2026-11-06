namespace Agentica.Clients.Llm;

/// <summary>Conservative observations at the configured transport boundary, not proof of provider execution.</summary>
public enum LlmDispatchCertainty
{
    NotDispatched,
    MayHaveDispatched,
    ResponseObserved
}

/// <summary>Client delivery/validation state, independent of provider status and host acceptance.</summary>
public enum LlmAttemptOutcome
{
    InProgress,
    Succeeded,
    Rejected,
    Failed,
    Cancelled,
    Abandoned
}

/// <summary>
/// An immutable bounded snapshot of facts observed during a provider attempt.
/// It contains neither output nor native continuation. Success is a client result,
/// not host acceptance, objective completion, a cost calculation or a durable receipt.
/// </summary>
public sealed record LlmAttemptEvidence
{
    public LlmAttemptEvidence(LlmAttemptReferences references, string providerName,
        string effectiveModelId, string? requestedModelId = null, long sequence = 1,
        LlmDispatchCertainty dispatchCertainty = LlmDispatchCertainty.NotDispatched,
        LlmAttemptOutcome outcome = LlmAttemptOutcome.InProgress,
        string? providerReportedModelId = null, string? providerRequestId = null,
        string? providerResponseId = null, int? httpStatusCode = null,
        LlmUsage? usage = null, string? providerStatus = null,
        LlmFinishReason? finishReason = null, string? reasonCode = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1L);
        if (!Enum.IsDefined(dispatchCertainty))
            throw new ArgumentOutOfRangeException(nameof(dispatchCertainty));
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (httpStatusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode));
        if (finishReason is { } finish && !Enum.IsDefined(finish))
            throw new ArgumentOutOfRangeException(nameof(finishReason));
        if (usage is not null && !Enum.IsDefined(usage.OutputTokenSemantics))
            throw new ArgumentOutOfRangeException(nameof(usage));

        References = references;
        ProviderName = LlmAttemptGuard.Required(providerName, nameof(providerName), 128);
        EffectiveModelId = LlmAttemptGuard.Required(effectiveModelId, nameof(effectiveModelId), 512);
        RequestedModelId = LlmAttemptGuard.Optional(requestedModelId, nameof(requestedModelId), 512);
        ProviderReportedModelId = LlmAttemptGuard.Optional(providerReportedModelId,
            nameof(providerReportedModelId), 512);
        ProviderRequestId = LlmAttemptGuard.Optional(providerRequestId, nameof(providerRequestId), 512);
        ProviderResponseId = LlmAttemptGuard.Optional(providerResponseId, nameof(providerResponseId), 512);
        ProviderStatus = LlmAttemptGuard.Optional(providerStatus, nameof(providerStatus), 128);
        ReasonCode = LlmAttemptGuard.Code(reasonCode, nameof(reasonCode));
        Sequence = sequence;
        DispatchCertainty = dispatchCertainty;
        Outcome = outcome;
        HttpStatusCode = httpStatusCode;
        Usage = usage;
        FinishReason = finishReason;
    }

    public LlmAttemptReferences References { get; }
    public string ProviderName { get; }
    public string? RequestedModelId { get; }
    public string EffectiveModelId { get; }
    public string? ProviderReportedModelId { get; }
    public long Sequence { get; }
    public LlmDispatchCertainty DispatchCertainty { get; }
    public LlmAttemptOutcome Outcome { get; }
    public string? ProviderRequestId { get; }
    public string? ProviderResponseId { get; }
    public int? HttpStatusCode { get; }
    public LlmUsage? Usage { get; }
    public string? ProviderStatus { get; }
    public LlmFinishReason? FinishReason { get; }
    public string? ReasonCode { get; }
}

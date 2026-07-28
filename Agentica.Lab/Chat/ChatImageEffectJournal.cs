using System.Text;
using Agentica;
using Agentica.Artifacts;
using Agentica.Tools;

internal sealed class ChatImageEffectJournal
{
    internal const string OversizedProviderRequestIdEvidence = "oversized-provider-request-id";

    private const int MaxEntries = 64;
    private const int MaxEffectNames = 16;
    private const int MaxTextLength = 240;
    private const int MaxProviderRequestIdBytes = 4096;

    private readonly List<IReadOnlyDictionary<string, object?>> _entries = [];
    private readonly HashSet<string> _activeLocalEffects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _indeterminateLocalEffects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _providerRequestIds = new(StringComparer.Ordinal);
    private int _droppedEntryCount;

    public int ProviderDispatchAttempts { get; private set; }

    public int ProviderResponses { get; private set; }

    public int LocalMutationAttempts { get; private set; }

    public int CleanupAttempts { get; private set; }

    public bool EffectsStarted => ProviderDispatchAttempts > 0 || LocalMutationAttempts > 0;

    public bool HasResidualOrIndeterminateLocalEffects =>
        _activeLocalEffects.Count > 0 || _indeterminateLocalEffects.Count > 0;

    public void ProviderDispatchAttempted(string providerRole, string modelId)
    {
        ProviderDispatchAttempts++;
        Add("provider_dispatch", "attempted", providerRole, modelId);
    }

    public void ProviderResponseReceived(
        string providerRole,
        string providerName,
        string modelId,
        string? providerRequestId = null)
    {
        ProviderResponses++;
        if (providerRequestId is not null && _providerRequestIds.Count < 8)
        {
            var requestIdEvidence = SnapshotProviderRequestId(providerRequestId);
            if (requestIdEvidence is not null)
            {
                _providerRequestIds[Limit(providerRole)] = requestIdEvidence;
            }
        }

        Add(
            "provider_dispatch",
            "completed",
            providerRole,
            $"{Limit(providerName)}/{Limit(modelId)}");
    }

    public void ProviderDispatchFailed(string providerRole, Exception exception, bool cancelled)
    {
        Add(
            "provider_dispatch",
            cancelled ? "cancelled_indeterminate" : "failed_indeterminate",
            providerRole,
            exception.GetType().Name);
    }

    public void MutationAttempted(string effectName, string detail)
    {
        LocalMutationAttempts++;
        _indeterminateLocalEffects.Add(effectName);
        Add("local_mutation", "attempted", effectName, detail);
    }

    public void MutationCompleted(string effectName, string detail)
    {
        _indeterminateLocalEffects.Remove(effectName);
        _activeLocalEffects.Add(effectName);
        Add("local_mutation", "completed", effectName, detail);
    }

    public void MutationConfirmedAbsent(string effectName, string detail)
    {
        _indeterminateLocalEffects.Remove(effectName);
        _activeLocalEffects.Remove(effectName);
        Add("local_mutation", "confirmed_absent", effectName, detail);
    }

    public void MutationFailed(string effectName, string detail, bool outcomeIndeterminate)
    {
        if (!outcomeIndeterminate)
        {
            _indeterminateLocalEffects.Remove(effectName);
        }

        Add(
            "local_mutation",
            outcomeIndeterminate ? "failed_indeterminate" : "failed",
            effectName,
            detail);
    }

    public void PublishAttempted(string publishedEffectName, string detail)
    {
        LocalMutationAttempts++;
        _indeterminateLocalEffects.Add(publishedEffectName);
        Add("publish", "attempted", publishedEffectName, detail);
    }

    public void PublishCompleted(string stagedEffectName, string publishedEffectName, string detail)
    {
        _indeterminateLocalEffects.Remove(stagedEffectName);
        _activeLocalEffects.Remove(stagedEffectName);
        _indeterminateLocalEffects.Remove(publishedEffectName);
        _activeLocalEffects.Add(publishedEffectName);
        Add("publish", "completed", publishedEffectName, detail);
    }

    public void PublishFailed(string publishedEffectName, string detail)
    {
        Add("publish", "failed_indeterminate", publishedEffectName, detail);
    }

    public void CleanupCompleted(string effectName, string detail)
    {
        CleanupAttempts++;
        _indeterminateLocalEffects.Remove(effectName);
        _activeLocalEffects.Remove(effectName);
        Add("cleanup", "completed", effectName, detail);
    }

    public void CleanupNotNeeded(string effectName, string detail)
    {
        CleanupAttempts++;
        _indeterminateLocalEffects.Remove(effectName);
        _activeLocalEffects.Remove(effectName);
        Add("cleanup", "confirmed_absent", effectName, detail);
    }

    public void CleanupFailed(string effectName, string detail)
    {
        CleanupAttempts++;
        _indeterminateLocalEffects.Add(effectName);
        Add("cleanup", "failed_indeterminate", effectName, detail);
    }

    public IReadOnlyDictionary<string, object?> Snapshot(string operationOutcome)
    {
        var active = _activeLocalEffects
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaxEffectNames)
            .ToArray();
        var unresolved = _activeLocalEffects
            .Concat(_indeterminateLocalEffects)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaxEffectNames)
            .ToArray();
        var succeeded = operationOutcome.Equals("succeeded", StringComparison.Ordinal);
        var residual = succeeded ? Array.Empty<string>() : unresolved;
        var effectState = operationOutcome.Equals("succeeded", StringComparison.Ordinal)
            ? "completed"
            : residual.Length > 0
                ? "partial_or_indeterminate"
                : ProviderDispatchAttempts > ProviderResponses
                    ? "provider_outcome_indeterminate_local_compensated"
                    : ProviderResponses > 0
                        ? "provider_completed_local_compensated"
                        : "no_effect_observed";

        return new Dictionary<string, object?>
        {
            ["operationOutcome"] = operationOutcome,
            ["effectState"] = effectState,
            ["providerDispatchAttempts"] = ProviderDispatchAttempts,
            ["providerResponses"] = ProviderResponses,
            ["providerRequestIds"] = new Dictionary<string, string>(
                _providerRequestIds,
                StringComparer.Ordinal),
            ["localMutationAttempts"] = LocalMutationAttempts,
            ["cleanupAttempts"] = CleanupAttempts,
            ["cleanupComplete"] = !succeeded && residual.Length == 0,
            ["committedLocalEffects"] = succeeded ? active : Array.Empty<string>(),
            ["residualOrIndeterminateLocalEffects"] = residual,
            ["entries"] = _entries.ToArray(),
            ["journalLimit"] = MaxEntries,
            ["droppedEntryCount"] = _droppedEntryCount
        };
    }

    private void Add(string category, string outcome, string subject, string detail)
    {
        if (_entries.Count >= MaxEntries)
        {
            _droppedEntryCount++;
            return;
        }

        _entries.Add(new Dictionary<string, object?>
        {
            ["sequence"] = _entries.Count + 1,
            ["category"] = category,
            ["outcome"] = outcome,
            ["subject"] = Limit(subject),
            ["detail"] = Limit(detail)
        });
    }

    private static string Limit(string value) =>
        value.Length <= MaxTextLength ? value : value[..MaxTextLength];

    private static string? SnapshotProviderRequestId(string providerRequestId)
    {
        if (providerRequestId.Length > MaxProviderRequestIdBytes ||
            Encoding.UTF8.GetByteCount(providerRequestId) > MaxProviderRequestIdBytes)
        {
            return OversizedProviderRequestIdEvidence;
        }

        return string.IsNullOrWhiteSpace(providerRequestId)
            ? null
            : string.Concat(providerRequestId);
    }
}

internal sealed class ChatImageEffectException : Exception
{
    public ChatImageEffectException(
        string message,
        ChatImageEffectJournal journal,
        bool cancelled,
        Exception innerException)
        : base(message, innerException)
    {
        Journal = journal;
        Cancelled = cancelled;
    }

    public ChatImageEffectJournal Journal { get; }

    public bool Cancelled { get; }
}

internal static class ChatImageEffectReceipts
{
    public static ToolResult Failure(
        ToolInvocation invocation,
        string message,
        ChatImageEffectJournal journal,
        bool cancelled = false)
    {
        var status = journal.HasResidualOrIndeterminateLocalEffects
            ? ReceiptStatus.Partial
            : cancelled
                ? ReceiptStatus.Cancelled
                : ReceiptStatus.Failed;
        var outcome = status switch
        {
            ReceiptStatus.Partial => "partial",
            ReceiptStatus.Cancelled => "cancelled",
            _ => "failed"
        };
        var data = new Dictionary<string, object?>
        {
            ["error"] = message,
            ["effectJournal"] = journal.Snapshot(outcome)
        };
        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            data);
        return new ToolResult(receipt);
    }
}

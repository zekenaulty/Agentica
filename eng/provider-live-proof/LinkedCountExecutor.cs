using Agentica.Lab.Web.Chat;
using System.Security.Cryptography;

namespace Agentica.ProviderLiveProof;

/// <summary>Links the complete compiler/dispatch input to separately observed native count work.</summary>
internal sealed class LinkedCountExecutor(OpenAiExperimentExecutor inner, DurableProviderGuard guard)
    : IExperimentExecutor, IExperimentInputCounterSource, IExperimentInputCountJournalConsumer
{
    public Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken) =>
        inner.ExecuteAsync(context, cancellationToken);
    public void BindInputCountJournal(IExperimentInputCountJournal journal) => inner.BindInputCountJournal(journal);
    public IExperimentInputTokenCounter CreateInputTokenCounter(ExperimentSettings settings) =>
        new LinkedCounter(inner.CreateInputTokenCounter(settings), guard);

    private sealed class LinkedCounter(IExperimentInputTokenCounter counter, DurableProviderGuard guard)
        : IExperimentInputTokenCounter, IExperimentInputCountEvidenceSource, IDisposable
    {
        public IReadOnlyList<ExperimentTokenCount> Measurements => counter.Measurements;
        public IReadOnlyList<ExperimentInputCountRecord> CountAttempts =>
            (counter as IExperimentInputCountEvidenceSource)?.CountAttempts ?? [];
        public async Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
        {
            var frozen = request.ToArray();
            try
            {
                var hash = ProofJournal.BodyHash(frozen);
                using var intent = guard.BeginCount(frozen);
                var result = await counter.CountAsync(frozen, cancellationToken).ConfigureAwait(false);
                if (!result.ProviderCount || result.RequestHash != hash || result.CountRequestHash is null)
                    throw new InvalidOperationException("proof.non_native_or_mismatched_measurement");
                guard.AcceptMeasurement(hash, result.Tokens, result.CountRequestHash);
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(frozen); }
        }
        public void Dispose() => (counter as IDisposable)?.Dispose();
    }
}

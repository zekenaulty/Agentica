namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>Explicit local index maintenance. It never starts provider work.</summary>
public sealed class ExperimentHistoryMaintenance(ExperimentStore store) : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    private sealed class Job(CancellationTokenSource cancellation, ConversationIndexProgress progress)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal ConversationIndexProgress Progress { get; set; } = progress;
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    public ConversationIndexProgress Start(string conversationId)
    {
        var head = store.GetHead(conversationId);
        var source = store.BindEvidenceSource(conversationId, head.LatestSequence);
        var coverage = source.GetCoverage();
        lock (_gate)
        {
            if (_jobs.TryGetValue(conversationId, out var active) && !active.Task.IsCompleted) return active.Progress;
            if (_jobs.Values.Count(job => !job.Task.IsCompleted) >= 2)
                throw new ExperimentException("history.index_busy", "Two local index rebuilds are already running. Finish or cancel one before starting another.", 409);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var progress = new ConversationIndexProgress(conversationId, "INDEXING", coverage.IndexedSourceCount,
                coverage.EligibleSourceCount, coverage.LastIndexedSequence, coverage.FtsAvailable, coverage.IndexVersion);
            var job = new Job(cancellation, progress);
            _jobs[conversationId] = job;
            job.Task = Task.Run(() =>
            {
                try
                {
                    var completed = store.RebuildIndex(conversationId, cancellation.Token);
                    lock (_gate) job.Progress = completed;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    lock (_gate) job.Progress = progress with { Status = "CANCELLED" };
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Keep database paths and source text out of operational errors.
                    lock (_gate) job.Progress = progress with { Status = "DEGRADED", Error = "history.index_failed" };
                }
                finally { cancellation.Dispose(); }
            }, CancellationToken.None);
            return progress;
        }
    }

    public ConversationIndexProgress? Status(string conversationId)
    {
        lock (_gate) return _jobs.GetValueOrDefault(conversationId)?.Progress;
    }

    public ConversationIndexProgress Cancel(string conversationId)
    {
        Job? job;
        lock (_gate) job = _jobs.GetValueOrDefault(conversationId);
        if (job is null)
        {
            var head = store.GetHead(conversationId);
            var coverage = store.BindEvidenceSource(conversationId, head.LatestSequence).GetCoverage();
            return new(conversationId, coverage.Status, coverage.IndexedSourceCount, coverage.EligibleSourceCount,
                coverage.LastIndexedSequence, coverage.FtsAvailable, coverage.IndexVersion);
        }
        try { job.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* A completed job is already stopped. */ }
        lock (_gate) return job.Task.IsCompleted ? job.Progress : job.Progress with { Status = "CANCELLING" };
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_gate) pending = _jobs.Values.Select(job => job.Task).ToArray();
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() => _stopping.Dispose();
}

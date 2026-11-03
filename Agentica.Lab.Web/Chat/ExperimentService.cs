using System.Collections.Concurrent;
using Agentica.Lab.Web.Providers;

namespace Agentica.Lab.Web.Chat;

public sealed class ExperimentService(ExperimentStore store, IExperimentExecutor executor,
    OpenAiProviderConfiguration configuration) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cancellation, Task Task)> _active = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    public ExperimentConversation Create(string? title)
    {
        var provider = configuration.GetSettings();
        return store.Create(new() { Model = provider.Model, ReasoningEffort = provider.ThinkingEffort }, title);
    }

    public ExperimentTurn Send(string conversationId, ExperimentSendRequest request)
    {
        if (request.RequestId is not null && store.FindRequest(conversationId, request.RequestId, request.Content) is { } existing) return existing;
        var head = store.GetHead(conversationId);
        var conversation = head.Conversation;
        // Compile before creating an active turn so an over-budget request leaves no uncertain dispatch.
        var preview = new ExperimentTurn("preview", conversationId, head.LatestSequence + 1, request.Content ?? "", "", "running",
            DateTimeOffset.UtcNow, null, conversation.SettingsRevision, conversation.Settings, "transcript-only; native reasoning state is not persisted");
        // Preflight only mandatory input and its recent floor. Historical selection runs
        // once against the admitted turn's frozen source cut, never twice per send.
        _ = ExperimentContextCompiler.Prepare(Window(head, preview), preview);
        var (turn, created) = store.Begin(conversationId, request, conversation.SettingsRevision, head.LatestSequence);
        if (!created) return turn;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        // The executor owns its deadline so explicit cancellation and timeout remain distinguishable.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active[turn.Id] = (cancellation, completion.Task);
        _ = ExecuteAsync(conversationId, turn, cancellation, completion);
        return turn;
    }

    private async Task ExecuteAsync(string conversationId, ExperimentTurn turn, CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(turn.Settings.TimeoutSeconds));
        try
        {
            await Task.Yield();
            store.Append(conversationId, turn.Id, new("turn.started", turn));
            var head = store.GetHead(conversationId);
            var window = Window(head, turn);
            var source = turn.Settings.ThreadRecallEnabled ? store.BindEvidenceSource(conversationId, turn.Sequence - 1) : null;
            var context = ExperimentContextCompiler.Prepare(window, turn, source, deadline.Token);
            store.Append(conversationId, turn.Id, new("context.manifest", context.Manifest with { HistoryPage = window.HistoryPage }));
            if (context.Recall is not null) store.Append(conversationId, turn.Id, new("context.recall", context.Recall.Manifest));
            store.Append(conversationId, turn.Id, new("context.isolation", new
            {
                conversationId,
                memory = "disabled",
                crossConversationRetrieval = false,
                instructions = turn.Settings.Instructions.Length == 0 ? "omitted" : "exact user-authored text",
                nativeContinuation = "not retained; visible transcript replay only",
                compaction = "disabled; no summarization call",
                mode = turn.Settings.Mode,
                enabledTools = turn.Settings.EnabledTools,
                ambientWebSearch = turn.Settings.AmbientWebSearch,
                maxWebSearchCalls = turn.Settings.MaxWebSearchCalls,
                threadRecall = turn.Settings.ThreadRecallEnabled ? turn.Settings.ThreadRecallMode : "off"
            }));
            var workspace = Path.Combine(store.WorkspaceRoot, conversationId);
            Directory.CreateDirectory(workspace);
            var execution = new ExperimentExecutionContext(conversationId, turn, context.Messages, workspace,
                input => { store.Append(conversationId, turn.Id, input); return Task.CompletedTask; },
                delta =>
                {
                    store.OutputAndTrace(conversationId, turn.Id, delta);
                    return Task.CompletedTask;
                })
            { EvidenceSource = source, Recall = context.Recall };
            var result = await executor.ExecuteAsync(execution, deadline.Token).ConfigureAwait(false);
            if (result.Status == "cancelled" && deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
                result = result with
                {
                    Status = "failed",
                    ErrorCode = "turn.timeout",
                    ErrorMessage = "The turn time limit expired. Partial evidence is retained; provider usage or an in-flight effect may be unknown. No automatic replay occurred."
                };
            Finish(conversationId, turn.Id, result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Finish(conversationId, turn.Id, new("cancelled", ErrorCode: "turn.cancelled",
                ErrorMessage: "Cancelled or timed out. Partial output is retained. Provider usage or an in-flight effect may be unknown; no automatic replay occurred."));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Finish(conversationId, turn.Id, new("failed", ErrorCode: "turn.timeout",
                ErrorMessage: "The turn time limit expired during context preparation or execution. Partial evidence is retained; no automatic replay occurred."));
        }
        catch (ExperimentException exception)
        {
            Finish(conversationId, turn.Id, new("failed", ErrorCode: exception.Code, ErrorMessage: exception.Message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Exception messages can contain credentials or raw HTTP diagnostics. Keep them out of chat/logs.
            Finish(conversationId, turn.Id, new("failed", ErrorCode: "turn.execution_failed",
                ErrorMessage: "The turn failed. Retained request, trace and partial output describe the boundary reached; no automatic replay occurred."));
        }
        finally
        {
            _active.TryRemove(turn.Id, out _);
            cancellation.Dispose();
            completion.TrySetResult();
        }
    }

    private ExperimentConversation Window(Recall.ExperimentConversationHead head, ExperimentTurn turn)
    {
        var settings = turn.Settings;
        var count = settings.ThreadRecallEnabled ? settings.ThreadRecallRecentTurns : settings.MaxHistoryMessages / 2 + 1;
        IReadOnlyList<ExperimentTurn> turns = count == 0 ? [] : store.GetRecentCommittedTurns(turn.ConversationId, turn.Sequence, count);
        var earlier = Math.Max(0, head.CompletedTurns - turns.Count);
        return head.Conversation with
        {
            Turns = turns,
            HistoryPage = new(earlier > 0 && turns.Count > 0 ? turns[0].Sequence : null,
                head.TotalTurns, head.LatestSequence, head.CompletedTurns, earlier)
        };
    }

    private void Finish(string conversationId, string turnId, ExperimentExecutionResult result)
    {
        store.CompleteAndTrace(conversationId, turnId, result);
    }

    public void Cancel(string conversationId, string turnId)
    {
        _ = store.GetTurn(conversationId, turnId);
        if (_active.TryGetValue(turnId, out var active))
            try { active.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* A concurrently finished turn is already stopped. */ }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_active.Values.Select(v => v.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() => _stopping.Dispose();
}

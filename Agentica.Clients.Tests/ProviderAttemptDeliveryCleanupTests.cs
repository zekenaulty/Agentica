using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class ProviderAttemptDeliveryCleanupTests
{
    [Theory]
    [InlineData("late_failure")]
    [InlineData("cancel")]
    [InlineData("dispose_failure")]
    [InlineData("duplicate")]
    public async Task Undelivered_native_carriers_are_disposed_on_all_terminal_failures(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var first = Carrier();
        var second = Carrier();
        var source = new TerminalSource(mode, first, second, cancellation);
        var request = new LlmRequest("fixture", [new(LlmMessageRole.User, "fixture")])
        { AttemptCapture = new(new("fixture-attempt")) };
        var events = new List<LlmStreamEvent>();
        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var item in LlmProviderAttempt.StreamAsync(request, "fixture", "fixture",
                _ => source, cancellation.Token)) events.Add(item);
        });
        Assert.NotNull(failure);
        Assert.DoesNotContain(events, item => item.Kind == LlmStreamEventKind.Completed);
        Assert.Throws<ObjectDisposedException>(() => first.HistoryStepsJson);
        if (mode == "duplicate") Assert.Throws<ObjectDisposedException>(() => second.HistoryStepsJson);
        else second.Dispose();
        Assert.NotEqual(LlmAttemptOutcome.Succeeded, request.AttemptCapture.Latest!.Outcome);
    }

    [Fact]
    public async Task Delivered_native_carrier_remains_owned_by_caller()
    {
        using var cancellation = new CancellationTokenSource();
        using var first = Carrier();
        using var second = Carrier();
        var source = new TerminalSource("success", first, second, cancellation);
        var request = new LlmRequest("fixture", [new(LlmMessageRole.User, "fixture")]);
        LlmResponse? delivered = null;
        await foreach (var item in LlmProviderAttempt.StreamAsync(request, "fixture", "fixture", _ => source))
            delivered = item.Response;
        Assert.Same(first, delivered!.NativeContinuation);
        Assert.Equal("[{}]", first.HistoryStepsJson);
    }

    private static LlmNativeContinuation Carrier() => new("fixture", "fixture", "", "[{}]");

    private sealed class TerminalSource(string mode, LlmNativeContinuation first,
        LlmNativeContinuation second, CancellationTokenSource cancellation) :
        IAsyncEnumerable<LlmStreamEvent>, IAsyncEnumerator<LlmStreamEvent>
    {
        private int _index;
        public LlmStreamEvent Current { get; private set; } = null!;
        public IAsyncEnumerator<LlmStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            _index++;
            if (_index == 1 || _index == 2 && mode == "duplicate")
            {
                Current = new(LlmStreamEventKind.Completed, Response: new("fixture", "fixture", "ok",
                    NativeContinuation: _index == 1 ? first : second));
                return ValueTask.FromResult(true);
            }
            if (mode == "late_failure") throw new IOException("fixture failure after terminal");
            if (mode == "cancel")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(false);
        }
        public ValueTask DisposeAsync() => mode == "dispose_failure"
            ? ValueTask.FromException(new IOException("fixture disposal failure")) : ValueTask.CompletedTask;
    }
}

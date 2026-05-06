namespace Agentica.Clients.Llm;

public sealed record LlmRetryOptions(
    int MaxAttempts = 3,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    TimeSpan? CallTimeout = null,
    bool UseJitter = true)
{
    public static LlmRetryOptions Default { get; } = new();

    public static LlmRetryOptions None { get; } = new(
        MaxAttempts: 1,
        BaseDelay: TimeSpan.Zero,
        MaxDelay: TimeSpan.Zero,
        CallTimeout: TimeSpan.FromMinutes(10),
        UseJitter: false);

    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromMilliseconds(500);

    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(3);

    public TimeSpan EffectiveCallTimeout => CallTimeout ?? TimeSpan.FromMinutes(10);

    public int EffectiveMaxAttempts => Math.Max(1, MaxAttempts);
}

using System.Security.Cryptography;

namespace Agentica.Lab.Web.Chat;

public sealed record ExperimentTokenCount(int Tokens, string Method, string RequestHash,
    bool ProviderCount = false, string? CountRequestHash = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CountAttemptId { get; init; }
}

/// <summary>A turn-owned input measurement. Counting never generates a response.</summary>
public interface IExperimentInputTokenCounter
{
    Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);
    IReadOnlyList<ExperimentTokenCount> Measurements { get; }
}

public interface IExperimentInputCounterSource
{
    IExperimentInputTokenCounter CreateInputTokenCounter(ExperimentSettings settings);
}

/// <summary>Compatibility strategy for explicit provider-free callers of the synchronous compiler.</summary>
internal sealed class ExperimentByteProxyCounter : IExperimentInputTokenCounter
{
    internal static readonly ExperimentByteProxyCounter Instance = new();
    public IReadOnlyList<ExperimentTokenCount> Measurements => [];
    public Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExperimentTokenCount(checked(request.Length + 512),
            "legacy-utf8-byte-proxy; not model tokens; provider-free compatibility only",
            Convert.ToHexStringLower(SHA256.HashData(request.Span))));
    }
}

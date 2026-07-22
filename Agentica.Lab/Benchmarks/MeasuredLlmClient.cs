using System.Globalization;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;

namespace Agentica.Lab.Benchmarks;

public sealed class MeasuredLlmClient : ILlmClient
{
    public const string CallIdVersion = "benchmark-llm-call-v1";
    public const string RepairAttemptMetadataKey = "agentica.planner.repairAttempt";
    public const string RepairKindMetadataKey = "agentica.planner.repairKind";
    public const string RetryAttemptsMetadataKey = "llm.retry.attempts";

    private readonly ILlmClient _inner;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly List<BenchmarkLlmCallTelemetry> _calls = [];

    public MeasuredLlmClient(
        ILlmClient inner,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAtUtc = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();
        var callId = $"{CallIdVersion}/{Guid.NewGuid():N}";

        try
        {
            var response = await _inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
            Record(new BenchmarkLlmCallTelemetry(
                CallId: callId,
                StartedAtUtc: startedAtUtc,
                Latency: _timeProvider.GetElapsedTime(startedTimestamp),
                Succeeded: true,
                RequestedModelId: request.ModelId,
                ProviderName: response.ProviderName,
                ResponseModelId: response.ModelId,
                FinishReason: response.FinishReason,
                Usage: response.Usage,
                PromptVersion: Metadata(request, WorkflowPlanPromptBuilder.PromptVersionMetadataKey),
                SchemaVersion: Metadata(request, WorkflowPlanPromptBuilder.SchemaVersionMetadataKey),
                RequestKind: Metadata(request, WorkflowPlanPromptBuilder.RequestKindMetadataKey),
                IsRepair: IsRepair(request),
                RetryAttempts: RetryAttempts(response.Metadata),
                ErrorClass: null));
            return response;
        }
        catch (Exception exception)
        {
            var clientException = exception as LlmClientException;
            Record(new BenchmarkLlmCallTelemetry(
                CallId: callId,
                StartedAtUtc: startedAtUtc,
                Latency: _timeProvider.GetElapsedTime(startedTimestamp),
                Succeeded: false,
                RequestedModelId: request.ModelId,
                ProviderName: clientException?.ProviderName,
                ResponseModelId: null,
                FinishReason: LlmFinishReason.Error,
                Usage: null,
                PromptVersion: Metadata(request, WorkflowPlanPromptBuilder.PromptVersionMetadataKey),
                SchemaVersion: Metadata(request, WorkflowPlanPromptBuilder.SchemaVersionMetadataKey),
                RequestKind: Metadata(request, WorkflowPlanPromptBuilder.RequestKindMetadataKey),
                IsRepair: IsRepair(request),
                RetryAttempts: clientException?.Attempts ?? 1,
                ErrorClass: clientException?.ErrorClass ?? exception.GetType().Name));
            throw;
        }
    }

    public IReadOnlyList<BenchmarkLlmCallTelemetry> Snapshot()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_calls.ToArray());
        }
    }

    private void Record(BenchmarkLlmCallTelemetry telemetry)
    {
        lock (_gate)
        {
            _calls.Add(telemetry);
        }
    }

    private static string? Metadata(LlmRequest request, string key) =>
        request.Metadata is not null && request.Metadata.TryGetValue(key, out var value)
            ? value
            : null;

    private static bool IsRepair(LlmRequest request) =>
        !string.IsNullOrWhiteSpace(Metadata(request, RepairAttemptMetadataKey)) ||
        !string.IsNullOrWhiteSpace(Metadata(request, RepairKindMetadataKey));

    private static int RetryAttempts(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(RetryAttemptsMetadataKey, out var raw))
        {
            return 1;
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var attempts) && attempts > 0
            ? attempts
            : 0;
    }
}

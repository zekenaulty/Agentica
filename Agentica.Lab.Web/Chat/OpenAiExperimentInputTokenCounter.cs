using System.Security.Cryptography;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.Lab.Web.Chat;

/// <summary>Turn-owned cache and limits over the shared, separately admitted native counter.</summary>
public sealed class OpenAiExperimentInputTokenCounter : IExperimentInputTokenCounter, IDisposable
{
    private readonly OpenAiInputTokenCounter _counter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _measurementGate = new();
    private readonly Dictionary<string, ExperimentTokenCount> _cache = new(StringComparer.Ordinal);
    private readonly List<ExperimentTokenCount> _measurements = [];
    private readonly CountAdmission _admission;
    private readonly string _scope = "lab-count:" + Guid.NewGuid().ToString("N");

    public OpenAiExperimentInputTokenCounter(HttpClient borrowed, string apiKey, int maximumCalls = 132)
    {
        _counter = new OpenAiInputTokenCounter(borrowed, new(ApiKey: apiKey), maximumCalls);
        _admission = new(maximumCalls);
    }

    public IReadOnlyList<ExperimentTokenCount> Measurements
    {
        get { lock (_measurementGate) return _measurements.ToArray(); }
    }

    public void Dispose() => _gate.Dispose();

    public async Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var owned = request.ToArray();
        try
        {
            OpenAiInputTokenCounter.ValidateGenerationBody(owned);
            var requestHash = Convert.ToHexStringLower(SHA256.HashData(owned));
            if (_cache.TryGetValue(requestHash, out var cached)) return cached;
            var generation = new LlmAttemptReferences("lab-count-parent:" + Guid.NewGuid().ToString("N"), "chat.input", "compile");
            var capture = new LlmAttemptCapture(new("lab-count:" + Guid.NewGuid().ToString("N"), "chat.input", "count"));
            var result = await _counter.CountAsync(owned, generation, _scope, capture, _admission, cancellationToken).ConfigureAwait(false);
            var measured = new ExperimentTokenCount(result.Tokens, result.Method, requestHash,
                ProviderCount: true, CountRequestHash: result.CountRequest.BodyHash["sha256:".Length..]);
            _cache.Add(requestHash, measured);
            lock (_measurementGate) _measurements.Add(measured);
            return measured;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExperimentException("context.token_count_unavailable",
                "OpenAI input-token counting did not complete. No automatic retry or generation request was dispatched.", 503);
        }
        catch (LlmClientException error)
        {
            var code = error.ErrorClass switch
            {
                "input_count_invalid_request" or "input_count_binding_mismatch" => "context.token_count_invalid",
                "input_count_limit" => "context.token_count_limit",
                _ => "context.token_count_unavailable"
            };
            throw new ExperimentException(code,
                "OpenAI input-token counting is unavailable, invalid or exceeds its separate allowance. No automatic retry or generation request was dispatched.", 503);
        }
        finally { CryptographicOperations.ZeroMemory(owned); _gate.Release(); }
    }

    private sealed class CountAdmission(int maximumCalls) : ILlmCallAdmission
    {
        private int _calls;
        public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.WorkKind != LlmProviderWorkKind.InputCount || Interlocked.Increment(ref _calls) > maximumCalls)
                throw new LlmClientException("openai", "Input-count allowance exhausted.", errorClass: "input_count_limit");
            return ValueTask.CompletedTask;
        }
    }
}

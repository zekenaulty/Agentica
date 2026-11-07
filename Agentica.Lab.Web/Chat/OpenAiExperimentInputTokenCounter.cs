using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.Lab.Web.Chat;

/// <summary>Turn-owned cache and limits over the shared, separately admitted native counter.</summary>
public sealed class OpenAiExperimentInputTokenCounter : IExperimentInputTokenCounter, IExperimentInputCountEvidenceSource, IDisposable
{
    private readonly OpenAiInputTokenCounter _counter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _measurementGate = new();
    private readonly Dictionary<string, ExperimentTokenCount> _cache = new(StringComparer.Ordinal);
    private readonly List<ExperimentTokenCount> _measurements = [];
    private readonly CountAdmission _admission;
    private readonly IExperimentInputCountJournal _journal;
    private readonly ExperimentBorrowedTransportBinding _binding;
    private readonly string _credential;
    private readonly List<ExperimentInputCountRecord> _attempts = [];
    private readonly string _scope = "lab-count:" + Guid.NewGuid().ToString("N");

    public OpenAiExperimentInputTokenCounter(HttpClient borrowed, string apiKey, int maximumCalls = 132, IExperimentInputCountJournal? journal = null)
    {
        _counter = new OpenAiInputTokenCounter(borrowed, new(ApiKey: apiKey), maximumCalls);
        _credential = apiKey;
        _journal = journal ?? new ExperimentComponentCountJournal();
        _binding = new(borrowed);
        _admission = new(maximumCalls, _journal, _binding);
    }

    public IReadOnlyList<ExperimentTokenCount> Measurements
    {
        get { lock (_measurementGate) return _measurements.ToArray(); }
    }

    public string ScopeId => _scope;
    public IReadOnlyList<ExperimentInputCountRecord> CountAttempts
    { get { lock (_measurementGate) return _attempts.ToArray(); } }
    public void Dispose() => _gate.Dispose();

    public async Task<ExperimentTokenCount> CountAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var owned = request.ToArray();
        LlmAttemptCapture? capture = null;
        _admission.Request = null;
        try
        {
            _binding.Validate();
            OpenAiInputTokenCounter.ValidateGenerationBody(owned);
            var requestHash = Convert.ToHexStringLower(SHA256.HashData(owned));
            if (_cache.TryGetValue(requestHash, out var cached)) return cached;
            var generation = new LlmAttemptReferences("lab-count-parent:" + Guid.NewGuid().ToString("N"), "chat.input", "compile", routeFingerprint: _binding.Identity);
            capture = new LlmAttemptCapture(new("lab-count:" + Guid.NewGuid().ToString("N"), "chat.input", "count", routeFingerprint: _binding.Identity));
            var result = await _counter.CountAsync(owned, generation, _scope, capture, _admission, cancellationToken).ConfigureAwait(false);
            var measured = new ExperimentTokenCount(result.Tokens, result.Method, requestHash,
                ProviderCount: true, CountRequestHash: result.CountRequest.BodyHash["sha256:".Length..])
            { CountAttemptId = capture.References.AttemptId };
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
            var code = error.InnerException is ExperimentException { Code: "provider.transport_changed" }
                ? "provider.transport_changed" : error.ErrorClass switch
                {
                    "input_count_invalid_request" or "input_count_binding_mismatch" => "context.token_count_invalid",
                    "input_count_limit" => "context.token_count_limit",
                    _ => "context.token_count_unavailable"
                };
            throw new ExperimentException(code,
                "OpenAI input-token counting is unavailable, invalid or exceeds its separate allowance. No automatic retry or generation request was dispatched.", 503);
        }
        finally
        {
            try { await PersistOutcomeAsync(capture, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(owned); _gate.Release(); }
        }
    }

    private async Task PersistOutcomeAsync(LlmAttemptCapture? capture, CancellationToken cancellationToken)
    {
        try
        {
            if (capture?.Latest is { } observed && _admission.Request is { } admitted)
            {
                var safe = ExperimentProviderTransport.Sanitize(JsonSerializer.SerializeToElement(observed), _credential);
                lock (_measurementGate) _attempts.Add(new(admitted, safe));
                // Required durable finalization survives caller cancellation and
                // optional native/UI failures. Its reservation survives a crash.
                await _journal.ObserveAsync(admitted, safe).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            _cache.Clear();
            cancellationToken.ThrowIfCancellationRequested();
            throw new ExperimentException("context.count_journal_failed", "Required native-count evidence could not be persisted. Generation remains unadmitted.", 503);
        }
    }

    private sealed class CountAdmission(int maximumCalls, IExperimentInputCountJournal journal, ExperimentBorrowedTransportBinding binding) : ILlmCallAdmission
    {
        private int _calls;
        public LlmPreparedRequestDescriptor? Request { get; set; }
        public async ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.WorkKind != LlmProviderWorkKind.InputCount || Interlocked.Increment(ref _calls) > maximumCalls)
                throw new LlmClientException("openai", "Input-count allowance exhausted.", errorClass: "input_count_limit");
            Request = request;
            binding.Validate();
            await journal.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
            binding.Validate();
        }
    }
}

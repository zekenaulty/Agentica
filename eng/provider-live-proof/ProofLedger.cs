using System.Security.Cryptography;
using System.Text.Json;

namespace Agentica.ProviderLiveProof;

internal sealed record ProofLimits(int InferenceCalls = 6, int CountCalls = 8, decimal MaximumUsd = 5m,
    int InputTokens = 8000, int OutputTokens = 8192);

internal sealed class ProviderLedger
{
    public string Contract { get; set; } = "agentica.live-proof.20261106.v1";
    public ProofLimits Limits { get; set; } = new();
    public string Model { get; set; } = "gpt-6-luna";
    public string Effort { get; set; } = "high";
    public string PricingVerifiedOn { get; set; } = "2026-11-06";
    public decimal InputUsdPerMillion { get; set; } = .25m;
    public decimal OutputUsdPerMillion { get; set; } = .75m;
    public decimal SearchUsdPerCall { get; set; } = .01m;
    public decimal CountUsd { get; set; }
    public string[] PricingSources { get; set; } =
    ["https://developers.openai.com/api/docs/models/gpt-6-luna", "https://developers.openai.com/api/docs/pricing",
     "https://help.openai.com/en/articles/4936856-understanding-and-counting-tokens"];
    public List<ProofAttempt> Attempts { get; set; } = [];
    public List<VerifiedInput> VerifiedInputs { get; set; } = [];
    public decimal HeldUsd => Attempts.Sum(item => Math.Max(item.SettledUsd ?? item.ReservedUsd, KnownExposure(item)));
    internal decimal KnownExposure(ProofAttempt item) => item.Kind == "count" ? 0 :
        (item.InputTokens ?? 0) * InputUsdPerMillion / 1_000_000m +
        (item.OutputTokens ?? 0) * OutputUsdPerMillion / 1_000_000m +
        (item.NativeSearchCalls ?? 0) * SearchUsdPerCall;
}

internal sealed class ProofAttempt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Kind { get; set; }
    public required string BodyHash { get; set; }
    public required string GenerationBodyHash { get; set; }
    public decimal ReservedUsd { get; set; }
    public decimal? SettledUsd { get; set; }
    public string State { get; set; } = "dispatch-possible";
    public DateTimeOffset ReservedAt { get; set; } = DateTimeOffset.UtcNow;
    public int? HttpStatus { get; set; }
    public string? ProviderRequestId { get; set; }
    public string? ResponseId { get; set; }
    public string? ProviderStatus { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? MeasuredInputTokens { get; set; }
    public int? NativeSearchCalls { get; set; }
    public int ReservedSearchCalls { get; set; }
    public bool StreamDrained { get; set; }
}

internal sealed record VerifiedInput(string GenerationBodyHash, string CountBodyHash, int Tokens,
    string CountAttemptId, DateTimeOffset VerifiedAt);

/// <summary>Exclusive process custody and atomic synchronous persistence before physical send.
/// The ledger stores structural hashes/counters only, never credentials, prompts or native output.</summary>
internal sealed class ProofJournal : IDisposable
{
    private readonly string _path;
    private readonly FileStream _custody;
    private readonly object _gate = new();
    private readonly ProviderLedger _ledger;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ProofJournal(string path, ProofLimits? limits = null)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var policy = limits ?? new ProofLimits();
        if (policy.InferenceCalls is < 1 or > 6 || policy.CountCalls is < 1 or > 8 ||
            policy.MaximumUsd is <= 0 or > 5 || policy.InputTokens is < 1 or > 8000 || policy.OutputTokens is < 1 or > 8192)
            throw new InvalidOperationException("proof.invalid_limits");
        _custody = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _ledger = File.Exists(_path) ? JsonSerializer.Deserialize<ProviderLedger>(File.ReadAllBytes(_path), Json)
                ?? throw new InvalidDataException("proof.invalid_ledger") : new() { Limits = policy };
            if (_ledger.Contract != "agentica.live-proof.20261106.v1" || _ledger.Limits != policy ||
                _ledger.Model != "gpt-6-luna" || _ledger.Effort != "high" || _ledger.InputUsdPerMillion != .25m ||
                _ledger.OutputUsdPerMillion != .75m || _ledger.SearchUsdPerCall != .01m || _ledger.CountUsd != 0 ||
                _ledger.Attempts.Count > policy.InferenceCalls + policy.CountCalls ||
                _ledger.Attempts.Any(item => item.Kind is not ("count" or "inference") || item.ReservedUsd < 0 ||
                    item.SettledUsd < 0 || item.InputTokens < 0 || item.OutputTokens < 0 || item.ReasoningTokens < 0 ||
                    item.NativeSearchCalls < 0 || item.ReservedSearchCalls != 0 || !Hash(item.BodyHash) || !Hash(item.GenerationBodyHash)) ||
                _ledger.Attempts.Count(item => item.Kind == "inference") > policy.InferenceCalls ||
                _ledger.Attempts.Count(item => item.Kind == "count") > policy.CountCalls ||
                _ledger.Attempts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != _ledger.Attempts.Count)
                throw new InvalidDataException("proof.ledger_policy_mismatch");
            if (_ledger.VerifiedInputs.Count > policy.CountCalls || _ledger.VerifiedInputs.Any(item => !ValidMeasurement(item)) ||
                _ledger.VerifiedInputs.Select(item => item.GenerationBodyHash).Distinct(StringComparer.Ordinal).Count() != _ledger.VerifiedInputs.Count)
                throw new InvalidDataException("proof.invalid_count_link");
            Save();
        }
        catch { _custody.Dispose(); throw; }
    }

    public ProviderLedger Snapshot()
    { lock (_gate) return JsonSerializer.Deserialize<ProviderLedger>(JsonSerializer.SerializeToUtf8Bytes(_ledger, Json), Json)!; }

    public string Reserve(string kind, string bodyHash, string generationHash, int maximumOutput, int searchCalls)
    {
        lock (_gate)
        {
            if (kind is not ("count" or "inference") || !Hash(bodyHash) || !Hash(generationHash) ||
                maximumOutput < 0 || maximumOutput > _ledger.Limits.OutputTokens || searchCalls != 0)
                throw new InvalidOperationException("proof.invalid_reservation");
            if (kind == "inference" && _ledger.Attempts.Any(item => item.Kind == kind && item.GenerationBodyHash == generationHash))
                throw new InvalidOperationException("proof.duplicate_generation");
            if (_ledger.Attempts.Any(item => item.State == "reservation-exceeded" || _ledger.KnownExposure(item) > item.ReservedUsd))
                throw new InvalidOperationException("proof.reservation_exceeded");
            var cap = kind == "count" ? _ledger.Limits.CountCalls : _ledger.Limits.InferenceCalls;
            if (_ledger.Attempts.Count(item => item.Kind == kind) >= cap) throw new InvalidOperationException("proof.physical_call_limit");
            var reserve = kind == "count" ? 0 :
                _ledger.Limits.InputTokens * _ledger.InputUsdPerMillion / 1_000_000m +
                maximumOutput * _ledger.OutputUsdPerMillion / 1_000_000m + searchCalls * _ledger.SearchUsdPerCall;
            if (_ledger.HeldUsd + reserve > _ledger.Limits.MaximumUsd) throw new InvalidOperationException("proof.dollar_reservation_limit");
            var attempt = new ProofAttempt
            { Kind = kind, BodyHash = bodyHash, GenerationBodyHash = generationHash, ReservedUsd = reserve, ReservedSearchCalls = searchCalls };
            _ledger.Attempts.Add(attempt);
            try { Save(); }
            catch { _ledger.Attempts.Remove(attempt); throw; }
            return attempt.Id;
        }
    }

    public void Observe(string id, Action<ProofAttempt> update)
    { lock (_gate) { update(_ledger.Attempts.Single(item => item.Id == id)); Save(); } }

    public VerifiedInput? FindCount(string generationHash)
    {
        lock (_gate) return _ledger.VerifiedInputs.LastOrDefault(item => item.GenerationBodyHash == generationHash &&
            ValidMeasurement(item) && DateTimeOffset.UtcNow - item.VerifiedAt <= TimeSpan.FromMinutes(30));
    }

    public void LinkMeasurement(string generationHash, int tokens, string countHash)
    {
        lock (_gate)
        {
            if (tokens < 0 || tokens > _ledger.Limits.InputTokens) throw new InvalidOperationException("proof.native_input_limit");
            var observed = _ledger.Attempts.LastOrDefault(item => item.Kind == "count" &&
                item.GenerationBodyHash == generationHash && item.BodyHash == countHash &&
                item.HttpStatus is >= 200 and <= 299 && item.MeasuredInputTokens == tokens && item.State == "count-response-valid");
            if (observed is null) throw new InvalidOperationException("proof.unverified_native_count");
            _ledger.VerifiedInputs.RemoveAll(item => item.GenerationBodyHash == generationHash);
            _ledger.VerifiedInputs.Add(new(generationHash, countHash, tokens, observed.Id, DateTimeOffset.UtcNow));
            Save();
        }
    }

    public void FinishInference(string id, bool drained, bool ambiguous)
    {
        Observe(id, attempt =>
        {
            attempt.StreamDrained = drained;
            if (drained && !ambiguous && attempt.ProviderStatus is ("completed" or "failed" or "incomplete") &&
                attempt.InputTokens is >= 0 && attempt.OutputTokens is >= 0 &&
                (attempt.ReservedSearchCalls == 0 || attempt.NativeSearchCalls is not null))
            {
                // Responses output includes reasoning. Never add ReasoningTokens again.
                attempt.SettledUsd = attempt.InputTokens.Value * _ledger.InputUsdPerMillion / 1_000_000m +
                    attempt.OutputTokens.Value * _ledger.OutputUsdPerMillion / 1_000_000m +
                    (attempt.NativeSearchCalls ?? 0) * _ledger.SearchUsdPerCall;
                attempt.State = "provider-terminal-observed";
                if (attempt.SettledUsd > attempt.ReservedUsd) attempt.State = "reservation-exceeded";
            }
            else attempt.State = "unresolved-reservation-retained";
            if (_ledger.KnownExposure(attempt) > attempt.ReservedUsd) attempt.State = "reservation-exceeded";
        });
    }

    private void Save()
    {
        var temporary = _path + ".next";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(JsonSerializer.SerializeToUtf8Bytes(_ledger, Json));
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    private bool ValidMeasurement(VerifiedInput item)
    {
        if (!Hash(item.GenerationBodyHash) || !Hash(item.CountBodyHash) || item.Tokens < 0 ||
            item.Tokens > _ledger.Limits.InputTokens || item.VerifiedAt > DateTimeOffset.UtcNow) return false;
        return _ledger.Attempts.Any(attempt => attempt.Id == item.CountAttemptId && attempt.Kind == "count" &&
            attempt.BodyHash == item.CountBodyHash && attempt.GenerationBodyHash == item.GenerationBodyHash &&
            attempt.MeasuredInputTokens == item.Tokens && attempt.HttpStatus is >= 200 and <= 299 &&
            attempt.State == "count-response-valid" && attempt.ReservedAt <= item.VerifiedAt);
    }

    private static bool Hash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    public void Dispose() => _custody.Dispose();
    internal static string BodyHash(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));
}

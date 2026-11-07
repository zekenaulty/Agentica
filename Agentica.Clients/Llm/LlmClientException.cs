using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public sealed class LlmClientException : Exception
{
    private LlmWebSearchEvidence? _webSearch;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmWebSearchEvidence? WebSearch
    {
        get => Volatile.Read(ref _webSearch);
        init => _webSearch = value;
    }
    internal void RetainWebSearch(LlmWebSearchEvidence? evidence) => Interlocked.CompareExchange(ref _webSearch, evidence, null);

    private LlmAttemptEvidence? _attemptEvidence;

    public LlmClientException(
        string providerName,
        string message,
        Exception? innerException = null,
        LlmClientErrorKind errorKind = LlmClientErrorKind.Unknown,
        int? statusCode = null,
        int attempts = 1,
        string? errorClass = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
        ErrorKind = errorKind;
        StatusCode = statusCode;
        Attempts = attempts;
        ErrorClass = errorClass ?? errorKind.ToString();
    }

    public string ProviderName { get; }

    public LlmClientErrorKind ErrorKind { get; }

    public int? StatusCode { get; }

    public int Attempts { get; }

    public string ErrorClass { get; }

    /// <summary>Facts already observed before the error, independent of output acceptance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmAttemptEvidence? AttemptEvidence
    {
        get => Volatile.Read(ref _attemptEvidence);
        init => _attemptEvidence = value;
    }

    internal void RetainAttemptEvidence(LlmAttemptEvidence evidence, LlmAttemptEvidence? replaceIfSame = null)
    {
        var previous = Volatile.Read(ref _attemptEvidence);
        if (previous is null || ReferenceEquals(previous, replaceIfSame))
            Interlocked.CompareExchange(ref _attemptEvidence, evidence, previous);
    }
}

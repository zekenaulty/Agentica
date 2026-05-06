namespace Agentica.Clients.Llm;

public sealed class LlmClientException : Exception
{
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
}

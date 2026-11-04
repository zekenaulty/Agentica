namespace Agentica.Clients.Embeddings;

/// <summary>Sanitized failure metadata. No source text, credentials or remote error body is retained.</summary>
public sealed class EmbeddingClientException : Exception
{
    internal EmbeddingClientException(string errorCode, int? statusCode = null)
        : base("OpenAI embedding request failed (" + errorCode + ").")
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ProviderId => OpenAiEmbeddingClient.ProviderId;
    public string ErrorCode { get; }
    public int? StatusCode { get; }
}

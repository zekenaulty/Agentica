using System.Net;
using System.Net.Http;

namespace Agentica.Clients.Llm;

public sealed class RetryingLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly LlmRetryOptions _options;
    private readonly Random _random = new();

    public RetryingLlmClient(ILlmClient inner, LlmRetryOptions? options = null)
    {
        _inner = inner;
        _options = options ?? LlmRetryOptions.Default;
    }

    public async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callTimeout.CancelAfter(_options.EffectiveCallTimeout);
        var callToken = callTimeout.Token;
        var reasons = new List<string>();
        LlmClientException? lastClientException = null;
        Exception? lastException = null;
        var maxAttempts = _options.EffectiveMaxAttempts;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await _inner.GenerateAsync(request, callToken).ConfigureAwait(false);
                return attempt == 1
                    ? response
                    : response with
                    {
                        Metadata = MergeRetryMetadata(
                            response.Metadata,
                            attempt,
                            reasons)
                    };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (callTimeout.IsCancellationRequested)
            {
                throw CallTimedOut(request, attempt, exception);
            }
            catch (OperationCanceledException exception)
            {
                lastException = exception;
                var reason = "operation_canceled_without_caller_cancellation";
                reasons.Add(reason);

                if (attempt >= maxAttempts)
                {
                    throw Exhausted(
                        request,
                        providerName: "unknown",
                        LlmClientErrorKind.Transient,
                        statusCode: null,
                        attempts: attempt,
                        errorClass: reason,
                        lastMessage: exception.Message,
                        innerException: exception);
                }

                await DelayOrThrowTimeoutAsync(request, attempt, callTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmClientException exception) when (ShouldRetry(exception, cancellationToken))
            {
                lastClientException = exception;
                reasons.Add(ReasonFor(exception));

                if (attempt >= maxAttempts)
                {
                    throw Exhausted(
                        request,
                        exception.ProviderName,
                        exception.ErrorKind,
                        exception.StatusCode,
                        attempt,
                        exception.ErrorClass,
                        exception.Message,
                        exception);
                }

                await DelayOrThrowTimeoutAsync(request, attempt, callTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (ShouldRetry(exception, cancellationToken))
            {
                lastException = exception;
                var errorKind = ClassifyHttp(exception.StatusCode);
                var reason = $"{errorKind.ToString().ToLowerInvariant()}:{StatusCodeText(exception.StatusCode)}";
                reasons.Add(reason);

                if (attempt >= maxAttempts)
                {
                    throw Exhausted(
                        request,
                        providerName: "unknown",
                        errorKind,
                        statusCode: exception.StatusCode is null ? null : (int)exception.StatusCode.Value,
                        attempts: attempt,
                        errorClass: errorKind.ToString(),
                        lastMessage: exception.Message,
                        innerException: exception);
                }

                await DelayOrThrowTimeoutAsync(request, attempt, callTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableTransientException(exception, cancellationToken))
            {
                lastException = exception;
                var reason = exception.GetType().Name;
                reasons.Add(reason);

                if (attempt >= maxAttempts)
                {
                    throw Exhausted(
                        request,
                        providerName: "unknown",
                        LlmClientErrorKind.Transient,
                        statusCode: null,
                        attempts: attempt,
                        errorClass: reason,
                        lastMessage: exception.Message,
                        innerException: exception);
                }

                await DelayOrThrowTimeoutAsync(request, attempt, callTimeout, cancellationToken).ConfigureAwait(false);
            }
        }

        var last = lastClientException ?? lastException;
        throw Exhausted(
            request,
            lastClientException?.ProviderName ?? "unknown",
            lastClientException?.ErrorKind ?? LlmClientErrorKind.Unknown,
            lastClientException?.StatusCode,
            maxAttempts,
            lastClientException?.ErrorClass ?? last?.GetType().Name ?? "unknown",
            last?.Message ?? "No provider response was returned.",
            last);
    }

    private async Task DelayOrThrowTimeoutAsync(
        LlmRequest request,
        int attempt,
        CancellationTokenSource callTimeout,
        CancellationToken callerToken)
    {
        var delay = ComputeDelay(attempt);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(delay, callTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (callTimeout.IsCancellationRequested)
        {
            throw CallTimedOut(request, attempt, exception);
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var baseDelayMs = _options.EffectiveBaseDelay.TotalMilliseconds;
        if (baseDelayMs <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponential = baseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exponential, _options.EffectiveMaxDelay.TotalMilliseconds);
        if (_options.UseJitter)
        {
            capped *= 0.75 + (_random.NextDouble() * 0.5);
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, capped));
    }

    private static bool ShouldRetry(LlmClientException exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception.InnerException is OperationCanceledException)
        {
            return true;
        }

        return exception.ErrorKind is
            LlmClientErrorKind.Transient or
            LlmClientErrorKind.Network or
            LlmClientErrorKind.RateLimited or
            LlmClientErrorKind.ServerError;
    }

    private static bool ShouldRetry(HttpRequestException exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        ClassifyHttp(exception.StatusCode) is
            LlmClientErrorKind.Network or
            LlmClientErrorKind.RateLimited or
            LlmClientErrorKind.ServerError;

    private static bool IsRetryableTransientException(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is TimeoutException || exception.InnerException is TimeoutException;
    }

    private static LlmClientErrorKind ClassifyHttp(HttpStatusCode? statusCode) =>
        statusCode switch
        {
            null => LlmClientErrorKind.Network,
            HttpStatusCode.TooManyRequests => LlmClientErrorKind.RateLimited,
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout => LlmClientErrorKind.ServerError,
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden => LlmClientErrorKind.Authentication,
            HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or
            HttpStatusCode.UnprocessableEntity => LlmClientErrorKind.BadRequest,
            _ => LlmClientErrorKind.Unknown
        };

    private static IReadOnlyDictionary<string, string> MergeRetryMetadata(
        IReadOnlyDictionary<string, string>? existing,
        int attempts,
        IReadOnlyList<string> reasons)
    {
        var metadata = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing, StringComparer.Ordinal);

        metadata["llm.retry.attempts"] = attempts.ToString();
        metadata["llm.retry.reasons"] = string.Join(",", reasons);
        return metadata;
    }

    private static LlmClientException Exhausted(
        LlmRequest request,
        string providerName,
        LlmClientErrorKind errorKind,
        int? statusCode,
        int attempts,
        string errorClass,
        string lastMessage,
        Exception? innerException) =>
        new(
            providerName,
            $"LLM generation failed after {attempts} attempt(s). provider={providerName}; model={request.ModelId}; errorKind={errorKind}; statusCode={statusCode?.ToString() ?? "none"}; lastErrorClass={errorClass}; lastError={lastMessage}",
            innerException,
            errorKind,
            statusCode,
            attempts,
            errorClass);

    private LlmClientException CallTimedOut(
        LlmRequest request,
        int attempts,
        Exception innerException) =>
        new(
            "unknown",
            $"LLM generation timed out after {_options.EffectiveCallTimeout.TotalSeconds:0} second(s). provider=unknown; model={request.ModelId}; attempts={attempts}; lastErrorClass=llm_call_timeout",
            innerException,
            LlmClientErrorKind.Transient,
            statusCode: null,
            attempts,
            errorClass: "llm_call_timeout");

    private static string ReasonFor(LlmClientException exception) =>
        $"{exception.ErrorKind.ToString().ToLowerInvariant()}:{exception.ErrorClass}:{exception.StatusCode?.ToString() ?? "none"}";

    private static string StatusCodeText(HttpStatusCode? statusCode) =>
        statusCode is null ? "none" : ((int)statusCode.Value).ToString();
}

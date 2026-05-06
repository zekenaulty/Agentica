using Agentica.Clients.Llm;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace Agentica.Clients.Gemini;

internal static class GeminiExceptionClassifier
{
    public static GeminiErrorClassification Classify(Exception exception)
    {
        var statusCode = FindStatusCode(exception);
        if (statusCode is not null)
        {
            return new GeminiErrorClassification(
                ErrorKind: ClassifyStatusCode(statusCode.Value),
                StatusCode: statusCode,
                ErrorClass: $"http_{statusCode.Value}");
        }

        var message = exception.ToString();
        if (ContainsAny(message, "API key", "unauthenticated", "permission denied", "401", "403"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.Authentication, null, "authentication");
        }

        if (ContainsAny(message, "INVALID_ARGUMENT", "bad request", "400", "schema", "request payload"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.BadRequest, null, "bad_request");
        }

        if (ContainsAny(message, "RESOURCE_EXHAUSTED", "rate limit", "quota", "429"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.RateLimited, null, "rate_limited");
        }

        if (ContainsAny(message, "UNAVAILABLE", "DEADLINE_EXCEEDED", "timeout", "temporar", "operation was canceled"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.Transient, null, "transient");
        }

        if (ContainsAny(message, "ServerError", "InternalServerError", "500", "502", "503", "504"))
        {
            return new GeminiErrorClassification(LlmClientErrorKind.ServerError, null, "server_error");
        }

        if (exception is HttpRequestException)
        {
            return new GeminiErrorClassification(LlmClientErrorKind.Network, null, "network");
        }

        return new GeminiErrorClassification(LlmClientErrorKind.Unknown, null, exception.GetType().Name);
    }

    public static string SafeMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;

        return message
            .Replace(System.Environment.NewLine, " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static LlmClientErrorKind ClassifyStatusCode(int statusCode) =>
        statusCode switch
        {
            401 or 403 => LlmClientErrorKind.Authentication,
            400 or 404 or 422 => LlmClientErrorKind.BadRequest,
            429 => LlmClientErrorKind.RateLimited,
            500 or 502 or 503 or 504 => LlmClientErrorKind.ServerError,
            _ => LlmClientErrorKind.Unknown
        };

    private static int? FindStatusCode(Exception exception)
    {
        if (exception is HttpRequestException httpRequestException &&
            httpRequestException.StatusCode is { } httpStatusCode)
        {
            return (int)httpStatusCode;
        }

        foreach (var propertyName in new[] { "StatusCode", "Status", "Code" })
        {
            var statusCode = FindStatusCodeProperty(exception, propertyName);
            if (statusCode is not null)
            {
                return statusCode;
            }
        }

        return exception.InnerException is null ? null : FindStatusCode(exception.InnerException);
    }

    private static int? FindStatusCodeProperty(Exception exception, string propertyName)
    {
        var properties = exception.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            .OrderByDescending(property => property.DeclaringType == exception.GetType())
            .ThenBy(property => property.DeclaringType?.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(exception);
            }
            catch (TargetInvocationException)
            {
                continue;
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (MethodAccessException)
            {
                continue;
            }

            var statusCode = ConvertStatusCode(value);
            if (statusCode is not null)
            {
                return statusCode;
            }
        }

        return null;
    }

    private static int? ConvertStatusCode(object? value) =>
        value switch
        {
            int intValue => intValue,
            HttpStatusCode statusCode => (int)statusCode,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };

    private static bool ContainsAny(string value, params string[] patterns) =>
        patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}

internal sealed record GeminiErrorClassification(
    LlmClientErrorKind ErrorKind,
    int? StatusCode,
    string ErrorClass);

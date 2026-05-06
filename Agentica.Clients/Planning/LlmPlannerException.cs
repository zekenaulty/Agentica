namespace Agentica.Clients.Planning;

public sealed class LlmPlannerException : Exception
{
    public LlmPlannerException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

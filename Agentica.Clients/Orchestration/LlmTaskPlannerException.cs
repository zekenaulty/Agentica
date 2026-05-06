namespace Agentica.Clients.Orchestration;

public sealed class LlmTaskPlannerException : InvalidOperationException
{
    public LlmTaskPlannerException(string message)
        : base(message)
    {
    }

    public LlmTaskPlannerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

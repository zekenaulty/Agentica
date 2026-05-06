namespace Agentica.Planning;

public sealed class WorkflowPlannerException : Exception
{
    public WorkflowPlannerException(
        WorkflowPlannerFailureKind failureKind,
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        Code = code;
    }

    public WorkflowPlannerFailureKind FailureKind { get; }

    public string Code { get; }
}

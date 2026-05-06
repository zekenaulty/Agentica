namespace Agentica.Outcomes;

public enum StopReason
{
    None,
    Complete,
    PlanInvalid,
    PlannerUnavailable,
    UnknownTool,
    ToolFailure,
    ToolUnavailable,
    CompletionNotSatisfied,
    ContinuationLimitReached,
    StepLimitReached,
    RefinementLimitReached,
    WaitingForApproval,
    Partial,
    Cancelled,
    Timeout
}

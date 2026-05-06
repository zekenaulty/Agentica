namespace Agentica.Events;

public enum ExecutionEventType
{
    RunCreated,
    RequestAccepted,
    PlanCreated,
    StepStarted,
    ObservationMade,
    ReceiptEmitted,
    PlanRefined,
    OutcomeReported,
    RunSucceeded,
    RunBlocked,
    RunFailed,
    RunStopped
}

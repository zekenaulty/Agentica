namespace Agentica.Events;

public enum ExecutionEventType
{
    RunCreated,
    RequestAccepted,
    PlanCreated,
    BatchStarted,
    BatchCompleted,
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

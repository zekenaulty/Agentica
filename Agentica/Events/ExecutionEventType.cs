namespace Agentica.Events;

public enum ExecutionEventType
{
    RunCreated,
    RequestAccepted,
    PlanCreationStarted,
    PlanCreationCancelled,
    PlanCreated,
    PlanContinuationStarted,
    PlanContinuationCancelled,
    BatchStarted,
    BatchCompleted,
    StepStarted,
    ObservationMade,
    ReceiptEmitted,
    PlanRefinementStarted,
    PlanRefinementCancelled,
    PlanRefined,
    OutcomeReported,
    RunSucceeded,
    RunBlocked,
    RunFailed,
    RunStopped
}

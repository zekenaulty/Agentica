namespace Agentica.Events;

public enum ExecutionEventType
{
    RunCreated = 0,
    RequestAccepted = 1,
    PlanCreationStarted = 2,
    PlanCreationCancelled = 3,
    PlanCreated = 4,
    PlanContinuationStarted = 5,
    PlanContinuationCancelled = 6,
    BatchStarted = 7,
    BatchCompleted = 8,
    StepStarted = 9,
    ObservationMade = 10,
    ReceiptEmitted = 11,
    PlanRefinementStarted = 12,
    PlanRefinementCancelled = 13,
    PlanRefined = 14,
    OutcomeReported = 15,
    RunSucceeded = 16,
    RunBlocked = 17,
    RunFailed = 18,
    RunStopped = 19,
    GrantConsumed = 20
}

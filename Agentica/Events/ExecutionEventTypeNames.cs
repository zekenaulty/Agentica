namespace Agentica.Events;

public static class ExecutionEventTypeNames
{
    public static string WireName(this ExecutionEventType type) =>
        type switch
        {
            ExecutionEventType.RunCreated => "run.created",
            ExecutionEventType.RequestAccepted => "request.accepted",
            ExecutionEventType.PlanCreationStarted => "plan.creation.started",
            ExecutionEventType.PlanCreationCancelled => "plan.creation.cancelled",
            ExecutionEventType.PlanCreated => "plan.created",
            ExecutionEventType.PlanContinuationStarted => "plan.continuation.started",
            ExecutionEventType.PlanContinuationCancelled => "plan.continuation.cancelled",
            ExecutionEventType.BatchStarted => "batch.started",
            ExecutionEventType.BatchCompleted => "batch.completed",
            ExecutionEventType.StepStarted => "step.started",
            ExecutionEventType.ObservationMade => "observation.made",
            ExecutionEventType.ReceiptEmitted => "receipt.emitted",
            ExecutionEventType.PlanRefinementStarted => "plan.refinement.started",
            ExecutionEventType.PlanRefinementCancelled => "plan.refinement.cancelled",
            ExecutionEventType.PlanRefined => "plan.refined",
            ExecutionEventType.OutcomeReported => "outcome.reported",
            ExecutionEventType.RunSucceeded => "run.succeeded",
            ExecutionEventType.RunBlocked => "run.blocked",
            ExecutionEventType.RunFailed => "run.failed",
            ExecutionEventType.RunStopped => "run.stopped",
            _ => type.ToString()
        };
}

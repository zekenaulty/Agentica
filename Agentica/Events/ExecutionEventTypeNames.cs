namespace Agentica.Events;

public static class ExecutionEventTypeNames
{
    public static string WireName(this ExecutionEventType type) =>
        type switch
        {
            ExecutionEventType.RunCreated => "run.created",
            ExecutionEventType.RequestAccepted => "request.accepted",
            ExecutionEventType.PlanCreated => "plan.created",
            ExecutionEventType.BatchStarted => "batch.started",
            ExecutionEventType.BatchCompleted => "batch.completed",
            ExecutionEventType.StepStarted => "step.started",
            ExecutionEventType.ObservationMade => "observation.made",
            ExecutionEventType.ReceiptEmitted => "receipt.emitted",
            ExecutionEventType.PlanRefined => "plan.refined",
            ExecutionEventType.OutcomeReported => "outcome.reported",
            ExecutionEventType.RunSucceeded => "run.succeeded",
            ExecutionEventType.RunBlocked => "run.blocked",
            ExecutionEventType.RunFailed => "run.failed",
            ExecutionEventType.RunStopped => "run.stopped",
            _ => type.ToString()
        };
}

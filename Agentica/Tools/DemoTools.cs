using Agentica.Artifacts;
using Agentica.Observations;

namespace Agentica.Tools;

public static class DemoTools
{
    public static ToolCatalog CreateCatalog() =>
        ToolCatalog.Create(
            new ToolRegistration(
                new ToolDescriptor(
                    ToolId: DemoToolIds.QueryState,
                    Name: "Query State",
                    Kind: ToolKind.Query,
                    Effect: ToolEffect.ReadOnly),
                new QueryStateTool()),
            new ToolRegistration(
                new ToolDescriptor(
                    ToolId: DemoToolIds.PerformAction,
                    Name: "Perform Action",
                    Kind: ToolKind.Action,
                    Effect: ToolEffect.WritesLocalState),
                new PerformActionTool()));
}

public sealed class QueryStateTool : ITool
{
    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var receipt = new Receipt(
            ReceiptId: AgenticaIds.New("receipt"),
            StepId: invocation.StepId,
            ToolId: invocation.ToolId,
            Status: ReceiptStatus.Succeeded,
            Message: "State query completed.",
            At: DateTimeOffset.UtcNow,
            Data: new Dictionary<string, object?>
            {
                ["query"] = invocation.Input.GetValueOrDefault("query"),
                ["stateReady"] = true
            });

        var observation = new Observation(
            ObservationId: AgenticaIds.New("observation"),
            StepId: invocation.StepId,
            Kind: ObservationKind.StateQuery,
            Summary: "State is ready for the requested action.",
            Data: new Dictionary<string, object?>
            {
                ["stateReady"] = true,
                ["confidence"] = 1.0
            },
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

        return Task.FromResult(new ToolResult(receipt, observation));
    }
}

public sealed class PerformActionTool : ITool
{
    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var receipt = new Receipt(
            ReceiptId: AgenticaIds.New("receipt"),
            StepId: invocation.StepId,
            ToolId: invocation.ToolId,
            Status: ReceiptStatus.Succeeded,
            Message: "Action completed.",
            At: DateTimeOffset.UtcNow,
            Data: new Dictionary<string, object?>
            {
                ["action"] = invocation.Input.GetValueOrDefault("action"),
                ["accepted"] = true
            });

        var artifact = new Artifact(
            ArtifactId: AgenticaIds.New("artifact"),
            Kind: "action_result",
            Payload: new Dictionary<string, object?>
            {
                ["action"] = invocation.Input.GetValueOrDefault("action"),
                ["result"] = "marker_written"
            },
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

        return Task.FromResult(new ToolResult(receipt, Artifact: artifact));
    }
}

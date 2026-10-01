using Agentica.Execution;
using Agentica.Clients.Llm;

internal static class CliParsing
{
    public static bool TryParseReasoningEffort(string value, out LlmReasoningEffort effort)
    {
        LlmReasoningEffort? parsed = value.ToLowerInvariant() switch
        {
            "none" => LlmReasoningEffort.None,
            "minimal" => LlmReasoningEffort.Minimal,
            "low" => LlmReasoningEffort.Low,
            "medium" => LlmReasoningEffort.Medium,
            "high" => LlmReasoningEffort.High,
            "xhigh" => LlmReasoningEffort.XHigh,
            "max" => LlmReasoningEffort.Max,
            _ => null
        };
        effort = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }

    public static bool TryParsePlanningMode(string value, out PlanningMode planningMode)
    {
        switch (value.ToLowerInvariant())
        {
            case "stepwise":
                planningMode = PlanningMode.Stepwise;
                return true;
            case "query-blocker":
            case "query-and-blocker":
            case "query-and-blocker-driven":
                planningMode = PlanningMode.QueryAndBlockerDriven;
                return true;
            case "blocker":
            case "blocker-driven":
                planningMode = PlanningMode.BlockerDriven;
                return true;
            case "plan-only":
            case "planonly":
                planningMode = PlanningMode.PlanOnly;
                return true;
            default:
                planningMode = PlanningMode.Stepwise;
                return false;
        }
    }
}

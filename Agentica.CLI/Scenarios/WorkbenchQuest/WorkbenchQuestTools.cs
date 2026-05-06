using Agentica.Tools;

namespace Agentica.CLI.Scenarios.WorkbenchQuest;

public static class WorkbenchQuestTools
{
    public static ToolCatalog CreateCatalog(WorkbenchQuestSession session)
    {
        var dispatcher = new WorkbenchQuestToolDispatcher(session);
        return ToolCatalog.Create(
            Register(WorkbenchQuestToolIds.ListFiles, "Workbench List Files", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(
                WorkbenchQuestToolIds.ReadFile,
                "Workbench Read File",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "path",
                    Required: true,
                    Description: "Scenario-relative file path copied from workbench.list_files.",
                    Example: "src/Calculator.txt"))),
            Register(
                WorkbenchQuestToolIds.Search,
                "Workbench Search",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "query",
                    Required: true,
                    Description: "Literal text to search for in scenario files.",
                    Example: "Add"))),
            Register(WorkbenchQuestToolIds.RunCheck, "Workbench Run Check", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(WorkbenchQuestToolIds.Diff, "Workbench Diff", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(
                WorkbenchQuestToolIds.ApplyPatch,
                "Workbench Apply Patch",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(
                    new ToolInputField(
                        "path",
                        Required: true,
                        Description: "Scenario-relative writable file path. Paths outside the scenario are refused.",
                        Example: "src/Calculator.txt"),
                    new ToolInputField(
                        "find",
                        Required: true,
                        Description: "Exact existing text to replace.",
                        Example: "return left - right"),
                    new ToolInputField(
                        "replace",
                        Required: true,
                        Description: "Exact replacement text.",
                        Example: "return left + right"),
                    new ToolInputField(
                        "rationale",
                        Required: false,
                        Description: "Short evidence-grounded reason for the patch.",
                        Example: "The Add test expects 2 + 3 to equal 5."))),
            Register(
                WorkbenchQuestToolIds.WriteNote,
                "Workbench Write Note",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "note",
                    Required: true,
                    Description: "Short evidence-grounded note. Notes are not proof of success.",
                    Example: "The failing check points at Calculator.Add."))),
            Register(WorkbenchQuestToolIds.Complete, "Workbench Complete", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher));
    }

    private static ToolRegistration Register(
        string toolId,
        string name,
        ToolKind kind,
        ToolEffect effect,
        ITool tool,
        ToolInputSchema? inputSchema = null) =>
        new(new ToolDescriptor(
            ToolId: toolId,
            Name: name,
            Kind: kind,
            Effect: effect,
            InputSchema: inputSchema,
            Description: DescriptionFor(toolId)), tool);

    private static string DescriptionFor(string toolId) =>
        toolId switch
        {
            WorkbenchQuestToolIds.ListFiles => "Returns scenario file paths, sizes, read-only flags, and the abstract objective. It does not reveal the fix.",
            WorkbenchQuestToolIds.ReadFile => "Returns raw file content for a scenario-relative path.",
            WorkbenchQuestToolIds.Search => "Returns raw line matches for a literal query across scenario files.",
            WorkbenchQuestToolIds.RunCheck => "Runs the deterministic scenario check and returns raw pass/fail output. Call before the first mutation to establish baseline failure evidence, and after mutation to verify.",
            WorkbenchQuestToolIds.Diff => "Returns a raw diff-style summary of scenario files changed from their initial content.",
            WorkbenchQuestToolIds.ApplyPatch => "Applies an exact find/replace patch to one writable scenario file. It refuses read-only files, missing text, paths outside the scenario, and mutation before failed baseline check plus relevant read evidence.",
            WorkbenchQuestToolIds.WriteNote => "Stores a short workbench note. Notes help audit planning but do not prove success.",
            WorkbenchQuestToolIds.Complete => "Checks terminal completion and emits workbench.objective_completed only when failed-check-before-mutation, relevant read, applied patch, and passing-check-after-mutation evidence exists.",
            _ => "WorkbenchQuest tool."
        };
}

public sealed class WorkbenchQuestToolDispatcher : ITool
{
    private readonly WorkbenchQuestSession _session;

    public WorkbenchQuestToolDispatcher(WorkbenchQuestSession session)
    {
        _session = session;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session.Execute(invocation));
    }
}

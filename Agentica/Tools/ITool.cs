namespace Agentica.Tools;

public interface ITool
{
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

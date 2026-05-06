using Agentica.CLI.Logging;
using Agentica.Events;
using Agentica.Outcomes;
using Agentica.Planning;

internal sealed record CliCommandServices(
    Func<CliRunOptions, IWorkflowPlanner> CreatePlanner,
    Func<bool, string?, string, IReadOnlyList<string>, RunLogWriter?> CreateRunLog,
    Func<IEventSink, RunLogWriter?, IEventSink> CreateEventSink,
    Action<OutcomeEnvelope> PrintEnvelope,
    Action<RunLogWriter?, OutcomeEnvelope> FinishRunLog,
    Func<bool> GeminiCredentialsAvailable,
    Action PrintUsage);

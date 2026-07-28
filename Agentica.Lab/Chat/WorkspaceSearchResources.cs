using System.Collections.ObjectModel;
using System.Diagnostics;

internal sealed record WorkspaceSearchResourceLimits(
    int MaxTraversalEntries,
    int MaxTraversalFiles,
    int MaxFallbackTotalBytes,
    int MaxFallbackFileBytes,
    int MaxSearchOutputChars,
    int MaxSearchLineChars,
    int MaxSearchErrorChars,
    TimeSpan MaxSearchDuration,
    TimeSpan ProcessTerminationGrace)
{
    public static WorkspaceSearchResourceLimits Default { get; } = new(
        MaxTraversalEntries: 20_000,
        MaxTraversalFiles: 10_000,
        MaxFallbackTotalBytes: 8 * 1024 * 1024,
        MaxFallbackFileBytes: 256 * 1024,
        MaxSearchOutputChars: 256 * 1024,
        MaxSearchLineChars: 8 * 1024,
        MaxSearchErrorChars: 16 * 1024,
        MaxSearchDuration: TimeSpan.FromSeconds(30),
        ProcessTerminationGrace: TimeSpan.FromSeconds(5));

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTraversalEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTraversalFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFallbackTotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFallbackFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSearchOutputChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSearchLineChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSearchErrorChars, 1);
        if (MaxSearchDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSearchDuration));
        }

        if (ProcessTerminationGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessTerminationGrace));
        }
    }
}

internal sealed class WorkspaceSearchProcessSpec
{
    public WorkspaceSearchProcessSpec(
        string FileName,
        IEnumerable<string>? PrefixArguments = null,
        bool AppendRipgrepArguments = true,
        Func<Process, Task>? TerminationOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FileName);
        this.FileName = FileName;
        this.PrefixArguments = new ReadOnlyCollection<string>((PrefixArguments ?? []).ToArray());
        this.AppendRipgrepArguments = AppendRipgrepArguments;
        this.TerminationOverride = TerminationOverride;
    }

    public static WorkspaceSearchProcessSpec Ripgrep { get; } = new("rg", []);

    public string FileName { get; }

    public IReadOnlyList<string> PrefixArguments { get; }

    public bool AppendRipgrepArguments { get; }

    internal Func<Process, Task>? TerminationOverride { get; }
}

internal sealed record WorkspaceSearchResult(
    IReadOnlyList<string> Matches,
    bool UsedFallback,
    bool Truncated,
    string? LimitReason,
    int ScannedFiles,
    long BytesRead,
    int OutputChars,
    string? Error);

internal sealed class WorkspaceSearchTerminationException : Exception
{
    public WorkspaceSearchTerminationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

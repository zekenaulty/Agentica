namespace Agentica.Lab.Logging;

/// <summary>
/// Storage limits for opt-in Lab run logs. Defaults retain useful proof artifacts while placing
/// hard bounds on an individual record, run directory, and the shared run-log root.
/// </summary>
public sealed record RunLogOptions
{
    public static RunLogOptions Default { get; } = new();

    /// <summary>Maximum UTF-8 bytes in one JSON record. Default: 256 KiB.</summary>
    public int MaxRecordBytes { get; init; } = 256 * 1024;

    /// <summary>Maximum bytes across all files in one run directory. Default: 16 MiB.</summary>
    public long MaxRunBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Maximum files in one run directory, including its ownership marker. Default: 64.</summary>
    public int MaxFilesPerRun { get; init; } = 64;

    /// <summary>Maximum owned run directories retained under one log root. Default: 100.</summary>
    public int MaxRunDirectories { get; init; } = 100;

    /// <summary>Maximum bytes retained anywhere under one log root. Default: 256 MiB.</summary>
    public long MaxStoredBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Age after which an owned run directory is eligible for deletion. Default: 30 days.</summary>
    public int RetentionDays { get; init; } = 30;
}

namespace Agentica.Lab.Configuration;

internal static class LocalEnvironmentFile
{
    public static LocalEnvironmentLoadResult LoadForCurrentProcess(string fileName = ".env")
    {
        var path = FindEnvironmentFile(fileName);
        if (path is null)
        {
            return LocalEnvironmentLoadResult.NotFound;
        }

        var loadedKeys = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (!TryParseLine(line, out var key, out var value))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
            loadedKeys.Add(key);
        }

        return new LocalEnvironmentLoadResult(path, loadedKeys);
    }

    private static string? FindEnvironmentFile(string fileName)
    {
        var probeRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in probeRoots)
        {
            var solutionRoot = FindAncestorContaining(root, "Agentica.slnx");
            if (solutionRoot is null)
            {
                continue;
            }

            var candidate = Path.Combine(solutionRoot, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var root in probeRoots)
        {
            var candidate = FindAncestorContaining(root, fileName);
            if (candidate is not null)
            {
                return Path.Combine(candidate, fileName);
            }
        }

        return null;
    }

    private static string? FindAncestorContaining(string start, string fileName)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, fileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool TryParseLine(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        if (trimmed.StartsWith("export ", StringComparison.Ordinal))
        {
            trimmed = trimmed["export ".Length..].TrimStart();
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        key = trimmed[..equalsIndex].Trim();
        value = ParseValue(trimmed[(equalsIndex + 1)..].Trim());

        return key.Length > 0;
    }

    private static string ParseValue(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1]
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1];
        }

        var commentIndex = raw.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            raw = raw[..commentIndex];
        }

        return raw.TrimEnd();
    }
}

internal sealed record LocalEnvironmentLoadResult(
    string? Path,
    IReadOnlyList<string> LoadedKeys)
{
    public static LocalEnvironmentLoadResult NotFound { get; } = new(null, []);

    public bool Loaded => Path is not null;
}

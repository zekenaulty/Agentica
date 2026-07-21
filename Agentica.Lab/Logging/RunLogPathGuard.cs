namespace Agentica.Lab.Logging;

internal static class RunLogPathGuard
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string ResolveRoot(string? baseDirectory)
    {
        var requestedRoot = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), ".agentica", "runs")
            : baseDirectory;
        var root = Path.GetFullPath(requestedRoot);

        EnsureExistingAncestorsArePlain(root);
        Directory.CreateDirectory(root);
        EnsureExistingAncestorsArePlain(root);
        EnsurePlainDirectory(root);
        return root;
    }

    public static string ResolveRunDirectory(string root, string directoryName)
    {
        EnsureSimpleName(directoryName);
        var directory = Path.GetFullPath(Path.Combine(root, directoryName));
        EnsureContained(root, directory);
        return directory;
    }

    public static string ResolveFile(string root, string runDirectory, string fileName)
    {
        EnsureSimpleName(fileName);
        EnsureContained(root, runDirectory);
        EnsurePlainDirectory(root);
        EnsurePlainDirectory(runDirectory);

        var path = Path.GetFullPath(Path.Combine(runDirectory, fileName));
        EnsureContained(runDirectory, path);
        var target = new FileInfo(path);
        if (target.LinkTarget is not null ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Run-log targets cannot be reparse points.");
        }

        if (Directory.Exists(path))
        {
            throw new IOException("Run-log targets must be files.");
        }

        return path;
    }

    public static void EnsurePlainDirectory(string directory)
    {
        if (!Directory.Exists(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Run-log directories must be existing plain directories.");
        }
    }

    public static void EnsurePlainDirectoryTree(string root, string directory)
    {
        EnsureContained(root, directory);
        EnsureExistingAncestorsArePlain(directory);
        EnsurePlainDirectory(directory);

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureContained(directory, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Run-log storage cannot contain reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    public static void EnsureContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (string.Equals(fullRoot, Path.TrimEndingDirectorySeparator(fullCandidate), PathComparison))
        {
            return;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, PathComparison))
        {
            throw new IOException("Run-log path containment failed.");
        }
    }

    private static void EnsureExistingAncestorsArePlain(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException("Run-log path has no filesystem root.");
        }

        var current = root;

        if (Directory.Exists(current) &&
            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Run-log paths cannot cross reparse points.");
        }

        var relative = Path.GetRelativePath(root, fullPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                if (File.Exists(current))
                {
                    throw new IOException("Run-log directory path crosses a file.");
                }

                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Run-log paths cannot cross reparse points.");
            }
        }
    }

    private static void EnsureSimpleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            Path.IsPathRooted(value) ||
            !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new IOException("Run-log names must be simple file names.");
        }
    }
}

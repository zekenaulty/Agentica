/// <summary>
/// Performs bounded, pre-open pathname validation for a cooperative workspace. It rejects
/// links and reparse points that are present when a path is checked. This is deliberately not
/// OS-handle-relative confinement and does not close adversarial TOCTOU path-replacement races.
/// </summary>
internal sealed class WorkspacePathBoundary
{
    private const string BoundaryPrefix = "Workspace boundary refused";
    private readonly Action<string>? _afterDirectoryCreated;

    internal const string SecurityModel =
        "Bounded pre-open validation rejects static links and reparse points; it is not " +
        "OS-handle-relative confinement and does not close adversarial TOCTOU replacement races.";

    public WorkspacePathBoundary(string workspaceRoot)
        : this(workspaceRoot, afterDirectoryCreated: null)
    {
    }

    internal WorkspacePathBoundary(
        string workspaceRoot,
        Action<string>? afterDirectoryCreated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        _afterDirectoryCreated = afterDirectoryCreated;
    }

    public string WorkspaceRoot { get; }

    public bool TryResolveContainedPath(
        string? path,
        out string resolvedPath,
        out string error)
    {
        try
        {
            var combined = string.IsNullOrWhiteSpace(path)
                ? WorkspaceRoot
                : Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(WorkspaceRoot, path);
            resolvedPath = Path.GetFullPath(combined);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            resolvedPath = string.Empty;
            error = $"{BoundaryPrefix}: invalid workspace path.";
            return false;
        }

        if (!IsContained(resolvedPath))
        {
            resolvedPath = string.Empty;
            error = $"{BoundaryPrefix}: path is outside the workspace root.";
            return false;
        }

        if (!TryValidateRoot(out error) ||
            !TryValidateExistingPrefix(resolvedPath, out error))
        {
            resolvedPath = string.Empty;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryResolveExistingPath(
        string? path,
        out string resolvedPath,
        out string error)
    {
        if (!TryResolveContainedPath(path, out resolvedPath, out error))
        {
            return false;
        }

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            resolvedPath = string.Empty;
            error = $"{BoundaryPrefix}: path does not exist under the workspace root.";
            return false;
        }

        return TryValidateExistingEntry(resolvedPath, out error);
    }

    public bool TryResolveExistingFile(
        string? path,
        out string resolvedPath,
        out string error)
    {
        if (!TryResolveExistingPath(path, out resolvedPath, out error))
        {
            return false;
        }

        if (!File.Exists(resolvedPath))
        {
            resolvedPath = string.Empty;
            error = $"{BoundaryPrefix}: file does not exist under the workspace root.";
            return false;
        }

        return true;
    }

    public bool TryPrepareDirectory(
        string relativePath,
        out string directoryPath,
        out string error) =>
        TryPrepareDirectory(
            relativePath,
            out directoryPath,
            out _,
            out error);

    public bool TryPrepareDirectory(
        string relativePath,
        out string directoryPath,
        out IReadOnlyList<string> createdDirectories,
        out string error)
    {
        var created = new List<string>();
        createdDirectories = created;
        if (!TryResolveContainedPath(relativePath, out directoryPath, out error))
        {
            return false;
        }

        var relative = Path.GetRelativePath(WorkspaceRoot, directoryPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return TryValidateExistingDirectory(directoryPath, out error);
        }

        var current = WorkspaceRoot;
        foreach (var segment in SplitRelativePath(relative))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (!TryValidateExistingDirectory(current, out error))
                {
                    directoryPath = string.Empty;
                    return false;
                }

                continue;
            }

            if (TryGetLinkTarget(current, out _))
            {
                directoryPath = string.Empty;
                error = $"{BoundaryPrefix}: reparse points and symbolic links are not allowed.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(current);
                created.Add(current);
                _afterDirectoryCreated?.Invoke(current);
            }
            catch (Exception exception) when (IsPathException(exception))
            {
                directoryPath = string.Empty;
                error = $"{BoundaryPrefix}: workspace directory could not be created.";
                return false;
            }

            if (!TryValidateExistingDirectory(current, out error))
            {
                directoryPath = string.Empty;
                return false;
            }
        }

        return TryValidateExistingDirectory(directoryPath, out error);
    }

    public bool TryResolveNewFile(
        string relativePath,
        out string filePath,
        out string error)
    {
        if (!TryResolveContainedPath(relativePath, out filePath, out error))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(parent) ||
            !TryPrepareDirectory(parent, out _, out error))
        {
            filePath = string.Empty;
            return false;
        }

        if (File.Exists(filePath) || Directory.Exists(filePath) || TryGetLinkTarget(filePath, out _))
        {
            filePath = string.Empty;
            error = $"{BoundaryPrefix}: output path already exists.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryEnumerateFiles(
        string searchRoot,
        int maxEntries,
        int maxFiles,
        IReadOnlySet<string> excludedDirectoryNames,
        CancellationToken cancellationToken,
        out IReadOnlyList<string> files,
        out int visitedEntries,
        out bool limitReached,
        out string error)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentNullException.ThrowIfNull(excludedDirectoryNames);

        files = Array.Empty<string>();
        visitedEntries = 0;
        limitReached = false;
        if (!TryResolveExistingPath(searchRoot, out var resolvedRoot, out error))
        {
            return false;
        }

        if (File.Exists(resolvedRoot))
        {
            files = [resolvedRoot];
            visitedEntries = 1;
            return true;
        }

        var discovered = new List<string>();
        var pending = new Stack<string>();
        pending.Push(resolvedRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (!TryValidateExistingDirectory(directory, out error))
            {
                return false;
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visitedEntries++;
                    if (visitedEntries > maxEntries)
                    {
                        files = discovered;
                        limitReached = true;
                        error = string.Empty;
                        return true;
                    }

                    if (!TryResolveExistingPath(entry, out var resolvedEntry, out error))
                    {
                        return false;
                    }

                    if (Directory.Exists(resolvedEntry))
                    {
                        if (!excludedDirectoryNames.Contains(Path.GetFileName(resolvedEntry)))
                        {
                            pending.Push(resolvedEntry);
                        }
                    }
                    else if (File.Exists(resolvedEntry))
                    {
                        discovered.Add(resolvedEntry);
                        if (discovered.Count >= maxFiles)
                        {
                            files = discovered;
                            limitReached = true;
                            error = string.Empty;
                            return true;
                        }
                    }
                }
            }
            catch (Exception exception) when (IsPathException(exception))
            {
                error = $"{BoundaryPrefix}: workspace traversal failed closed.";
                return false;
            }

        }

        files = discovered;
        error = string.Empty;
        return true;
    }

    private bool TryValidateRoot(out string error)
    {
        if (!Directory.Exists(WorkspaceRoot))
        {
            error = $"{BoundaryPrefix}: workspace root does not exist.";
            return false;
        }

        return TryValidateExistingDirectory(WorkspaceRoot, out error);
    }

    private bool TryValidateExistingPrefix(string path, out string error)
    {
        var relative = Path.GetRelativePath(WorkspaceRoot, path);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            error = string.Empty;
            return true;
        }

        var current = WorkspaceRoot;
        foreach (var segment in SplitRelativePath(relative))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (TryGetLinkTarget(current, out _))
                {
                    error = $"{BoundaryPrefix}: reparse points and symbolic links are not allowed.";
                    return false;
                }

                error = string.Empty;
                return true;
            }

            if (!TryValidateExistingEntry(current, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private bool TryValidateExistingDirectory(string path, out string error)
    {
        if (!Directory.Exists(path))
        {
            error = $"{BoundaryPrefix}: expected workspace directory is unavailable.";
            return false;
        }

        return TryValidateExistingEntry(path, out error);
    }

    private static bool TryValidateExistingEntry(string path, out string error)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || TryGetLinkTarget(path, out _))
            {
                error = $"{BoundaryPrefix}: reparse points and symbolic links are not allowed.";
                return false;
            }
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            error = $"{BoundaryPrefix}: workspace path could not be verified.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool IsContained(string path)
    {
        var relative = Path.GetRelativePath(WorkspaceRoot, path);
        if (Path.IsPathRooted(relative) || string.Equals(relative, "..", StringComparison.Ordinal))
        {
            return false;
        }

        return !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitRelativePath(string relativePath) =>
        relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static bool TryGetLinkTarget(string path, out string? linkTarget)
    {
        try
        {
            linkTarget = new FileInfo(path).LinkTarget;
            if (!string.IsNullOrWhiteSpace(linkTarget))
            {
                return true;
            }
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            // Try the directory view below. Some platforms distinguish the entry kind here.
        }

        try
        {
            linkTarget = new DirectoryInfo(path).LinkTarget;
            return !string.IsNullOrWhiteSpace(linkTarget);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            linkTarget = null;
            return false;
        }
    }

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException or
            IOException or
            NotSupportedException or
            System.Security.SecurityException or
            UnauthorizedAccessException;
}

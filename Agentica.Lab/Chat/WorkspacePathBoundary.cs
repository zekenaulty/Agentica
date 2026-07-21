internal sealed class WorkspacePathBoundary
{
    private const string BoundaryPrefix = "Workspace boundary refused";

    public WorkspacePathBoundary(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
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
        out string error)
    {
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
        out IReadOnlyList<string> files,
        out string error)
    {
        files = Array.Empty<string>();
        if (!TryResolveExistingPath(searchRoot, out var resolvedRoot, out error))
        {
            return false;
        }

        if (File.Exists(resolvedRoot))
        {
            files = [resolvedRoot];
            return true;
        }

        var discovered = new List<string>();
        var pending = new Stack<string>();
        pending.Push(resolvedRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!TryValidateExistingDirectory(directory, out error))
            {
                return false;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (IsPathException(exception))
            {
                error = $"{BoundaryPrefix}: workspace traversal failed closed.";
                return false;
            }

            foreach (var entry in entries)
            {
                if (!TryResolveExistingPath(entry, out var resolvedEntry, out error))
                {
                    return false;
                }

                if (Directory.Exists(resolvedEntry))
                {
                    pending.Push(resolvedEntry);
                }
                else if (File.Exists(resolvedEntry))
                {
                    discovered.Add(resolvedEntry);
                }
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

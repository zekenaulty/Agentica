using System.Globalization;
using System.Text;

namespace Agentica.Lab.Web.Chat;

// This is a conversation-owned scratch namespace, not a grant to the checkout
// or arbitrary host files. Every existing component, including the configured
// root's ancestors, is checked for links/reparse points before each operation.
internal sealed class ExperimentScratchFiles(string workspaceDirectory)
{
    private const int MaximumFileBytes = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _root = ValidateRoot(workspaceDirectory);

    public object List(string relativePath, int offset, int limit)
    {
        var path = Resolve(relativePath, allowRoot: true);
        if (!Directory.Exists(path))
        {
            if (path == _root) return new { path = relativePath, entries = Array.Empty<object>(), nextOffset = (int?)null };
            throw new ExperimentToolException("scratch.not_found", "The scratch directory does not exist.");
        }
        // Do not recurse into directories or follow links. Bound enumeration
        // independently of the displayed result budget.
        var entries = Directory.EnumerateFileSystemEntries(path).Take(1001)
            .Select(entry => new
            {
                name = Path.GetFileName(entry),
                kind = IsLink(entry) ? "blocked_link" : Directory.Exists(entry) ? "directory" : "file"
            }).OrderBy(entry => entry.name, StringComparer.Ordinal).ToArray();
        CheckExistingComponents(path);
        return new
        {
            path = relativePath,
            enumerationTruncated = entries.Length > 1000,
            entries = entries.Take(1000).Skip(offset).Take(limit).ToArray(),
            nextOffset = offset + limit < Math.Min(entries.Length, 1000) ? offset + limit : (int?)null
        };
    }

    public async Task<object> ReadAsync(string relativePath, int offset, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) throw new ExperimentToolException("scratch.not_found", "The scratch file does not exist.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        CheckExistingComponents(path);
        if (file.Length > MaximumFileBytes)
            throw new ExperimentToolException("scratch.file_too_large", "Scratch text reads are limited to 262144 bytes.");
        using var reader = new StreamReader(file, StrictUtf8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        var skip = new char[4096];
        var skipped = 0;
        while (skipped < offset)
        {
            var count = await reader.ReadAsync(skip.AsMemory(0, Math.Min(skip.Length, offset - skipped)), cancellationToken);
            if (count == 0) break;
            skipped += count;
        }
        var text = new char[maximumCharacters + 1];
        var length = 0;
        while (length < text.Length)
        {
            var count = await reader.ReadAsync(text.AsMemory(length), cancellationToken);
            if (count == 0) break;
            length += count;
        }
        CheckExistingComponents(path);
        if (length > 0 && skipped > 0 && char.IsLowSurrogate(text[0]))
            throw new ExperimentToolException("utility.invalid_offset", "A text offset cannot split a UTF-16 surrogate pair.");
        var take = Math.Min(maximumCharacters, length);
        if (take > 0 && take < length && char.IsHighSurrogate(text[take - 1])) take--;
        if (take == 0 && length > 0)
            throw new ExperimentToolException("utility.page_too_small", "The requested page must fit a complete Unicode character.");
        return new
        {
            path = relativePath,
            encoding = "utf-8",
            characterUnit = "utf16-code-units",
            offset = skipped,
            content = new string(text, 0, take),
            truncated = length > take,
            nextOffset = length > take ? skipped + take : (int?)null
        };
    }

    public async Task<object> WriteAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        var bytes = StrictUtf8.GetBytes(content);
        if (bytes.Length > MaximumFileBytes)
            throw new ExperimentToolException("scratch.file_too_large", "Scratch writes are limited to 262144 UTF-8 bytes.");
        var path = Resolve(relativePath);
        var directory = Path.GetDirectoryName(path)!;
        CheckExistingComponents(directory);
        Directory.CreateDirectory(directory);
        CheckExistingComponents(directory);
        var temporary = Path.Combine(directory, ".agentica-pending-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                CheckExistingComponents(temporary);
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            CheckExistingComponents(directory);
            CheckExistingComponents(path);
            // Create-new publication prevents overwriting an existing file or
            // hard link. An interrupted write can be reconciled by list/read.
            File.Move(temporary, path, overwrite: false);
            CheckExistingComponents(path);
            return new { path = relativePath, written = true, utf8Bytes = bytes.Length, characters = content.Length, overwrite = false };
        }
        finally
        {
            // Only our exact randomly named temporary file is eligible for cleanup.
            if (File.Exists(temporary) && !IsLink(temporary)) File.Delete(temporary);
        }
    }

    private string Resolve(string relativePath, bool allowRoot = false)
    {
        if (allowRoot && relativePath is "" or ".")
        {
            CheckExistingComponents(_root);
            return _root;
        }
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512 || Path.IsPathRooted(relativePath) ||
            relativePath.Any(character => character < ' ' || ":*?\"<>|".Contains(character)))
            throw InvalidPath();
        var parts = relativePath.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || IsReserved(part)) throw InvalidPath();
        }
        var path = Path.GetFullPath(Path.Combine(_root, Path.Combine(parts)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw InvalidPath();
        CheckExistingComponents(path);
        return path;
    }

    private static string ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("The scratch root must be absolute.", nameof(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full == Path.GetPathRoot(full)) throw new ArgumentException("A filesystem root cannot be a scratch root.", nameof(root));
        CheckExistingComponents(full);
        return full;
    }

    private static void CheckExistingComponents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (IsLink(current)) throw new ExperimentToolException("scratch.link_refused", "Scratch paths cannot contain symbolic links or reparse points.");
        }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsReserved(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("con", StringComparison.OrdinalIgnoreCase) || stem.Equals("prn", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("aux", StringComparison.OrdinalIgnoreCase) || stem.Equals("nul", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("com", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("lpt", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9';
    }

    private static ExperimentToolException InvalidPath() => new("scratch.invalid_path", "Use a relative path inside this conversation's scratch directory.");
}

using System.Text;

internal static class WorkspaceTextResourceReader
{
    private const int BufferSize = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<WorkspaceTextPrefix> ReadPrefixAsync(
        string path,
        int maxChars,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var content = new StringBuilder(Math.Min(maxChars, BufferSize));
        var charLimitReached = false;
        var scan = await ScanUtf8Async(
                path,
                maxBytes,
                (buffer, offset, count) =>
                {
                    for (var index = offset; index < offset + count; index++)
                    {
                        if (content.Length < maxChars)
                        {
                            content.Append(buffer[index]);
                        }
                        else
                        {
                            charLimitReached = true;
                        }
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkspaceTextPrefix(
            content.ToString(),
            charLimitReached || scan.ByteLimitReached,
            scan.BytesRead,
            charLimitReached,
            scan.ByteLimitReached);
    }

    public static async Task<WorkspaceFileSearchResult> SearchFileAsync(
        string path,
        string pattern,
        int maxResults,
        int maxOutputChars,
        int maxLineChars,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var matches = new List<string>();
        var line = new StringBuilder(Math.Min(maxLineChars, BufferSize));
        var lineNumber = 1;
        var outputChars = 0;
        var lineLimitReached = false;
        var resultLimitReached = false;
        var outputLimitReached = false;
        var collectionStopped = false;

        void CompleteLine()
        {
            if (collectionStopped)
            {
                return;
            }

            if (line.Length > 0 && line[^1] == '\r')
            {
                line.Length--;
            }

            if (line.ToString().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                var match = $"{path}:{lineNumber}:1:{line}";
                if (match.Length > maxOutputChars - outputChars)
                {
                    outputLimitReached = true;
                    collectionStopped = true;
                    return;
                }

                matches.Add(match);
                outputChars += match.Length;
                if (matches.Count >= maxResults)
                {
                    resultLimitReached = true;
                    collectionStopped = true;
                }
            }

            line.Clear();
            lineNumber++;
        }

        var scan = await ScanUtf8Async(
                path,
                maxBytes,
                (buffer, offset, count) =>
                {
                    if (collectionStopped)
                    {
                        return;
                    }

                    for (var index = offset; index < offset + count && !collectionStopped; index++)
                    {
                        var character = buffer[index];
                        if (character == '\n')
                        {
                            CompleteLine();
                            continue;
                        }

                        if (line.Length < maxLineChars)
                        {
                            line.Append(character);
                        }
                        else
                        {
                            lineLimitReached = true;
                        }
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!collectionStopped && line.Length > 0)
        {
            CompleteLine();
        }

        return new WorkspaceFileSearchResult(
            matches,
            scan.BytesRead,
            outputChars,
            Truncated: scan.ByteLimitReached || lineLimitReached || resultLimitReached || outputLimitReached,
            LimitReason: outputLimitReached
                ? "output_chars"
                : resultLimitReached
                    ? "result_count"
                    : scan.ByteLimitReached
                        ? "file_bytes"
                        : lineLimitReached
                            ? "line_chars"
                            : null);
    }

    private static async Task<BoundedUtf8Scan> ScanUtf8Async(
        string path,
        int maxBytes,
        Action<char[], int, int> consumeChars,
        CancellationToken cancellationToken)
    {
        await using var file = OpenRead(path);
        var decoder = StrictUtf8.GetDecoder();
        var byteBuffer = new byte[BufferSize];
        var charBuffer = new char[StrictUtf8.GetMaxCharCount(BufferSize)];
        var readCapacity = checked((long)maxBytes + 1);
        long totalBytesRead = 0;
        var reachedEnd = false;
        var firstDecodedCharacter = true;

        while (totalBytesRead < readCapacity)
        {
            var requested = (int)Math.Min(byteBuffer.Length, readCapacity - totalBytesRead);
            var read = await file.ReadAsync(
                    byteBuffer.AsMemory(0, requested),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                reachedEnd = true;
                break;
            }

            var accepted = (int)Math.Min(read, maxBytes - Math.Min(totalBytesRead, maxBytes));
            if (accepted > 0)
            {
                if (Array.IndexOf(byteBuffer, (byte)0, 0, accepted) >= 0)
                {
                    throw WorkspaceTextResourceException.BinaryContent();
                }

                DecodeAndConsume(
                    decoder,
                    byteBuffer,
                    accepted,
                    charBuffer,
                    flush: false,
                    consumeChars,
                    ref firstDecodedCharacter);
            }

            totalBytesRead += read;
        }

        if (reachedEnd)
        {
            DecodeAndConsume(
                decoder,
                [],
                0,
                charBuffer,
                flush: true,
                consumeChars,
                ref firstDecodedCharacter);
        }

        return new BoundedUtf8Scan(
            Math.Min(totalBytesRead, maxBytes),
            ByteLimitReached: totalBytesRead > maxBytes);
    }

    private static void DecodeAndConsume(
        Decoder decoder,
        byte[] byteBuffer,
        int byteCount,
        char[] charBuffer,
        bool flush,
        Action<char[], int, int> consumeChars,
        ref bool firstDecodedCharacter)
    {
        int charCount;
        try
        {
            charCount = decoder.GetChars(
                byteBuffer,
                0,
                byteCount,
                charBuffer,
                0,
                flush);
        }
        catch (DecoderFallbackException exception)
        {
            throw WorkspaceTextResourceException.InvalidUtf8(exception);
        }

        var offset = 0;
        if (firstDecodedCharacter && charCount > 0)
        {
            firstDecodedCharacter = false;
            if (charBuffer[0] == '\uFEFF')
            {
                offset = 1;
            }
        }

        if (charCount > offset)
        {
            consumeChars(charBuffer, offset, charCount - offset);
        }
    }

    private static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private sealed record BoundedUtf8Scan(long BytesRead, bool ByteLimitReached);
}

internal sealed record WorkspaceTextPrefix(
    string Content,
    bool Truncated,
    long BytesRead,
    bool CharLimitReached,
    bool ByteLimitReached);

internal sealed record WorkspaceFileSearchResult(
    IReadOnlyList<string> Matches,
    long BytesRead,
    int OutputChars,
    bool Truncated,
    string? LimitReason);

internal sealed class WorkspaceTextResourceException : Exception
{
    private WorkspaceTextResourceException(
        string code,
        string reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Reason = reason;
    }

    public string Code { get; }

    public string Reason { get; }

    public static WorkspaceTextResourceException BinaryContent() =>
        new(
            "workspace.resource.binary",
            "nul_byte",
            "Workspace resource refused: binary file content is not supported.");

    public static WorkspaceTextResourceException InvalidUtf8(DecoderFallbackException innerException) =>
        new(
            "workspace.resource.invalid_utf8",
            "invalid_utf8",
            "Workspace resource refused: file content is not valid UTF-8.",
            innerException);
}

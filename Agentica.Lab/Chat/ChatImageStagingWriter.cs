internal interface IChatImageStagingWriter
{
    Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

internal sealed class ChatImageStagingWriter : IChatImageStagingWriter
{
    public static ChatImageStagingWriter Instance { get; } = new();

    private ChatImageStagingWriter()
    {
    }

    public async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

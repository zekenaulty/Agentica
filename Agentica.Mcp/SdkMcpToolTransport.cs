using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agentica.Mcp;

/// <summary>SDK-backed MCP transport. The host supplies the endpoint and any credentials.</summary>
public sealed class SdkMcpToolTransport : IMcpToolTransport, IAsyncDisposable
{
    private readonly McpClient _client;

    private SdkMcpToolTransport(string serverId, McpClient client)
    {
        ServerId = serverId;
        _client = client;
    }

    public string ServerId { get; }

    public static async Task<SdkMcpToolTransport> ConnectHttpAsync(
        string serverId,
        Uri endpoint,
        CancellationToken cancellationToken = default,
        string? bearerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp))
        {
            throw new ArgumentException("MCP HTTP endpoints must use HTTPS or loopback HTTP.", nameof(endpoint));
        }
        if (bearerToken is not null &&
            (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Contains('\r') ||
             bearerToken.Contains('\n')))
        {
            throw new ArgumentException("MCP bearer token contains invalid characters.",
                nameof(bearerToken));
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = bearerToken is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer " + bearerToken
                }
        });
        try
        {
            return new SdkMcpToolTransport(serverId, await McpClient.CreateAsync(
                transport, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(
        CancellationToken cancellationToken)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return tools.Select(tool => new McpDiscoveredTool(
            tool.ProtocolTool.Name,
            tool.ProtocolTool.InputSchema.GetRawText())).ToArray();
    }

    public async Task<McpCallResult> CallToolAsync(
        string remoteName,
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var result = await _client.CallToolAsync(
            remoteName,
            input.ToDictionary(pair => pair.Key, pair => (object?)pair.Value,
                StringComparer.Ordinal),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Content.Any(block => block is not TextContentBlock))
        {
            throw new NotSupportedException(
                "This MCP adapter accepts text content blocks only.");
        }
        return new McpCallResult(
            result.IsError == true,
            result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToArray(),
            result.StructuredContent?.GetRawText());
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

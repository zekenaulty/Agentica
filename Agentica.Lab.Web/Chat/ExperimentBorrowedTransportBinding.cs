using System.Security.Cryptography;
using System.Text.Json;

namespace Agentica.Lab.Web.Chat;

internal sealed class ExperimentBorrowedTransportBinding(HttpClient borrowed)
{
    public string Identity { get; } = Describe(borrowed);
    public void Validate()
    {
        if (Identity != Describe(borrowed))
            throw new ExperimentException("provider.transport_changed", "Borrowed provider transport defaults changed after capture. No further dispatch is admitted.");
    }
    private static string Describe(HttpClient client) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        timeout = client.Timeout.Ticks,
        version = client.DefaultRequestVersion.ToString(),
        policy = client.DefaultVersionPolicy.ToString(),
        headers = client.DefaultRequestHeaders.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new { name = item.Key.ToLowerInvariant(), values = item.Value.ToArray() })
    })));
}

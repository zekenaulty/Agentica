using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Context;

/// <summary>Public host observations and derived knowledge only. Provider continuation never enters this store.</summary>
public sealed class HostContextStore
{
    private readonly string _rootDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, WeakReference<HostContextSession>> _sessions = new(StringComparer.Ordinal);

    public HostContextStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public HostContextSession Open(HostRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = new HostContextIdentity(
            Required(request.HostId), Required(request.SessionId), Required(request.SessionEpoch),
            Required(request.ScopeId), Required(request.PerspectiveId));
        var key = ContextJson.Hash(identity);
        lock (_gate)
        {
            // Finished runs do not make this cache retain every historical context payload indefinitely.
            foreach (var dead in _sessions.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
                _sessions.Remove(dead);
            if (!_sessions.TryGetValue(key, out var reference) || !reference.TryGetTarget(out var session))
            {
                session = new HostContextSession(identity, Path.Combine(_rootDirectory, key + ".json"));
                _sessions[key] = new(session);
            }
            // Every new run supplies an observation, even when a compatible context was restored.
            session.Record(request.Observation);
            return session;
        }
    }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256) throw new ArgumentException("Context identity fields are limited to 256 characters.", nameof(value));
        return value;
    }
}

internal static class ContextJson
{
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(JsonSerializer.SerializeToElement(value, HostProtocol.Json))));

    public static byte[] CanonicalBytes(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    public static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, HostProtocol.Json).Length;

    public static void AtomicWrite(string path, ContextPayload payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new ContextFile(1, payload, Hash(payload)), HostProtocol.Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

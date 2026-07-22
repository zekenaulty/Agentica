namespace Agentica;

/// <summary>
/// Creates readable, collision-resistant identifiers for Agentica records.
/// </summary>
public static class AgenticaIds
{
    /// <summary>
    /// Creates an identifier in the form <c>{prefix}_{uuid}</c>.
    /// </summary>
    /// <remarks>
    /// The UUID is the complete lowercase, hyphen-free version 4 representation:
    /// 32 hexadecimal characters carrying approximately 122 random bits after the
    /// UUID version and variant bits. These identifiers are durable references, not secrets.
    /// </remarks>
    public static string New(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}";
}

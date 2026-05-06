namespace Agentica;

public static class AgenticaIds
{
    public static string New(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 8, prefix.Length + 1 + 32)];
}

namespace Agentica.Tools;

/// <summary>
/// Declares retry semantics; it does not itself authorize a retry.
/// </summary>
public enum ToolRetrySafety
{
    /// <summary>No retry safety claim has been established.</summary>
    Unknown,

    /// <summary>Repeating the operation with the same input has the same intended effect.</summary>
    Idempotent,

    /// <summary>Repeating the operation may add another effect and is not mutation-retry safe.</summary>
    Additive,

    /// <summary>The mutation is explicitly unsafe to repeat.</summary>
    MutationUnsafe,

    /// <summary>The destructive operation is explicitly unsafe to repeat.</summary>
    Destructive
}

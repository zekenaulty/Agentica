namespace Agentica.Execution;

/// <summary>
/// Classifies exceptions that a runtime boundary may safely project into a bounded
/// failure outcome. Process-integrity failures must escape unchanged.
/// </summary>
internal static class RuntimeExceptionBoundary
{
    private const int MaxExceptionGraphNodes = 256;

    public static bool IsRecoverable(Exception exception)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (visited.Count > MaxExceptionGraphNodes)
            {
                return false;
            }

            if (current is OutOfMemoryException or StackOverflowException or AccessViolationException)
            {
                return false;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var innerException in aggregate.InnerExceptions)
                {
                    if (pending.Count >= MaxExceptionGraphNodes)
                    {
                        return false;
                    }

                    pending.Push(innerException);
                }
            }
            else if (current.InnerException is { } innerException)
            {
                pending.Push(innerException);
            }
        }

        return true;
    }
}

namespace Agentica.Tests;

internal static class ConsoleOutputTestCollection
{
    public const string Name = "Console output";
}

// Console.Out is process-wide, so tests that replace it must not overlap any
// other test collection that may read from or write to the console.
[CollectionDefinition(ConsoleOutputTestCollection.Name, DisableParallelization = true)]
public sealed class ConsoleOutputTestCollectionDefinition;

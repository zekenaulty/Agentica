namespace Agentica.Tests;

internal static class ProcessEnvironmentTestCollection
{
    public const string Name = "Process environment";
}

[CollectionDefinition(ProcessEnvironmentTestCollection.Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentTestCollectionDefinition;

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Agentica.Orchestration;

internal static class StructuralValueEquality
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 4_096;
    private const int MaxCollectionItems = 4_096;

    public static bool AreEqual(object? left, object? right)
    {
        try
        {
            return new ComparisonContext().AreEqual(left, right, depth: 0);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            // Host state is untrusted proof input. Unsupported, unstable, or hostile values fail closed.
            return false;
        }
    }

    private sealed class ComparisonContext
    {
        private readonly HashSet<ReferencePair> _activePairs = new(ReferencePairComparer.Instance);
        private int _nodes;

        public bool AreEqual(object? left, object? right, int depth)
        {
            if (depth > MaxDepth || ++_nodes > MaxNodes)
            {
                return false;
            }

            if (!NormalizeJsonScalar(ref left) || !NormalizeJsonScalar(ref right))
            {
                return false;
            }

            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            var leftDictionary = ReadStringDictionary(left);
            var rightDictionary = ReadStringDictionary(right);
            if (leftDictionary.Kind != DictionaryKind.NotDictionary ||
                rightDictionary.Kind != DictionaryKind.NotDictionary)
            {
                if (leftDictionary.Kind != DictionaryKind.Valid ||
                    rightDictionary.Kind != DictionaryKind.Valid)
                {
                    return false;
                }

                return CompareComposite(
                    left,
                    right,
                    () => DictionariesEqual(leftDictionary.Values!, rightDictionary.Values!, depth + 1));
            }

            var leftSequence = ReadSequence(left);
            var rightSequence = ReadSequence(right);
            if (leftSequence.Kind != SequenceKind.NotSequence ||
                rightSequence.Kind != SequenceKind.NotSequence)
            {
                if (leftSequence.Kind != SequenceKind.Valid ||
                    rightSequence.Kind != SequenceKind.Valid)
                {
                    return false;
                }

                return CompareComposite(
                    left,
                    right,
                    () => SequencesEqual(leftSequence.Values!, rightSequence.Values!, depth + 1));
            }

            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return left.GetType() == right.GetType() && Equals(left, right);
        }

        private bool DictionariesEqual(
            IReadOnlyDictionary<string, object?> left,
            IReadOnlyDictionary<string, object?> right,
            int depth)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var value) ||
                    !AreEqual(pair.Value, value, depth))
                {
                    return false;
                }
            }

            return true;
        }

        private bool SequencesEqual(
            IReadOnlyList<object?> left,
            IReadOnlyList<object?> right,
            int depth)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!AreEqual(left[index], right[index], depth))
                {
                    return false;
                }
            }

            return true;
        }

        private bool CompareComposite(object left, object right, Func<bool> compare)
        {
            if (left.GetType().IsValueType || right.GetType().IsValueType)
            {
                return compare();
            }

            var pair = new ReferencePair(left, right);
            if (!_activePairs.Add(pair))
            {
                return false;
            }

            try
            {
                return compare();
            }
            finally
            {
                _activePairs.Remove(pair);
            }
        }
    }

    private static bool NormalizeJsonScalar(ref object? value)
    {
        if (value is JsonDocument document)
        {
            value = document.RootElement;
        }

        if (value is not JsonElement element)
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                value = element;
                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            case JsonValueKind.Number when element.TryGetInt64(out var integer):
                value = integer;
                return true;
            case JsonValueKind.Number:
                value = element.GetDouble();
                return true;
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Null:
                value = null;
                return true;
            default:
                return false;
        }
    }

    private static DictionaryReadResult ReadStringDictionary(object value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            var jsonValues = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (jsonValues.Count >= MaxCollectionItems ||
                    !jsonValues.TryAdd(property.Name, property.Value))
                {
                    return DictionaryReadResult.Invalid;
                }
            }

            return DictionaryReadResult.Valid(jsonValues);
        }

        if (value is IDictionary dictionary)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key ||
                    values.Count >= MaxCollectionItems ||
                    !values.TryAdd(key, entry.Value))
                {
                    return DictionaryReadResult.Invalid;
                }
            }

            return DictionaryReadResult.Valid(values);
        }

        var dictionaryInterface = value.GetType().GetInterfaces().FirstOrDefault(type =>
            type.IsGenericType &&
            type.GenericTypeArguments[0] == typeof(string) &&
            type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)));
        if (dictionaryInterface is null)
        {
            return DictionaryReadResult.NotDictionary;
        }

        if (value is not IEnumerable entries)
        {
            return DictionaryReadResult.Invalid;
        }

        var reflectedValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || reflectedValues.Count >= MaxCollectionItems)
            {
                return DictionaryReadResult.Invalid;
            }

            var entryType = entry.GetType();
            var keyProperty = entryType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public);
            var valueProperty = entryType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            if (keyProperty?.GetValue(entry) is not string key ||
                valueProperty is null ||
                !reflectedValues.TryAdd(key, valueProperty.GetValue(entry)))
            {
                return DictionaryReadResult.Invalid;
            }
        }

        return DictionaryReadResult.Valid(reflectedValues);
    }

    private static SequenceReadResult ReadSequence(object value)
    {
        if (value is string)
        {
            return SequenceReadResult.NotSequence;
        }

        IEnumerable? sequence = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element => element.EnumerateArray(),
            IEnumerable enumerable => enumerable,
            _ => null
        };
        if (sequence is null)
        {
            return SequenceReadResult.NotSequence;
        }

        var values = new List<object?>();
        foreach (var item in sequence)
        {
            if (values.Count >= MaxCollectionItems)
            {
                return SequenceReadResult.Invalid;
            }

            values.Add(item);
        }

        return SequenceReadResult.Valid(values);
    }

    private enum DictionaryKind
    {
        NotDictionary,
        Valid,
        Invalid
    }

    private sealed record DictionaryReadResult(
        DictionaryKind Kind,
        IReadOnlyDictionary<string, object?>? Values = null)
    {
        public static DictionaryReadResult NotDictionary { get; } = new(DictionaryKind.NotDictionary);

        public static DictionaryReadResult Invalid { get; } = new(DictionaryKind.Invalid);

        public static DictionaryReadResult Valid(IReadOnlyDictionary<string, object?> values) =>
            new(DictionaryKind.Valid, values);
    }

    private enum SequenceKind
    {
        NotSequence,
        Valid,
        Invalid
    }

    private sealed record SequenceReadResult(
        SequenceKind Kind,
        IReadOnlyList<object?>? Values = null)
    {
        public static SequenceReadResult NotSequence { get; } = new(SequenceKind.NotSequence);

        public static SequenceReadResult Invalid { get; } = new(SequenceKind.Invalid);

        public static SequenceReadResult Valid(IReadOnlyList<object?> values) =>
            new(SequenceKind.Valid, values);
    }

    private readonly record struct ReferencePair(object Left, object Right);

    private sealed class ReferencePairComparer : IEqualityComparer<ReferencePair>
    {
        public static ReferencePairComparer Instance { get; } = new();

        public bool Equals(ReferencePair x, ReferencePair y) =>
            ReferenceEquals(x.Left, y.Left) && ReferenceEquals(x.Right, y.Right);

        public int GetHashCode(ReferencePair pair) =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(pair.Left),
                RuntimeHelpers.GetHashCode(pair.Right));
    }
}

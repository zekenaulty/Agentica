internal static class CliOptionReader
{
    public static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    public static bool TryReadInt(IReadOnlyList<string> args, ref int index, out int value)
    {
        if (index + 1 >= args.Count ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
            !int.TryParse(args[index + 1], out value))
        {
            value = 0;
            return false;
        }

        index++;
        return true;
    }
}

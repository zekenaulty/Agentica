using System.Collections.ObjectModel;
using System.Text;

namespace Agentica.Validation;

/// <summary>
/// Bounds validation fan-out before it becomes returned proof. Once the collector
/// reaches its item or byte budget it stops accepting detail and emits one explicit
/// truncation issue, preserving fail-closed semantics without unbounded amplification.
/// </summary>
internal sealed class ValidationIssueCollector
{
    private const int MaxIssues = 1_024;
    private const int MaxBytes = 1024 * 1024;
    private const int MaxCodeBytes = 512;
    private const int MaxMessageBytes = 4 * 1024;
    private const int MaxStepIdBytes = 4 * 1024;
    private const string TruncationCode = "plan.validation.truncated";
    private const string TruncationMessage =
        "Additional validation issues were omitted after the bounded validation-proof budget was reached.";

    private static readonly int ReservedTruncationBytes =
        Encoding.UTF8.GetByteCount(TruncationCode) +
        Encoding.UTF8.GetByteCount(TruncationMessage);

    private readonly List<ValidationIssue> _issues = [];
    private int _remainingBytes = MaxBytes - ReservedTruncationBytes;
    private bool _truncated;
    private bool _full;

    public bool IsFull => _full;

    public void Exhaust()
    {
        _truncated = true;
        _full = true;
    }

    public void ReachWorkLimit()
    {
        if (_full)
        {
            return;
        }

        Add(new ValidationIssue(
            "plan.validation.work_limit",
            "Plan validation stopped after reaching the bounded validation-work limit."));
        _full = true;
    }

    public static string Display(string? value) =>
        value is null
            ? "<null>"
            : Bound(value, maximumBytes: 512, out _);

    public static bool IsDisplayBounded(string? value)
    {
        if (value is null || value.Length > 512)
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(value) <= 512;
    }

    public bool Add(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (_full)
        {
            _truncated = true;
            return false;
        }

        if (_issues.Count >= MaxIssues - 1)
        {
            _truncated = true;
            _full = true;
            return false;
        }

        var code = Bound(issue.Code, MaxCodeBytes, out var codeTruncated);
        var message = Bound(issue.Message, MaxMessageBytes, out var messageTruncated);
        var stepIdTruncated = false;
        var stepId = issue.StepId is null
            ? null
            : Bound(issue.StepId, MaxStepIdBytes, out stepIdTruncated);
        var bytes = Encoding.UTF8.GetByteCount(code) +
                    Encoding.UTF8.GetByteCount(message) +
                    (stepId is null ? 0 : Encoding.UTF8.GetByteCount(stepId));
        if (bytes > _remainingBytes)
        {
            _truncated = true;
            _full = true;
            return false;
        }

        _remainingBytes -= bytes;
        _truncated |= codeTruncated || messageTruncated || stepIdTruncated;
        _issues.Add(new ValidationIssue(code, message, stepId));
        return true;
    }

    public void AddRange(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        foreach (var issue in issues)
        {
            if (!Add(issue))
            {
                break;
            }
        }
    }

    public IReadOnlyList<ValidationIssue> Complete()
    {
        var snapshot = new List<ValidationIssue>(_issues);
        if (_truncated)
        {
            snapshot.Add(new ValidationIssue(TruncationCode, TruncationMessage));
        }

        return new ReadOnlyCollection<ValidationIssue>(snapshot);
    }

    private static string Bound(string value, int maximumBytes, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= maximumBytes &&
            Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            truncated = false;
            return value;
        }

        const string suffix = "\u2026";
        var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
        var low = 0;
        var high = Math.Min(value.Length, maximumBytes - suffixBytes);
        while (low < high)
        {
            var candidate = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, candidate)) <= maximumBytes - suffixBytes)
            {
                low = candidate;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (low > 0 && low < value.Length && char.IsHighSurrogate(value[low - 1]))
        {
            low--;
        }

        truncated = true;
        return string.Concat(value.AsSpan(0, low), suffix);
    }
}

internal sealed class ValidationWorkBudget
{
    private const int MaxWorkUnits = 100_000;
    private int _remaining = MaxWorkUnits;

    public bool TryConsume(ValidationIssueCollector issues, int units = 1)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        if (units > _remaining)
        {
            issues.ReachWorkLimit();
            return false;
        }

        _remaining -= units;
        return true;
    }
}

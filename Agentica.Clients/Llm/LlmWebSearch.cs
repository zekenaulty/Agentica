using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public enum LlmWebSearchMode { Auto, Required }
public enum LlmCitationIndexUnit { ProviderCharacters, Utf16CodeUnits, UnicodeScalars }

/// <summary>Opt-in provider-native search. Its limit is sent to the provider and checked against observed calls.</summary>
public sealed record LlmWebSearchOptions
{
    public LlmWebSearchOptions(LlmWebSearchMode mode = LlmWebSearchMode.Auto, int maxToolCalls = 3,
        LlmCitationIndexUnit citationIndexUnit = LlmCitationIndexUnit.ProviderCharacters)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (maxToolCalls is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxToolCalls));
        if (!Enum.IsDefined(citationIndexUnit)) throw new ArgumentOutOfRangeException(nameof(citationIndexUnit));
        CitationIndexUnit = citationIndexUnit;
        Mode = mode;
        MaxToolCalls = maxToolCalls;
    }
    public LlmCitationIndexUnit CitationIndexUnit { get; }
    public LlmWebSearchMode Mode { get; }
    public int MaxToolCalls { get; }
}

/// <summary>External, untrusted provider-reported source; a citation is not verification or authority.</summary>
public sealed record LlmWebSource
{
    public LlmWebSource(string url, string? title = null)
    {
        if (!IsSafeUrl(url)) throw new ArgumentException("A bounded HTTP(S) URL without user info is required.", nameof(url));
        Url = url;
        Title = Text(title, 1024, nameof(title));
    }
    public string Url { get; }
    public string? Title { get; }
    internal static bool IsSafeUrl(string? value) => value is { Length: > 0 and <= 4096 } &&
        !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.UserInfo.Length == 0 && uri.Scheme is "http" or "https";
    internal static string? Text(string? value, int maximum, string name) =>
        value is null || value.Length <= maximum ? value : throw new ArgumentOutOfRangeException(name);
}

/// <summary>Native annotation positions and their projection into unchanged canonical answer text.</summary>
public sealed record LlmWebCitation
{
    public LlmWebCitation(LlmWebSource source, int? startIndex = null, int? endIndex = null,
        int? outputIndex = null, int? contentIndex = null, int? textOffset = null,
        int? utf16StartIndex = null, int? utf16EndIndex = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (startIndex < 0 || endIndex < 0 || outputIndex < 0 || contentIndex < 0 || textOffset < 0 ||
            utf16StartIndex < 0 || utf16EndIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        StartIndex = startIndex;
        EndIndex = endIndex;
        OutputIndex = outputIndex;
        ContentIndex = contentIndex;
        TextOffset = textOffset;
        Utf16StartIndex = utf16StartIndex;
        Utf16EndIndex = utf16EndIndex;
    }
    public LlmWebSource Source { get; }
    public int? StartIndex { get; }
    public int? EndIndex { get; }
    public int? OutputIndex { get; }
    public int? ContentIndex { get; }
    public int? TextOffset { get; }
    public int? Utf16StartIndex { get; }
    public int? Utf16EndIndex { get; }
    public int? AnswerStartIndex => Offset(Utf16StartIndex);
    public int? AnswerEndIndex => Offset(Utf16EndIndex);
    private int? Offset(int? value) => value is { } index && TextOffset is { } offset && index <= int.MaxValue - offset
        ? index + offset : null;
}

/// <summary>Observed search action. Missing query/queries remain missing; they are not inferred from user text.</summary>
public sealed record LlmWebSearchCall
{
    public LlmWebSearchCall(string? callId, int? outputIndex, string? status, string? actionType,
        string? query = null, IReadOnlyList<string>? queries = null)
    {
        CallId = LlmWebSource.Text(callId, 512, nameof(callId));
        if (outputIndex < 0) throw new ArgumentOutOfRangeException(nameof(outputIndex));
        OutputIndex = outputIndex;
        Status = LlmWebSource.Text(status, 128, nameof(status));
        ActionType = LlmWebSource.Text(actionType, 128, nameof(actionType));
        Query = LlmWebSource.Text(query, 4096, nameof(query));
        if (queries?.Count > 16 || queries?.Any(item => item is null || item.Length > 4096) == true)
            throw new ArgumentOutOfRangeException(nameof(queries));
        Queries = queries is null ? null : Array.AsReadOnly(queries.ToArray());
    }
    public string? CallId { get; }
    public int? OutputIndex { get; }
    public string? Status { get; }
    public string? ActionType { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Query { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Queries { get; }
}

/// <summary>Detached bounded external search evidence. Separate from structural attempt receipts and private reasoning.</summary>
public sealed record LlmWebSearchEvidence
{
    public LlmWebSearchEvidence(IReadOnlyList<LlmWebSearchCall> calls,
        IReadOnlyList<LlmWebSource> sources, IReadOnlyList<LlmWebCitation> citations, bool referencesTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(citations);
        if (calls.Count > 64 || sources.Count > 256 || citations.Count > 256 ||
            calls.Any(item => item is null) || sources.Any(item => item is null) || citations.Any(item => item is null))
            throw new ArgumentOutOfRangeException(nameof(calls));
        Calls = Array.AsReadOnly(calls.ToArray());
        Sources = Array.AsReadOnly(sources.ToArray());
        Citations = Array.AsReadOnly(citations.ToArray());
        ReferencesTruncated = referencesTruncated;
    }
    public IReadOnlyList<LlmWebSearchCall> Calls { get; }
    public IReadOnlyList<LlmWebSource> Sources { get; }
    public IReadOnlyList<LlmWebCitation> Citations { get; }
    public bool ReferencesTruncated { get; }
    public int CompletedCalls => Calls.Count(item => item.Status == "completed");
    public string Trust => "external-untrusted";
}

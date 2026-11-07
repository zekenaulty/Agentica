using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

/// <summary>Bounded projection of public external references, never private provider reasoning.</summary>
internal sealed class OpenAiWebSearchState(LlmWebSearchOptions options, LlmAttemptRecorder recorder)
{
    private readonly List<LlmWebSearchCall> _calls = [];
    private readonly List<LlmWebSource> _sources = [];
    private readonly List<LlmWebCitation> _citations = [];
    private bool _truncated;

    internal bool Observe(JsonElement envelope, string? kind)
    {
        var changed = false;
        if (kind is "response.output_item.added" or "response.output_item.done")
        {
            var item = Object(envelope, "item");
            if (String(item, "type") == "web_search_call")
            { ObserveCall(item, Number(envelope, "output_index")); changed = true; }
            else if (kind == "response.output_item.done" && String(item, "type") == "message")
            { ObserveMessage(item, Number(envelope, "output_index"), null); changed = true; }
        }
        else if (kind is "response.web_search_call.in_progress" or "response.web_search_call.searching" or "response.web_search_call.completed")
        {
            var id = String(envelope, "item_id", 512);
            var index = Number(envelope, "output_index");
            var status = kind["response.web_search_call.".Length..];
            Upsert(new(id, index, status, null));
            changed = true;
        }
        else if (kind == "response.output_text.annotation.added")
        {
            AddCitation(Object(envelope, "annotation"), Number(envelope, "output_index"),
                Number(envelope, "content_index"), null, null);
            changed = true;
        }
        var response = Object(envelope, "response");
        var output = Object(response, "output");
        if (output.ValueKind == JsonValueKind.Array)
        { ObserveOutput(output); changed = true; }
        if (changed) Publish();
        return changed;
    }

    internal void ObserveOutput(JsonElement output)
    {
        var offset = 0;
        var outputIndex = 0;
        foreach (var item in output.EnumerateArray())
        {
            if (String(item, "type") == "web_search_call") ObserveCall(item, outputIndex);
            if (String(item, "type") == "message")
            {
                offset = ObserveMessage(item, outputIndex, offset) ?? offset;
            }
            outputIndex++;
        }
        Publish();
    }

    private int? ObserveMessage(JsonElement item, int? outputIndex, int? offset)
    {
        var contents = Object(item, "content");
        var contentIndex = 0;
        if (contents.ValueKind == JsonValueKind.Array)
            foreach (var part in contents.EnumerateArray())
            {
                if (String(part, "type") == "output_text")
                {
                    var text = String(part, "text", 4_194_304) ?? string.Empty;
                    var annotations = Object(part, "annotations");
                    if (annotations.ValueKind == JsonValueKind.Array)
                        foreach (var annotation in annotations.EnumerateArray())
                            AddCitation(annotation, outputIndex, contentIndex, offset, text);
                    offset = offset is { } value && text.Length <= int.MaxValue - value ? value + text.Length : null;
                }
                contentIndex++;
            }
        return offset;
    }

    internal void ValidateCompletion()
    {
        if (_calls.Count > options.MaxToolCalls || _truncated && _calls.Count == 64)
            throw Failure("web_search_call_limit");
        if (options.Mode == LlmWebSearchMode.Required && (_calls.Count == 0 || !_calls.Any(item => item.Status == "completed")))
            throw Failure("web_search_not_completed");
    }

    private void ObserveCall(JsonElement item, int? index)
    {
        var action = Object(item, "action");
        IReadOnlyList<string>? queries = null;
        var nativeQueries = Object(action, "queries");
        if (nativeQueries.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (var query in nativeQueries.EnumerateArray())
            {
                if (values.Count == 16) { _truncated = true; break; }
                if (query.ValueKind == JsonValueKind.String && query.GetString() is { Length: <= 4096 } text)
                    values.Add(text);
                else _truncated = true;
            }
            queries = values.AsReadOnly();
        }
        var sources = Object(action, "sources");
        AddSource(action);
        if (sources.ValueKind == JsonValueKind.Array)
            foreach (var source in sources.EnumerateArray()) AddSource(source);
        Upsert(new(String(item, "id", 512), index, String(item, "status"), String(action, "type"),
            String(action, "query", 4096), queries));
    }

    private void Upsert(LlmWebSearchCall call)
    {
        var found = _calls.FindIndex(old => call.CallId is not null && old.CallId == call.CallId ||
            call.OutputIndex is not null && old.OutputIndex == call.OutputIndex);
        if (found >= 0)
        {
            var old = _calls[found];
            if (old.CallId is not null && call.CallId is not null && old.CallId != call.CallId ||
                old.OutputIndex is not null && call.OutputIndex is not null && old.OutputIndex != call.OutputIndex)
            {
                if (_calls.Count < 64) _calls.Add(call);
                else _truncated = true;
                Publish();
                throw Failure("web_search_identity_conflict");
            }
            _calls[found] = new(call.CallId ?? old.CallId, call.OutputIndex ?? old.OutputIndex,
                call.Status ?? old.Status, call.ActionType ?? old.ActionType,
                call.Query ?? old.Query, call.Queries ?? old.Queries);
        }
        else if (_calls.Count < 64) _calls.Add(call);
        else _truncated = true;
    }

    private LlmWebSource? Source(JsonElement value)
    {
        var url = String(value, "url", 4096);
        if (!LlmWebSource.IsSafeUrl(url)) return null;
        return new(url!, String(value, "title", 1024));
    }
    private void AddSource(JsonElement value)
    {
        if (Source(value) is not { } source) return;
        var old = _sources.FindIndex(item => item.Url == source.Url);
        if (old >= 0)
        {
            if (_sources[old].Title is null && source.Title is not null) _sources[old] = source;
            return;
        }
        if (_sources.Count == 256) { _truncated = true; return; }
        _sources.Add(source);
    }

    private void AddCitation(JsonElement annotation, int? outputIndex, int? contentIndex, int? offset, string? text)
    {
        if (String(annotation, "type") != "url_citation" || Source(annotation) is not { } source) return;
        var start = Number(annotation, "start_index");
        var end = Number(annotation, "end_index");
        var citation = new LlmWebCitation(source, start, end, outputIndex, contentIndex, offset,
            start <= end ? ProjectIndex(text, start) : null, start <= end ? ProjectIndex(text, end) : null);
        var found = _citations.FindIndex(old => old.OutputIndex == outputIndex && old.ContentIndex == contentIndex &&
            old.StartIndex == start && old.EndIndex == end && old.Source.Url == source.Url);
        if (found >= 0) _citations[found] = citation;
        else if (_citations.Count < 256) _citations.Add(citation);
        else _truncated = true;
        AddSource(annotation);
    }

    private int? ProjectIndex(string? text, int? native)
    {
        if (text is null || native is not { } index) return null;
        if (options.CitationIndexUnit == LlmCitationIndexUnit.UnicodeScalars)
        {
            var scalar = 0;
            var utf16 = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (scalar == index) return utf16;
                scalar++;
                utf16 += rune.Utf16SequenceLength;
            }
            return scalar == index ? utf16 : null;
        }
        if (index > text.Length || index > 0 && index < text.Length && char.IsLowSurrogate(text[index])) return null;
        if (options.CitationIndexUnit == LlmCitationIndexUnit.ProviderCharacters && text.Any(char.IsSurrogate)) return null;
        return index;
    }

    private void Publish() => recorder.ObserveWebSearch(new(_calls, _sources, _citations, _truncated));
    private static LlmClientException Failure(string code) => new(OpenAiResponsesLlmClient.ProviderName,
        "Provider-native search did not satisfy its requested completion contract.", errorClass: code);
    private static JsonElement Object(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    private static int? Number(JsonElement value, string key) => Object(value, key) is var found &&
        found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var number) && number >= 0 ? number : null;
    private string? String(JsonElement value, string key, int maximum = 128)
    {
        var found = Object(value, key);
        if (found.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (found.ValueKind == JsonValueKind.String && found.GetString() is { } text && text.Length <= maximum)
            return text;
        _truncated = true;
        return null;
    }
}

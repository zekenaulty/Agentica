using System.Text;
using System.Data.Common;

namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>Deterministic exact-source selection. It never summarizes or performs model inference.</summary>
internal static class ExperimentRecallAllocator
{
    internal static ExperimentPreparedContext Prepare(ExperimentConversation conversation, ExperimentTurn turn,
        IReadOnlyList<ExperimentMessage> eligible, IConversationEvidenceSource? source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = turn.Settings;
        var recent = eligible.TakeLast(settings.ThreadRecallRecentTurns * 2).ToArray();
        var selectedIds = recent.Select(message => message.Id).Append(turn.Id + ":user").ToArray();
        var omittedIds = eligible.Where(message => !selectedIds.Contains(message.Id, StringComparer.Ordinal))
            .Select(message => message.Id).ToArray();
        var asOf = turn.Sequence - 1;
        if (source is not null) ValidateSource(source, conversation.Id, asOf);
        cancellationToken.ThrowIfCancellationRequested();
        if (recent.Length > settings.MaxHistoryMessages || !Fits(settings, recent, turn.UserText, null))
            throw new ExperimentException("context.recent_floor_limit",
                "The current input, instructions and protected recent-turn floor do not fit the configured complete-input allowance. " +
                "Reduce the recent-turn floor or current input, or adjust character/window/output limits. Optional recall cannot displace that floor. No model call was made.", 409);

        var omissions = new List<ExperimentRecallOmission>();
        var candidates = new List<ConversationEvidenceHit>();
        var calls = 0;
        var candidateCount = 0;
        var inspectedCount = 0;
        var sourceTruncated = false;
        string? effectiveQuery = null;
        object? coverage = null;
        ConversationEvidenceHead? head = null;
        var sourceUnavailable = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            coverage = source?.GetCoverage();
            cancellationToken.ThrowIfCancellationRequested();
            head = source?.GetHead();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (IsSourceUnavailable(error))
        {
            sourceUnavailable = true;
            coverage = new { status = "UNAVAILABLE", asOfSequence = asOf, reason = "recall.source_unavailable", lastKnownCoverage = coverage };
        }
        if (head is not null && (head.ScopeRef != conversation.Id || head.AsOfSequence != asOf))
            throw new ExperimentException("recall.scope", "The historical source head does not match the frozen conversation scope.");
        var mode = settings.ThreadRecallMode;
        var method = mode == "agent" ? "agent-directed; no host query" : mode == "manual" ? "operator exact ranges" : "local lexical lookup";
        var queryOrigin = mode == "manual" ? "operator-selected exact ranges" : mode == "agent" ? "agent tools only" :
            settings.ThreadRecallQuery.Length == 0 ? "current user input" : "operator query";

        ConversationEvidenceResult? Retrieve(Func<ConversationEvidenceResult> operation, string sourceId, int start, int end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls++;
            try
            {
                var result = operation();
                cancellationToken.ThrowIfCancellationRequested();
                ValidateResult(result, conversation.Id, asOf);
                return result;
            }
            catch (Exception error) when (IsSourceUnavailable(error))
            {
                omissions.Add(new(sourceId, start, end, "source-unavailable; no fallback retrieval was performed"));
                coverage = new { status = "UNAVAILABLE", asOfSequence = asOf, reason = "recall.source_unavailable", lastKnownCoverage = coverage };
                return null;
            }
        }

        if (source is null || sourceUnavailable)
        {
            if (source is null) coverage = new { status = "NOT_READY", reason = "No conversation evidence source is bound." };
            omissions.Add(new("", 0, 0, "source-unavailable; historical evidence was not searched"));
        }
        else if (mode == "manual")
        {
            foreach (var selection in settings.ThreadRecallSelections.Take(settings.ThreadRecallTopK))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (calls >= settings.ThreadRecallMaxCalls)
                {
                    omissions.Add(new(selection.SourceId, selection.StartOffset, selection.EndOffset, "source-call-limit"));
                    continue;
                }
                var length = Math.Min(selection.EndOffset - selection.StartOffset, settings.ThreadRecallMaxCharacters);
                var result = Retrieve(() => source.ReadExact(selection.SourceId, selection.StartOffset, length, cancellationToken), selection.SourceId, selection.StartOffset, selection.EndOffset);
                if (result is null) continue;
                coverage = result.Coverage;
                sourceTruncated |= result.Truncated;
                candidateCount += result.CandidateCount; inspectedCount += result.InspectedCount;
                candidates.AddRange(result.Hits);
                if (result.Hits.Count == 0)
                    omissions.Add(new(selection.SourceId, selection.StartOffset, selection.EndOffset, result.Status + ": " + string.Join(", ", result.Reasons)));
                var returnedEnd = result.Hits.Where(hit => hit.Source.SourceId == selection.SourceId)
                    .Select(hit => hit.EndOffset).DefaultIfEmpty(selection.StartOffset).Max();
                if (returnedEnd < selection.EndOffset && result.Hits.Count != 0)
                    omissions.Add(new(selection.SourceId, returnedEnd, selection.EndOffset,
                        length < selection.EndOffset - selection.StartOffset ? "retrieval-character-limit" : "requested source range not fully returned"));
                if (settings.ThreadRecallNeighborRadius > 0 && result.Hits.Count != 0 && calls < settings.ThreadRecallMaxCalls)
                {
                    var neighbors = Retrieve(() => source.ReadNeighbors(selection.SourceId, settings.ThreadRecallNeighborRadius, settings.ThreadRecallMaxCharacters, cancellationToken),
                        selection.SourceId, selection.StartOffset, selection.EndOffset);
                    if (neighbors is null) continue;
                    candidateCount += neighbors.CandidateCount; inspectedCount += neighbors.InspectedCount;
                    candidates.AddRange(neighbors.Hits.Where(hit => hit.Source.SourceId != selection.SourceId));
                    sourceTruncated |= neighbors.Truncated;
                }
                else if (settings.ThreadRecallNeighborRadius > 0 && result.Hits.Count != 0)
                    omissions.Add(new(selection.SourceId, selection.StartOffset, selection.EndOffset, "neighbor-expansion-call-limit"));
            }
            foreach (var selection in settings.ThreadRecallSelections.Skip(settings.ThreadRecallTopK))
                omissions.Add(new(selection.SourceId, selection.StartOffset, selection.EndOffset, "selection-count-limit"));
        }
        else if (mode == "auto")
        {
            var query = settings.ThreadRecallQuery.Length == 0 ? turn.UserText : settings.ThreadRecallQuery;
            if (string.IsNullOrWhiteSpace(query)) omissions.Add(new("", 0, 0, "empty-query; historical evidence was not searched"));
            else
            {
                // Local query length is bounded independently from the unchanged current user input.
                var boundedQuery = ExactPrefix(query, 512);
                effectiveQuery = boundedQuery;
                if (boundedQuery.Length != query.Length) omissions.Add(new("", boundedQuery.Length, query.Length, "query-character-limit"));
                var result = Retrieve(() => source.Search(new(boundedQuery, settings.ThreadRecallTopK, settings.ThreadRecallMaxCharacters,
                    settings.ThreadRecallNeighborRadius), cancellationToken), "", 0, 0);
                if (result is not null)
                {
                    coverage = result.Coverage; method = result.QueryMethod;
                    candidateCount = result.CandidateCount; inspectedCount = result.InspectedCount;
                    sourceTruncated = result.Truncated;
                    var expandedSources = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var hit in result.Hits)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        candidates.Add(hit); // Preserve the exact primary anchor before optional neighboring text.
                        if (settings.ThreadRecallNeighborRadius == 0 || !expandedSources.Add(hit.Source.SourceId)) continue;
                        if (calls >= settings.ThreadRecallMaxCalls)
                        {
                            omissions.Add(new(hit.Source.SourceId, hit.StartOffset, hit.EndOffset, "neighbor-expansion-call-limit"));
                            continue;
                        }
                        var neighbors = Retrieve(() => source.ReadNeighbors(hit.Source.SourceId, settings.ThreadRecallNeighborRadius, settings.ThreadRecallMaxCharacters, cancellationToken),
                            hit.Source.SourceId, hit.StartOffset, hit.EndOffset);
                        if (neighbors is null) continue;
                        candidateCount += neighbors.CandidateCount; inspectedCount += neighbors.InspectedCount;
                        candidates.AddRange(neighbors.Hits.Where(neighbor => neighbor.Source.SourceId != hit.Source.SourceId));
                        sourceTruncated |= neighbors.Truncated;
                    }
                    foreach (var reason in result.Reasons) omissions.Add(new("", 0, 0, reason));
                    if (result.Hits.Count == 0) omissions.Add(new("", 0, 0, result.Status));
                }
            }
        }

        var accepted = new List<ExperimentRecallEvidence>();
        var recentIds = recent.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        ExperimentRecallContext Context() => new(new(true, mode, conversation.Id, asOf, queryOrigin, method, coverage,
            candidateCount, inspectedCount, accepted.ToArray(), omissions.ToArray(), accepted.Sum(item => item.Content.Length),
            sourceTruncated || omissions.Any(item => item.Reason.Contains("limit", StringComparison.Ordinal)),
            turn.Settings.Mode == "clean-room" ? "direct provider evidence input part" : "Agentica planning context evidence",
            calls, Query: effectiveQuery, SourceHead: head), accepted.ToArray());
        foreach (var hit in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateHit(hit, asOf);
            if (recentIds.Contains(hit.Source.SourceId))
            {
                omissions.Add(new(hit.Source.SourceId, hit.StartOffset, hit.EndOffset, "already-present-in-recent-dialogue"));
                continue;
            }
            var parts = NonOverlapping(hit, accepted);
            if (parts.Count == 0)
            {
                omissions.Add(new(hit.Source.SourceId, hit.StartOffset, hit.EndOffset, "duplicate-or-overlapping-evidence"));
                continue;
            }
            if (parts.Sum(part => part.Content.Length) < hit.Excerpt.Length)
                omissions.Add(new(hit.Source.SourceId, hit.StartOffset, hit.EndOffset, "overlapping portions deduplicated"));
            foreach (var part in parts)
            {
                var maximum = Math.Min(part.Content.Length, settings.ThreadRecallMaxCharacters - accepted.Sum(item => item.Content.Length));
                var low = 0; var high = Math.Max(0, maximum);
                while (low < high)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var middle = low + (high - low + 1) / 2;
                    var content = ExactPrefix(part.Content, middle);
                    var proposal = part with { Content = content, EndOffset = part.StartOffset + content.Length };
                    accepted.Add(proposal);
                    var fits = Fits(settings, recent, turn.UserText, Context());
                    accepted.RemoveAt(accepted.Count - 1);
                    if (fits) low = middle; else high = middle - 1;
                }
                var chosen = ExactPrefix(part.Content, low);
                if (chosen.Length != 0) accepted.Add(part with { Content = chosen, EndOffset = part.StartOffset + chosen.Length });
                if (chosen.Length < part.Content.Length)
                    omissions.Add(new(part.SourceId, part.StartOffset + chosen.Length, part.EndOffset,
                        maximum < part.Content.Length ? "retrieval-character-limit" : "complete-request-input-limit"));
            }
        }
        var recall = Context();
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = ExperimentDirectRequest.Build(settings, recent, turn.UserText, recall);
        var units = settings.Mode == "clean-room" ? envelope.EstimatedInputUnits : HarnessUnits(settings, recent, turn.UserText, recall);
        return new(recent, new(conversation.Id, turn.Id, selectedIds, omittedIds,
            settings.Instructions.Length + turn.UserText.Length + recent.Sum(message => message.Content.Length) + recall.Manifest.PresentedCharacters,
            units, ExperimentDirectRequest.InputAllowance(settings),
            settings.Mode == "clean-room" ? "utf8-wire-byte-proxy-plus-512; exact direct request including recall; not measured provider tokens" :
                "conservative UTF-8 content projection including quoted recall; complete harness wire guard remains authoritative",
            "thread-recall protected recent floor; exact optional historical evidence", turn.Continuity))
        { Recall = recall };
    }

    private static bool Fits(ExperimentSettings settings, IReadOnlyList<ExperimentMessage> recent, string userText, ExperimentRecallContext? recall)
    {
        if (settings.Mode == "clean-room")
        {
            var envelope = ExperimentDirectRequest.Build(settings, recent, userText, recall);
            return envelope.SerializedCharacters <= settings.MaxInputCharacters && envelope.EstimatedInputUnits <= ExperimentDirectRequest.InputAllowance(settings);
        }
        var evidence = recall is { Evidence.Count: > 0 } ? ExperimentDirectRequest.FormatEvidence(recall) : "";
        return settings.Instructions.Length + userText.Length + recent.Sum(message => message.Content.Length) + evidence.Length <= settings.MaxInputCharacters &&
            HarnessUnits(settings, recent, userText, recall) <= ExperimentDirectRequest.InputAllowance(settings);
    }

    private static int HarnessUnits(ExperimentSettings settings, IReadOnlyList<ExperimentMessage> recent, string userText, ExperimentRecallContext? recall) =>
        Encoding.UTF8.GetByteCount(settings.Instructions) + Encoding.UTF8.GetByteCount(userText) +
        recent.Sum(message => Encoding.UTF8.GetByteCount(message.Content) + 64) + 1024 +
        (recall is { Evidence.Count: > 0 } ? Encoding.UTF8.GetByteCount(ExperimentDirectRequest.FormatEvidence(recall)) : 0);

    internal static void ValidateSource(IConversationEvidenceSource source, string scopeRef, int asOf)
    {
        if (source.ScopeRef != scopeRef || source.AsOfSequence != asOf)
            throw new ExperimentException("recall.scope", "The historical evidence source does not match this conversation and frozen turn boundary.");
    }

    internal static void ValidateResult(ConversationEvidenceResult result, string scopeRef, int asOf)
    {
        if (result.ScopeRef != scopeRef || result.AsOfSequence != asOf || result.Coverage.AsOfSequence != asOf)
            throw new ExperimentException("recall.scope", "The historical evidence result does not match its bound scope.");
        foreach (var hit in result.Hits) ValidateHit(hit, asOf);
    }

    private static void ValidateHit(ConversationEvidenceHit hit, int asOf)
    {
        if (hit.Source.TurnSequence > asOf || hit.Source.Role is not ("user" or "assistant") || hit.StartOffset < 0 ||
            hit.EndOffset < hit.StartOffset || hit.EndOffset > hit.Source.CharacterCount || hit.EndOffset - hit.StartOffset != hit.Excerpt.Length)
            throw new ExperimentException("recall.source_integrity", "The evidence source returned an invalid historical range or eligibility marker.");
    }

    private static List<ExperimentRecallEvidence> NonOverlapping(ConversationEvidenceHit hit, IReadOnlyList<ExperimentRecallEvidence> accepted)
    {
        var ranges = new List<(int Start, int End)> { (hit.StartOffset, hit.EndOffset) };
        foreach (var prior in accepted.Where(item => item.SourceId == hit.Source.SourceId))
        {
            var remaining = new List<(int Start, int End)>();
            foreach (var range in ranges)
            {
                if (prior.EndOffset <= range.Start || prior.StartOffset >= range.End) remaining.Add(range);
                else
                {
                    if (prior.StartOffset > range.Start) remaining.Add((range.Start, prior.StartOffset));
                    if (prior.EndOffset < range.End) remaining.Add((prior.EndOffset, range.End));
                }
            }
            ranges = remaining;
        }
        return ranges.Select(range => new ExperimentRecallEvidence(hit.Source.SourceId, hit.Source.TurnId, hit.Source.TurnSequence,
            hit.Source.Role, hit.Source.CreatedAt, hit.Source.ContentHash, range.Start, range.End,
            hit.Excerpt.Substring(range.Start - hit.StartOffset, range.End - range.Start), hit.MatchReason)).ToList();
    }

    internal static string ExactPrefix(string text, int maximum)
    {
        var count = Math.Min(maximum, text.Length);
        if (count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1]) && char.IsLowSurrogate(text[count])) count--;
        return text[..count];
    }

    internal static bool IsSourceUnavailable(Exception error) => error is IOException or DbException ||
        error is ExperimentException { Code: "recall.unavailable" or "recall.not_ready" or "recall.index_unavailable" };
}

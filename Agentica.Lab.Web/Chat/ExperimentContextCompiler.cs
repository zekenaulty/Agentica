using System.Text;
using System.Security.Cryptography;
using Agentica.Lab.Web.Chat.Recall;

namespace Agentica.Lab.Web.Chat;

/// <summary>Only this conversation's complete visible dialogue is eligible. No memory service is bound.</summary>
public static class ExperimentContextCompiler
{
    public static ExperimentPreparedContext Prepare(ExperimentConversation conversation, ExperimentTurn turn,
        IConversationEvidenceSource? evidenceSource = null, CancellationToken cancellationToken = default)
        => PrepareAsync(conversation, turn, ExperimentByteProxyCounter.Instance, evidenceSource, cancellationToken).GetAwaiter().GetResult();

    public static async Task<ExperimentPreparedContext> PrepareAsync(ExperimentConversation conversation, ExperimentTurn turn,
        IExperimentInputTokenCounter counter, IConversationEvidenceSource? evidenceSource = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counter);
        cancellationToken.ThrowIfCancellationRequested();
        // Only explicit provider-free compatibility callers retain the old proxy policy.
        if (counter is ExperimentByteProxyCounter) return PrepareCompatibility(conversation, turn, evidenceSource, cancellationToken);
        if (conversation.Id != turn.ConversationId) throw new ExperimentException("context.scope", "Conversation context does not match this turn.");
        var settings = turn.Settings;
        ExperimentStore.ValidateSettings(settings);
        var eligible = conversation.Turns.Where(item => item.Sequence < turn.Sequence && item.Status == "completed")
            .SelectMany(item => new[] { new ExperimentMessage(item.Id + ":user", "user", item.UserText, item.CreatedAt),
                new ExperimentMessage(item.Id + ":assistant", "assistant", item.AssistantText, item.FinishedAt ?? item.CreatedAt) }).ToArray();
        var measurements = new ExperimentContextMeasurements(counter);
        if (settings.ThreadRecallEnabled)
            return await ExperimentRecallAllocator.PrepareAsync(conversation, turn, eligible, evidenceSource, measurements, cancellationToken).ConfigureAwait(false);
        if (settings.Mode == "harness") return PrepareHarnessProjection(conversation, turn, eligible, cancellationToken);
        return await PrepareMeasuredDirectAsync(conversation, turn, eligible, measurements, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExperimentPreparedContext> PrepareMeasuredDirectAsync(ExperimentConversation conversation, ExperimentTurn turn,
        ExperimentMessage[] eligible, ExperimentContextMeasurements measurements, CancellationToken cancellationToken)
    {
        var settings = turn.Settings;
        var allowance = ExperimentDirectRequest.InputAllowance(settings);
        var current = ExperimentDirectRequest.Build(settings, [], turn.UserText);
        if (current.SerializedCharacters > settings.MaxInputCharacters)
            throw ExperimentContextMeasurements.Limit("context.current_input_limit", settings, "serialized request character limit", current, null,
                current, null, 0, 0, "The current input and instructions alone exceed the character limit; changing history or token settings cannot repair that character failure.");

        var pairs = eligible.Length / 2;
        var maximumPairs = Math.Min(pairs, settings.MaxHistoryMessages / 2);
        ExperimentMessage[] Suffix(int count) => eligible.Skip(eligible.Length - count * 2).ToArray();
        var keep = pairs;
        if (settings.HistoryPolicy == "recent")
        {
            // Character-size selection is local; it does not spend provider count requests.
            var low = 0; var high = maximumPairs;
            while (low < high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = low + (high - low + 1) / 2;
                var proposal = Suffix(middle);
                var rawCharacters = settings.Instructions.Length + turn.UserText.Length + proposal.Sum(item => item.Content.Length);
                if (rawCharacters <= settings.MaxInputCharacters &&
                    ExperimentDirectRequest.Build(settings, proposal, turn.UserText).SerializedCharacters <= settings.MaxInputCharacters) low = middle;
                else high = middle - 1;
            }
            keep = low;
        }
        else
        {
            var rawCharacters = settings.Instructions.Length + turn.UserText.Length + eligible.Sum(item => item.Content.Length);
            if (rawCharacters > settings.MaxInputCharacters)
                throw ExperimentContextMeasurements.CharacterLowerBoundLimit("context.history_limit", settings, current, rawCharacters, eligible.Length, pairs,
                    "The current input fits the character limit. Select recent-history omission or change the applicable history/character limit; token settings do not repair a character failure.");
            var full = ExperimentDirectRequest.Build(settings, eligible, turn.UserText);
            if (eligible.Length > settings.MaxHistoryMessages || full.SerializedCharacters > settings.MaxInputCharacters)
            {
                var gates = string.Join(", ", new[] { eligible.Length > settings.MaxHistoryMessages ? "history-message limit" : null,
                    full.SerializedCharacters > settings.MaxInputCharacters ? "serialized request character limit" : null }.Where(item => item is not null));
                throw ExperimentContextMeasurements.Limit("context.history_limit", settings, gates, full, null, current, null, pairs, 0,
                    "The current input fits the character limit. Select recent-history omission or change the applicable history/character limit; token settings do not repair a character failure.");
            }
        }
        var selected = Suffix(keep);
        var envelope = ExperimentDirectRequest.Build(settings, selected, turn.UserText);
        var count = await measurements.CountAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (count.Tokens > allowance)
        {
            var currentCount = await measurements.CountAsync(current, cancellationToken).ConfigureAwait(false);
            if (currentCount.Tokens > allowance)
                throw ExperimentContextMeasurements.Limit("context.current_input_limit", settings, "model input token allowance", envelope, count,
                    current, currentCount, keep, pairs - keep,
                    "The current input and instructions alone exceed the token allowance. History omission cannot repair it; shorten that input or explicitly change the context window/output reservation.");
            if (settings.HistoryPolicy != "recent")
                throw ExperimentContextMeasurements.Limit("context.history_limit", settings, "model input token allowance", envelope, count,
                    current, currentCount, keep, pairs - keep,
                    "The current input alone fits. Select recent-history omission or explicitly change the context window/output reservation; the character limit is not this failed gate.");
            // Current-only is a measured fitting lower bound. Search whole suffix pairs,
            // rather than issuing one remote count for every removed history pair.
            var low = 0; var high = keep - 1;
            var bestEnvelope = current; var bestCount = currentCount;
            while (low < high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = low + (high - low + 1) / 2;
                var proposal = ExperimentDirectRequest.Build(settings, Suffix(middle), turn.UserText);
                var proposedCount = await measurements.CountAsync(proposal, cancellationToken).ConfigureAwait(false);
                if (proposedCount.Tokens <= allowance)
                { low = middle; bestEnvelope = proposal; bestCount = proposedCount; }
                else high = middle - 1;
            }
            keep = low;
            selected = Suffix(keep);
            envelope = ExperimentDirectRequest.Build(settings, selected, turn.UserText);
            count = envelope.Utf8.AsSpan().SequenceEqual(bestEnvelope.Utf8) ? bestCount : await measurements.CountAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ids = selected.Select(item => item.Id).Append(turn.Id + ":user").ToArray();
        var omitted = eligible.Where(item => !ids.Contains(item.Id, StringComparer.Ordinal)).Select(item => item.Id).ToArray();
        return new(selected, new(conversation.Id, turn.Id, ids, omitted,
            settings.Instructions.Length + turn.UserText.Length + selected.Sum(item => item.Content.Length), count.Tokens,
            allowance, count.Method, settings.HistoryPolicy, turn.Continuity)
        { TokenCount = count, WireBytes = envelope.Utf8.Length });
    }

    private static ExperimentPreparedContext PrepareHarnessProjection(ExperimentConversation conversation, ExperimentTurn turn,
        ExperimentMessage[] eligible, CancellationToken cancellationToken)
    {
        var settings = turn.Settings;
        var selected = eligible.ToList();
        int Characters() => settings.Instructions.Length + turn.UserText.Length + selected.Sum(item => item.Content.Length);
        if (settings.Instructions.Length + turn.UserText.Length > settings.MaxInputCharacters)
            throw new ExperimentException("context.current_input_limit", "The current input and instructions alone exceed the character limit. Changing history or token settings cannot repair this character failure. No generation request was dispatched.", 409);
        if (settings.HistoryPolicy == "recent")
            while (selected.Count >= 2 && (selected.Count > settings.MaxHistoryMessages || Characters() > settings.MaxInputCharacters))
            { cancellationToken.ThrowIfCancellationRequested(); selected.RemoveRange(0, 2); }
        if (selected.Count > settings.MaxHistoryMessages || Characters() > settings.MaxInputCharacters)
            throw new ExperimentException("context.history_limit", "The harness transcript projection exceeds its history-message/character limit. The current input alone fits; select recent-history omission or change the applicable host limit. Native model tokens have not been counted yet. No generation request was dispatched.", 409);
        var ids = selected.Select(item => item.Id).Append(turn.Id + ":user").ToArray();
        return new(selected, new(conversation.Id, turn.Id, ids,
            eligible.Where(item => !ids.Contains(item.Id, StringComparer.Ordinal)).Select(item => item.Id).ToArray(),
            Characters(), 0, ExperimentDirectRequest.InputAllowance(settings),
            "not measured during harness projection; full native request counted at dispatch", settings.HistoryPolicy, turn.Continuity));
    }

    private static ExperimentPreparedContext PrepareCompatibility(ExperimentConversation conversation, ExperimentTurn turn,
        IConversationEvidenceSource? evidenceSource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (conversation.Id != turn.ConversationId) throw new ExperimentException("context.scope", "Conversation context does not match this turn.");
        var settings = turn.Settings;
        ExperimentStore.ValidateSettings(settings);
        var eligible = conversation.Turns.Where(t => t.Sequence < turn.Sequence && t.Status == "completed")
            .SelectMany(t => new[] { new ExperimentMessage(t.Id + ":user", "user", t.UserText, t.CreatedAt),
                new ExperimentMessage(t.Id + ":assistant", "assistant", t.AssistantText, t.FinishedAt ?? t.CreatedAt) }).ToArray();
        var selected = eligible.ToList();
        if (settings.ThreadRecallEnabled) return ExperimentRecallAllocator.Prepare(conversation, turn, eligible, evidenceSource, cancellationToken);
        if (settings.Mode == "clean-room") return PrepareDirect(conversation, turn, eligible, selected, cancellationToken);
        var allowance = settings.ContextWindowTokens - settings.MaxOutputTokens - 4096;
        int Characters() => settings.Instructions.Length + turn.UserText.Length + selected.Sum(m => m.Content.Length);
        int Units() => Encoding.UTF8.GetByteCount(settings.Instructions) + Encoding.UTF8.GetByteCount(turn.UserText) +
            selected.Sum(m => Encoding.UTF8.GetByteCount(m.Content) + 64) + 1024;
        bool Over()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return selected.Count > settings.MaxHistoryMessages || Characters() > settings.MaxInputCharacters || Units() > allowance;
        }
        if (settings.HistoryPolicy == "recent")
            while (selected.Count >= 2 && Over()) selected.RemoveRange(0, 2);
        if (Over()) throw new ExperimentException("context.limit",
            "The conversation exceeds its selected history/input allowance. Increase the limits or explicitly select recent-history omission. No model call was made.", 409);
        var selectedIds = selected.Select(m => m.Id).Append(turn.Id + ":user").ToArray();
        var omitted = eligible.Where(m => !selectedIds.Contains(m.Id, StringComparer.Ordinal)).Select(m => m.Id).ToArray();
        return new(selected, new(conversation.Id, turn.Id, selectedIds, omitted, Characters(), Units(), allowance,
            "conservative UTF-8 content bytes + message/envelope overhead; not measured provider tokens", settings.HistoryPolicy, turn.Continuity));
    }

    private static ExperimentPreparedContext PrepareDirect(ExperimentConversation conversation, ExperimentTurn turn,
        ExperimentMessage[] eligible, List<ExperimentMessage> selected, CancellationToken cancellationToken)
    {
        var settings = turn.Settings;
        var allowance = ExperimentDirectRequest.InputAllowance(settings);
        int ContentCharacters() => settings.Instructions.Length + turn.UserText.Length + selected.Sum(message => message.Content.Length);
        bool Over()
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A complete JSON request cannot be shorter than its unescaped content. Avoid
            // materializing large histories already certain to exceed these cheap gates.
            if (selected.Count > settings.MaxHistoryMessages || ContentCharacters() > settings.MaxInputCharacters) return true;
            var request = ExperimentDirectRequest.Build(settings, selected, turn.UserText);
            return request.SerializedCharacters > settings.MaxInputCharacters || request.EstimatedInputUnits > allowance;
        }
        if (settings.HistoryPolicy == "recent")
            while (selected.Count >= 2 && Over()) selected.RemoveRange(0, 2);
        cancellationToken.ThrowIfCancellationRequested();
        var currentOnly = ExperimentDirectRequest.Build(settings, [], turn.UserText);
        var currentDoesNotFit = currentOnly.SerializedCharacters > settings.MaxInputCharacters || currentOnly.EstimatedInputUnits > allowance;
        var advice = currentDoesNotFit
            ? "The current input/settings alone do not fit; shorten that input or adjust the character/window/output limits."
            : settings.HistoryPolicy == "recent" ? "No complete eligible history pair can be omitted further. Adjust the applicable limits."
            : "Select recent-history omission or adjust the applicable limits.";
        var omittedCount = eligible.Length - selected.Count;
        var budget = $"Current-only request: {currentOnly.SerializedCharacters} serialized characters, {currentOnly.EstimatedInputUnits} proxy units. " +
            $"Window {settings.ContextWindowTokens} minus output reserve {settings.MaxOutputTokens} minus safety reserve {ExperimentDirectRequest.InputSafetyReserve} = {allowance}; " +
            $"history omitted {omittedCount}/{eligible.Length} messages under '{settings.HistoryPolicy}'. {advice} " +
            "Proxy units are UTF-8 serialized bytes plus 512, not measured provider tokens. No model call was made.";
        if (selected.Count > settings.MaxHistoryMessages || ContentCharacters() > settings.MaxInputCharacters)
        {
            var failedCheapGate = selected.Count > settings.MaxHistoryMessages ? "history-message limit" : "request character limit";
            throw new ExperimentException("context.limit", $"Failed: {failedCheapGate}. Selected history {selected.Count} messages " +
                $"(limit {settings.MaxHistoryMessages}); unescaped content {ContentCharacters()} characters (request limit {settings.MaxInputCharacters}). " +
                "The full request was not materialized after this certain limit failure. " + budget, 409);
        }
        var envelope = ExperimentDirectRequest.Build(settings, selected, turn.UserText);
        var failed = new List<string>();
        if (envelope.SerializedCharacters > settings.MaxInputCharacters) failed.Add("serialized request character limit");
        if (envelope.EstimatedInputUnits > allowance) failed.Add("request proxy allowance");
        if (failed.Count != 0)
            throw new ExperimentException("context.limit", $"Failed: {string.Join(", ", failed)}. Complete request: " +
                $"{envelope.SerializedCharacters} serialized characters (limit {settings.MaxInputCharacters}), " +
                $"{envelope.EstimatedInputUnits} conservative proxy units (allowance {allowance}). " + budget, 409);
        var selectedIds = selected.Select(message => message.Id).Append(turn.Id + ":user").ToArray();
        var omitted = eligible.Where(message => !selectedIds.Contains(message.Id, StringComparer.Ordinal)).Select(message => message.Id).ToArray();
        return new(selected, new(conversation.Id, turn.Id, selectedIds, omitted, ContentCharacters(), envelope.EstimatedInputUnits,
            allowance, "utf8-wire-byte-proxy-plus-512; complete direct Responses request; not measured provider tokens",
            settings.HistoryPolicy, turn.Continuity));
    }
}

/// <summary>Invocation-local exact-envelope measurement cache; no transcript or provider state survives the turn.</summary>
internal sealed class ExperimentContextMeasurements(IExperimentInputTokenCounter counter)
{
    private readonly Dictionary<string, ExperimentTokenCount> _counts = new(StringComparer.Ordinal);
    internal async Task<ExperimentTokenCount> CountAsync(ExperimentDirectRequest.Envelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = Convert.ToHexStringLower(SHA256.HashData(envelope.Utf8));
        if (_counts.TryGetValue(hash, out var cached)) return cached;
        if (_counts.Count >= 16) throw new ExperimentException("context.measurement_limit", "The bounded input-token measurement allowance was reached. No generation request was dispatched.");
        var count = await counter.CountAsync(envelope.Utf8, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (count.Tokens < 0 || count.RequestHash != hash || string.IsNullOrWhiteSpace(count.Method))
            throw new ExperimentException("context.token_count_invalid", "The input-token measurement did not match the exact prepared request. No generation request was dispatched.");
        _counts.Add(hash, count);
        return count;
    }

    internal static ExperimentException Limit(string code, ExperimentSettings settings, string failed,
        ExperimentDirectRequest.Envelope combined, ExperimentTokenCount? combinedCount,
        ExperimentDirectRequest.Envelope current, ExperimentTokenCount? currentCount, int retainedTurns, int omittedTurns, string advice) =>
        new(code, $"Failed: {failed}. Current-only request: {current.SerializedCharacters} serialized characters, " +
            $"{(currentCount is null ? "model input tokens not measured" : currentCount.Tokens + " input tokens (" + currentCount.Method + ")")}. " +
            $"Combined request: {combined.SerializedCharacters} serialized characters (limit {settings.MaxInputCharacters}), " +
            $"{(combinedCount is null ? "model input tokens not measured" : combinedCount.Tokens + " input tokens (" + combinedCount.Method + ")")}; " +
            $"retained complete turns {retainedTurns}, omitted complete turns {omittedTurns}, history-message limit {settings.MaxHistoryMessages}. " +
            $"Window {settings.ContextWindowTokens} minus output reserve {settings.MaxOutputTokens} minus safety reserve {ExperimentDirectRequest.InputSafetyReserve} " +
            $"= input token allowance {ExperimentDirectRequest.InputAllowance(settings)}. {advice} No generation request was dispatched.", 409);

    internal static ExperimentException CharacterLowerBoundLimit(string code, ExperimentSettings settings,
        ExperimentDirectRequest.Envelope current, int combinedRawCharacters, int historyMessages, int retainedTurns, string advice) =>
        new(code, $"Failed: request character limit (unescaped content already exceeds the serialized character ceiling)" +
            (historyMessages > settings.MaxHistoryMessages ? ", history-message limit. " : ". ") +
            $"Current-only request: {current.SerializedCharacters} serialized characters. Combined unescaped content: {combinedRawCharacters} characters " +
            $"(request character limit {settings.MaxInputCharacters}); retained complete turns {retainedTurns}, history messages {historyMessages} " +
            $"(limit {settings.MaxHistoryMessages}). The full request was not materialized after this certain character failure; model input tokens were not measured. " +
            $"Window {settings.ContextWindowTokens} minus output reserve {settings.MaxOutputTokens} minus safety reserve {ExperimentDirectRequest.InputSafetyReserve} " +
            $"= input token allowance {ExperimentDirectRequest.InputAllowance(settings)}. {advice} No generation request was dispatched.", 409);
}

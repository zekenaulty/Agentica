using System.Text;

namespace Agentica.Lab.Web.Chat;

/// <summary>Only this conversation's complete visible dialogue is eligible. No memory service is bound.</summary>
public static class ExperimentContextCompiler
{
    public static ExperimentPreparedContext Prepare(ExperimentConversation conversation, ExperimentTurn turn)
    {
        if (conversation.Id != turn.ConversationId) throw new ExperimentException("context.scope", "Conversation context does not match this turn.");
        var settings = turn.Settings;
        ExperimentStore.ValidateSettings(settings);
        var eligible = conversation.Turns.Where(t => t.Sequence < turn.Sequence && t.Status == "completed")
            .SelectMany(t => new[] { new ExperimentMessage(t.Id + ":user", "user", t.UserText, t.CreatedAt),
                new ExperimentMessage(t.Id + ":assistant", "assistant", t.AssistantText, t.FinishedAt ?? t.CreatedAt) }).ToArray();
        var selected = eligible.ToList();
        if (settings.Mode == "clean-room") return PrepareDirect(conversation, turn, eligible, selected);
        var allowance = settings.ContextWindowTokens - settings.MaxOutputTokens - 4096;
        int Characters() => settings.Instructions.Length + turn.UserText.Length + selected.Sum(m => m.Content.Length);
        int Units() => Encoding.UTF8.GetByteCount(settings.Instructions) + Encoding.UTF8.GetByteCount(turn.UserText) +
            selected.Sum(m => Encoding.UTF8.GetByteCount(m.Content) + 64) + 1024;
        bool Over() => selected.Count > settings.MaxHistoryMessages || Characters() > settings.MaxInputCharacters || Units() > allowance;
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
        ExperimentMessage[] eligible, List<ExperimentMessage> selected)
    {
        var settings = turn.Settings;
        var allowance = ExperimentDirectRequest.InputAllowance(settings);
        int ContentCharacters() => settings.Instructions.Length + turn.UserText.Length + selected.Sum(message => message.Content.Length);
        bool Over()
        {
            // A complete JSON request cannot be shorter than its unescaped content. Avoid
            // materializing large histories already certain to exceed these cheap gates.
            if (selected.Count > settings.MaxHistoryMessages || ContentCharacters() > settings.MaxInputCharacters) return true;
            var request = ExperimentDirectRequest.Build(settings, selected, turn.UserText);
            return request.SerializedCharacters > settings.MaxInputCharacters || request.EstimatedInputUnits > allowance;
        }
        if (settings.HistoryPolicy == "recent")
            while (selected.Count >= 2 && Over()) selected.RemoveRange(0, 2);
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

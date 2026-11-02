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
}

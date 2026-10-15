using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;

namespace Agentica.Lab.Web.Runtime;

public sealed partial class HostStartStore
{
    private static void ValidateSession(string hostId, string sessionId)
    {
        ProtocolValidation.Identifier(hostId);
        ProtocolValidation.Identifier(sessionId);
    }
    private static void ValidateKey(HostStartKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateSession(key.HostId, key.SessionId);
        ProtocolValidation.Identifier(key.AttemptId);
    }
    private static void ValidateReference(HostStartReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateSession(reference.HostId, reference.SessionId);
        ProtocolValidation.Identifier(reference.AttemptId);
        ProtocolValidation.Identifier(reference.RunId);
        ProtocolValidation.Text(reference.AcceptedRequestHash, 71, "accepted request hash");
    }
    private static void ValidateAdmission(string attemptId, HostRunRequest original, HostRunRequest accepted, string runId)
    {
        ProtocolValidation.Identifier(attemptId);
        ProtocolValidation.Identifier(runId);
        ProtocolValidation.Validate(original);
        ProtocolValidation.Validate(accepted);
        if (ProtocolValidation.Digest(original with { Provider = null, Limits = null }) !=
            ProtocolValidation.Digest(accepted with { Provider = null, Limits = null }))
            Fail("start.accepted_changed", "Accepted identity, objective, observation and capabilities must match the original start.");
        if (accepted.Limits != original.Limits && !(original.Limits is null && accepted.Limits == new HostRunLimits()))
            Fail("start.accepted_changed", "Accepted limits may only materialize the original default limits.");
        ValidateProvider(original.Provider, accepted.Provider);
    }
    private static void ValidateProvider(ProviderSettings? original, ProviderSettings? accepted)
    {
        if (original == accepted) return;
        var requested = original ?? new ProviderSettings();
        var provider = requested.Provider?.Trim().ToLowerInvariant() switch
        {
            "google" => "gemini",
            "claude" => "anthropic",
            "xai" => "grok",
            { } name => name,
            _ => "gemini"
        };
        // The registry owns configuration lookup. Only unspecified model/thinking values may be filled.
        if (accepted is null || accepted.Provider != provider ||
            (requested.Model is not null && accepted.Model != requested.Model) ||
            ((requested.ThinkingEffort is not null || requested.ThinkingEffortResolved) && accepted.ThinkingEffort != requested.ThinkingEffort) ||
            (requested.ThinkingEffortResolved && !accepted.ThinkingEffortResolved) ||
            accepted.MaxOutputTokens != requested.MaxOutputTokens || accepted.ContextWindowTokens != requested.ContextWindowTokens ||
            accepted.IncludeThoughtSummaries != requested.IncludeThoughtSummaries || accepted.GeminiApi != requested.GeminiApi)
            Fail("start.accepted_changed", "Accepted provider settings may normalize the provider and freeze unspecified model/thinking defaults only.");
    }
    private static void ValidateSettlement(string reason, HostRunUsage? usage, bool usageUnknown)
    {
        if (reason is not ("stopped" or "interrupted")) Fail("start.reason", "Stop settlement reason must be stopped or interrupted.");
        if (reason == "interrupted" ? usage is not null || !usageUnknown : usage is null || usageUnknown)
            Fail("start.usage", "Interrupted starts require unknown/null usage; stopped starts require known original-run usage.");
        if (usage is not null && (usage.Steps < 0 || usage.Refinements < 0 || usage.Continuations < 0 || usage.ProviderCalls < 0))
            Fail("start.usage", "Stop usage must be nonnegative; unknown spend cannot be represented as zero.");
    }
    private static void ValidateEntry(HostStartEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateAdmission(entry.AttemptId, entry.OriginalRequest, entry.AcceptedRequest, entry.RunId);
        if (entry.Schema != 1 || entry.AdmittedAt == default ||
            entry.OriginalRequestHash != ProtocolValidation.Digest(entry.OriginalRequest) ||
            entry.AcceptedRequestHash != ProtocolValidation.Digest(entry.AcceptedRequest) ||
            entry.ManifestHash != ProtocolValidation.Digest(entry.AcceptedRequest.Capabilities))
            throw new InvalidDataException("Start entry schema, timestamp or request/manifest hashes are invalid.");
        if (entry.Terminal is { } terminal)
        {
            if (!entry.StopRequested || terminal.SettledAt == default || terminal.SettledAt < entry.AdmittedAt)
                throw new InvalidDataException("Start settlement requires a retained Stop and an ordered timestamp.");
            ValidateSettlement(terminal.Reason, terminal.Usage, terminal.UsageUnknown);
        }
    }
}

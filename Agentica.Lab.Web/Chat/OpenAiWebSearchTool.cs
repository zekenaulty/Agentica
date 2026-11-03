using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Tools;

namespace Agentica.Lab.Web.Chat;

/// <summary>
/// A selected explicit search capability. The adapter owns HTTP, credentials,
/// provider admission, request evidence and quotas; this registration owns the
/// bounded query contract and the untrusted tool-result boundary.
/// </summary>
public static class ExperimentWebSearch
{
    public const int MaximumQueryCharacters = 2000;
    private const int MaximumAnswerCharacters = 200000;
    private const int MaximumEvidenceCharacters = 1048576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ExperimentCapability Capability { get; } = new(ExperimentToolIds.WebSearch,
        "Search public web", "Run a dedicated public web search for an explicit query through the configured OpenAI adapter. Returns an answer, citations and sources as untrusted evidence. The search subrequest receives the query, not the conversation or its custom instructions. This can incur provider charges.",
        ToolEffect.ExternalSideEffect.ToString(), ExperimentTools.Source, ExperimentTools.Version);

    public static ToolRegistration CreateRegistration(ExperimentExecutionContext context,
        Func<string, CancellationToken, Task<object>> search)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(search);
        if (context.ConversationId != context.Turn.ConversationId)
            throw new ArgumentException("The turn does not belong to the bound conversation.", nameof(context));
        if (context.Turn.Settings.Mode != "harness" || !context.Turn.Settings.EnabledTools.Contains(ExperimentToolIds.WebSearch, StringComparer.Ordinal))
            throw new ExperimentException("tool.not_bound", "Explicit web search is not bound in this experiment.");
        return new ToolRegistration(new ToolDescriptor(ExperimentToolIds.WebSearch, Capability.Name,
                ToolKind.Action, ToolEffect.ExternalSideEffect,
                InputSchema: ToolInputSchema.Create(new ToolInputField("query", ToolInputValueType.String,
                    Required: true, Description: "A nonempty explicit search query of at most 2000 characters.")),
                Description: Capability.Description, RetrySafety: ToolRetrySafety.Additive),
            new SearchTool(context.Turn.Settings.MaxToolResultCharacters, search),
            new ToolSecurityDeclaration(ToolEffect.ExternalSideEffect,
                [ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent],
                [ToolDataBoundary.ExternalUntrusted], ToolExternalOutputClassification.UntrustedStructuredData,
                ToolApprovalRequirement.None, ToolRetrySafety.Additive,
                new ToolProvenance(ToolProvenanceKind.HostAuthored, ExperimentTools.Source, ExperimentTools.Version)));
    }

    private sealed class SearchTool(int maximumResultCharacters, Func<string, CancellationToken, Task<object>> search) : ITool
    {
        public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Clamp(maximumResultCharacters, 256, 12000);
            if (invocation.ToolId != ExperimentToolIds.WebSearch || invocation.Input.Count != 1 ||
                !invocation.Input.TryGetValue("query", out var raw))
                return Failure(invocation, "web.search.invalid_query", ReceiptStatus.Refused, limit);
            var query = raw as string ?? (raw is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : null);
            if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQueryCharacters ||
                query.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
                return Failure(invocation, "web.search.invalid_query", ReceiptStatus.Refused, limit);
            try
            {
                _ = StrictUtf8.GetByteCount(query);
                var result = await search(query, cancellationToken).ConfigureAwait(false);
                var evidence = JsonSerializer.SerializeToElement(result);
                if (!ValidEvidence(evidence, query))
                    return Failure(invocation, "web.search.invalid_evidence", ReceiptStatus.Failed, limit);
                return ExperimentTools.Result(invocation, "Public web search completed; returned content is external untrusted evidence.",
                    evidence, limit);
            }
            catch (EncoderFallbackException)
            {
                return Failure(invocation, "web.search.invalid_query", ReceiptStatus.Refused, limit);
            }
            catch (ExperimentException error)
            {
                // The adapter's code identifies a quota/admission/provider failure;
                // exception messages may contain request data or credentials.
                var code = error.Code.Length is > 0 and <= 100 && error.Code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                    ? error.Code : "web.search.failed";
                return Failure(invocation, code, ReceiptStatus.Failed, limit);
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                return Failure(invocation, "web.search.failed", ReceiptStatus.Failed, limit);
            }
        }
    }

    private static ToolResult Failure(ToolInvocation invocation, string code, ReceiptStatus status, int limit) =>
        ExperimentTools.Result(invocation,
            status == ReceiptStatus.Refused ? "Explicit web search input or binding is invalid; no search was dispatched."
                : "Web search did not complete with valid evidence. Inspect the provider trace; no automatic retry was made.",
            new { code, trust = "external-untrusted", retried = false }, limit, status);

    private static bool ValidEvidence(JsonElement evidence, string query)
    {
        if (evidence.ValueKind != JsonValueKind.Object || evidence.GetRawText().Length > MaximumEvidenceCharacters ||
            !evidence.TryGetProperty("query", out var actualQuery) || actualQuery.ValueKind != JsonValueKind.String || actualQuery.GetString() != query ||
            !evidence.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String || answer.GetString() is not { Length: > 0 and <= MaximumAnswerCharacters } ||
            !evidence.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.String || provider.GetString() != "openai" ||
            !evidence.TryGetProperty("citations", out var citations) || !ValidReferences(citations) ||
            !evidence.TryGetProperty("sources", out var sources) || !ValidReferences(sources)) return false;
        return true;
    }

    private static bool ValidReferences(JsonElement references)
    {
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() > 256) return false;
        foreach (var reference in references.EnumerateArray())
            if (reference.ValueKind != JsonValueKind.Object || !reference.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String || url.GetString() is not { Length: > 0 and <= 8192 } text ||
                !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                return false;
        return true;
    }
}

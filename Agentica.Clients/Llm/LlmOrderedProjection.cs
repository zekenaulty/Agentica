using System.Text.Json;
using System.Xml.Linq;

namespace Agentica.Clients.Llm;

internal static class LlmOrderedProjection
{
    internal static LlmContinuationConfigurationBinding? CaptureConfiguration(LlmRequest request,
        Func<LlmConfigurationReport> describe)
    {
        if (request.MessageMode != LlmMessageMode.Ordered) return null;
        var prepared = request.PreparedDispatch?.Descriptor;
        var report = prepared is null ? describe() : null;
        var binding = prepared is not null ? new LlmContinuationConfigurationBinding(prepared.ApiMode, prepared.ConfigurationIdentity)
            : new LlmContinuationConfigurationBinding(report!.ApiMode, report.ConfigurationIdentity);
        if (request.NativeContinuation?.SnapshotOwned().Binding is { } stored && stored != binding)
            throw LlmMessageMapping.Failure(request.NativeContinuation.ProviderName, "continuation_configuration_mismatch");
        return binding;
    }

    internal static void BindOutcome(LlmNativeContinuation? continuation, LlmContinuationConfigurationBinding? configuration)
    {
        if (continuation is not null && configuration is not null && !continuation.TryBindConfiguration(configuration))
            throw LlmMessageMapping.Failure(continuation.ProviderName, "continuation_configuration_mismatch");
    }

    // A known instruction-channel limitation gets an explicit interpreted projection.
    // Source role boundaries survive escaping; no second native priority is claimed.
    internal static string InstructionBlock(LlmMessage message) =>
        new XElement(message.Role == LlmMessageRole.Developer ? "developer_instruction" : "system_instruction",
            message.Content).ToString(SaveOptions.DisableFormatting);

    internal static string InstructionPrefix(IEnumerable<LlmMessage> messages)
    {
        var blocks = messages.ToArray();
        return blocks.Length == 0 ? "" :
            "<instruction_interpretation>System blocks retain the caller's governing interpretation. " +
            "Developer blocks are subordinate application instructions co-located in the system channel; " +
            "these labels do not create a second native priority or grant effects.</instruction_interpretation>\n" +
            string.Join("\n", blocks.Select(InstructionBlock));
    }

    internal static (string Instructions, LlmMessage[] Dialogue) Split(LlmRequest request, string provider)
    {
        var prefix = request.Messages.TakeWhile(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer).ToArray();
        if (request.NativeContinuation is not null && prefix.Length != 0)
            throw LlmMessageMapping.Failure(provider, "ordered_append_instruction_placement_unsupported");
        var dialogue = request.Messages.Skip(prefix.Length).ToArray();
        if (dialogue.Any(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer))
            throw LlmMessageMapping.Failure(provider, "ordered_instruction_placement_unsupported");
        if (dialogue.Length == 0) throw LlmMessageMapping.Failure(provider, "ordered_dialogue_required");
        return (request.NativeContinuation?.SystemInstruction ?? InstructionPrefix(prefix), dialogue);
    }

    internal static List<object> History(LlmRequest request, string provider, string model)
    {
        var history = new List<object>();
        if (request.NativeContinuation is not { } native) return history;
        if (native.ProviderName != provider || native.ModelId != model || native.MessageMode != LlmMessageMode.Ordered)
            throw LlmMessageMapping.Failure(provider, "continuation_binding_mismatch");
        if (request.Messages.Any(message => message.Role == LlmMessageRole.Assistant))
            throw LlmMessageMapping.Failure(provider, "ordered_append_contains_assistant_history");
        using var document = JsonDocument.Parse(native.HistoryStepsJson);
        history.AddRange(document.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
        return history;
    }

    internal static (string Instructions, List<object> Dialogue) Dialogue(LlmRequest request, string provider,
        string model, bool interactions = false, bool generateContent = false)
    {
        var (instructions, messages) = Split(request, provider);
        var history = History(request, provider, model);
        foreach (var message in messages)
        {
            if (message.Role == LlmMessageRole.Tool) throw LlmMessageMapping.Failure(provider, "native_tool_linkage_required");
            var assistant = message.Role == LlmMessageRole.Assistant;
            if (interactions)
                history.Add(new { type = assistant ? "model_output" : "user_input", content = new[] { new { type = "text", text = message.Content } } });
            else if (generateContent)
                history.Add(new { role = assistant ? "model" : "user", parts = new[] { new { text = message.Content } } });
            else history.Add(new { role = assistant ? "assistant" : "user", content = message.Content });
        }
        Bound(history, provider);
        return (instructions, history);
    }

    internal static List<object> Ollama(LlmRequest request, string model)
    {
        var history = History(request, "ollama", model);
        foreach (var message in request.Messages)
        {
            if (message.Role == LlmMessageRole.Tool) throw LlmMessageMapping.Failure("ollama", "native_tool_linkage_required");
            history.Add(new
            {
                role = message.Role == LlmMessageRole.Developer ? "system" : message.Role.ToString().ToLowerInvariant(),
                content = message.Role == LlmMessageRole.Developer ? InstructionPrefix([message]) : message.Content
            });
        }
        Bound(history, "ollama");
        return history;
    }

    internal static void Bound(List<object> history, string provider)
    {
        if (history.Count > LlmNativeContinuationCodec.MaximumCollectionItems ||
            JsonSerializer.Serialize(history).Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw LlmMessageMapping.Failure(provider, "continuation_too_large");
    }
}

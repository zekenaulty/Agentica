using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Clients.Llm;

/// <summary>Optional, network-free reports derived from the adapters' actual request builders.
/// Pass the same options snapshot used to construct the client. Reports never resolve host routes.</summary>
public static class LlmConfigurationReports
{
    public static LlmConfigurationReport Describe(LlmRequest request,
        OpenAiResponsesClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Describe(request, "openai", "responses", options.DefaultModelId,
            options.Endpoint ?? OpenAiResponsesClientOptions.DefaultEndpoint,
            model => new OpenAiResponsesLlmClient(options).BuildRequestBody(request, model), context);
    }

    public static LlmConfigurationReport Describe(LlmRequest request,
        XaiResponsesClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Describe(request, "xai", "responses", options.DefaultModelId,
            options.Endpoint ?? XaiResponsesClientOptions.DefaultEndpoint,
            model => new XaiResponsesLlmClient(options).BuildRequestBody(request, model), context);
    }

    public static LlmConfigurationReport Describe(LlmRequest request,
        AnthropicMessagesClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Describe(request, "anthropic", "messages", options.DefaultModelId,
            options.Endpoint ?? AnthropicMessagesClientOptions.DefaultEndpoint,
            model => AnthropicMessagesLlmClient.BuildRequestBody(request, model), context);
    }

    public static LlmConfigurationReport Describe(LlmRequest request,
        OllamaClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Describe(request, "ollama", "chat", options.DefaultModelId,
            options.Endpoint ?? OllamaClientOptions.DefaultEndpoint,
            model =>
            {
                _ = new OllamaLlmClient(options); // Reuse the actual endpoint policy too.
                return OllamaLlmClient.BuildRequestBody(request, model);
            }, context);
    }

    public static LlmConfigurationReport Describe(LlmRequest request,
        GeminiInteractionsClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Describe(request, GeminiLlmClient.ProviderName, "interactions", options.DefaultModelId,
            options.Endpoint ?? GeminiInteractionsClientOptions.DefaultEndpoint,
            model => GeminiInteractionsLlmClient.BuildRequestBody(request, model), context);
    }

    public static LlmConfigurationReport Describe(LlmRequest request,
        GeminiGenerateContentClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Describe(request, GeminiGenerateContentLlmClient.ProviderName, "generate_content",
            options.DefaultModelId, options.Endpoint ?? new Uri("https://generativelanguage.googleapis.com/v1beta/models/"),
            model => GeminiGenerateContentLlmClient.BuildRequest(request, model, out _), context,
            resolveEndpoint: model => options.Endpoint ?? GeminiGenerateContentLlmClient.Endpoint(model));
    }

    public static LlmConfigurationReport Describe(LlmRequest request,
        GeminiClientOptions options, LlmConfigurationReportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // These options do not pin the SDK's mutable static/environment base URL resolution.
        // Never label this partial configuration as a resolved transport route.
        var endpoint = new Uri(options.UseVertexAi
            ? "https://aiplatform.googleapis.com/"
            : "https://generativelanguage.googleapis.com/");
        return Describe(request, GeminiLlmClient.ProviderName,
            options.UseVertexAi ? "vertex_sdk_generate_content_route_unresolved" : "sdk_generate_content_route_unresolved",
            options.DefaultModelId, endpoint, _ => LegacyConfig(request), context,
            options.UseVertexAi ? new { options.Project, options.Location } : null);
    }

    private static object LegacyConfig(LlmRequest request)
    {
        LlmMessageMapping.Validate(request, GeminiLlmClient.ProviderName, supportsOrdered: true);
        if (request.NativeContinuation is not null || request.MessageMode != LlmMessageMode.Ordered && request.Messages.Any(message =>
            message.Role is LlmMessageRole.Assistant or LlmMessageRole.Tool))
            throw new LlmClientException(GeminiLlmClient.ProviderName, "Native history is unsupported by this adapter.",
                errorKind: LlmClientErrorKind.BadRequest, errorClass: "native_history_required");
        var config = GeminiLlmClient.CreateConfig(request);
        if (request.MessageMode == LlmMessageMode.Ordered) _ = GeminiLlmClient.BuildOrderedContents(request);
        return new
        {
            generationConfig = new
            {
                temperature = config.Temperature,
                maxOutputTokens = config.MaxOutputTokens,
                responseMimeType = config.ResponseMimeType,
                responseJsonSchema = config.ResponseJsonSchema,
                thinkingConfig = config.ThinkingConfig is null ? null : new
                {
                    thinkingBudget = config.ThinkingConfig.ThinkingBudget,
                    includeThoughts = config.ThinkingConfig.IncludeThoughts,
                    thinkingLevel = config.ThinkingConfig.ThinkingLevel?.ToString().ToLowerInvariant()
                }
            }
        };
    }

    private static LlmConfigurationReport Describe(LlmRequest request, string provider, string apiMode,
        string? defaultModel, Uri endpoint, Func<string, object> build,
        LlmConfigurationReportContext? context, object? sdkRoute = null,
        Func<string, Uri>? resolveEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? defaultModel ?? string.Empty : request.ModelId;
        var issues = ImmutableArray.CreateBuilder<LlmConfigurationIssue>();
        JsonElement wire = default;
        try
        {
            if (request.OpenAiHistoryMode != OpenAiHistoryMode.NativeContinuation && provider != "openai")
                throw new LlmClientException(provider, "OpenAI transcript mode is unsupported by this route.",
                    errorKind: LlmClientErrorKind.BadRequest, errorClass: "unsupported_history_mode");
            if (request.WebSearch is not null && provider != "openai")
                throw new LlmClientException(provider, "Provider-native web search is unsupported by this route.",
                    errorKind: LlmClientErrorKind.BadRequest, errorClass: "unsupported_web_search");
            if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
                issues.Add(new("invalid_endpoint", "The configured endpoint must be an absolute HTTP(S) URL."));
            else if (string.IsNullOrWhiteSpace(model))
                issues.Add(new("missing_model", "No model was requested or configured; no substitute was selected."));
            else
            {
                wire = JsonSerializer.SerializeToElement(build(model));
            }
        }
        catch (LlmClientException exception)
        {
            issues.Add(new(exception.ErrorClass ?? "adapter_request_rejected",
                "The adapter rejected this exact request. Its model and controls were not downgraded."));
        }
        catch (JsonException)
        {
            issues.Add(new("invalid_request_json", "The adapter could not serialize this request."));
        }
        catch (ArgumentException)
        {
            issues.Add(new("invalid_request_value", "The adapter could not serialize this request value."));
        }
        var routeResolved = !string.IsNullOrWhiteSpace(model) && endpoint.IsAbsoluteUri &&
            endpoint.Scheme is "http" or "https";
        if (resolveEndpoint is not null && !string.IsNullOrWhiteSpace(model))
        {
            try
            {
                endpoint = resolveEndpoint(model);
                routeResolved = endpoint.IsAbsoluteUri && endpoint.Scheme is "http" or "https";
            }
            catch (LlmClientException exception)
            {
                routeResolved = false;
                issues.Add(new(exception.ErrorClass ?? "adapter_route_rejected",
                    "The adapter rejected the configured model/endpoint route."));
            }
        }
        var legacy = apiMode.Contains("sdk_", StringComparison.Ordinal);
        var geminiContent = apiMode.Contains("generate_content", StringComparison.Ordinal);
        var controls = Controls(request, wire, provider, apiMode, issues);
        var endpointIdentity = endpoint.IsAbsoluteUri
            ? endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "invalid-relative-endpoint";
        // Only applied configuration enters this identity; no user messages, system instructions,
        // native history, request metadata, keys, endpoint query/userinfo or host bundle IDs.
        var providerConfiguration = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (wire.ValueKind == JsonValueKind.Object)
            foreach (var property in wire.EnumerateObject())
                if (property.Name is not ("input" or "contents" or "messages" or "instructions" or
                    "system" or "system_instruction" or "systemInstruction" or "metadata"))
                    providerConfiguration.Add(property.Name, property.Value);
        var identitySource = new
        {
            contract = "agentica.client.configuration.v1",
            provider,
            apiMode,
            model,
            endpoint = endpointIdentity,
            sdkRoute,
            providerConfiguration,
            clientHistoryMode = request.OpenAiHistoryMode,
            clientCitationIndexUnit = request.WebSearch?.CitationIndexUnit,
            rejectedControls = issues.Count == 0 ? null
                : controls.Select(control => new { control.Name, control.RequestedValue }).ToArray(),
            issues = issues.Select(issue => issue.Code)
        };
        var identity = "sha256:" + Hash(request.MessageMode == LlmMessageMode.Legacy
            ? JsonSerializer.Serialize(identitySource)
            : JsonSerializer.Serialize(new { configuration = identitySource, messageMode = request.MessageMode }));
        if (request.InstructionRolePolicy != LlmInstructionRolePolicy.AllowInterpretedFallback)
            identity = "sha256:" + Hash(JsonSerializer.Serialize(new { identity, request.InstructionRolePolicy }));
        if (legacy) identity = "unresolved:" + identity;
        var asOf = context?.AsOf ?? DateTimeOffset.UtcNow;
        var version = typeof(LlmConfigurationReports).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var capabilities = ImmutableArray.Create(
            Capability("streaming", !legacy, "Configured transport behavior; no live readiness claim."),
            Capability("named_reasoning_effort", true, "Provider-specific named values; model/account support remains unknown."),
            Capability("numeric_thinking_budget", provider == "anthropic" || geminiContent,
                "Positive token budgets differ from named effort and dynamic/off sentinels."),
            Capability("native_structured_output", provider != "anthropic",
                provider == "anthropic" ? "This adapter validates schema syntax but emits unconstrained text; host validation is required."
                    : "Reports the serialized format, not universal JSON Schema or model support."),
            Capability("native_continuation_replay", !legacy, "Replay is bound to provider, API mode, exact model and instructions."),
            Capability("durable_continuation_exchange", !legacy,
                "Explicit versioned sensitive codec; exact native bindings apply. Hosts own authenticated storage and retention."),
            Capability("native_search", provider == "openai", "Optional OpenAI native web search; model/account readiness and host acceptance remain separate."),
            Capability("native_input_count", provider == "openai", "OpenAI input counting is a separate admitted operation; generation does not count automatically."),
            Capability("prepared_request_admission", !legacy, "Optional exact preparation requires host admission before inference; ordinary client entrypoints remain compatible."),
            Capability("ordered_messages", true, "Native dialogue and explicit instruction translation; exact-route limitations remain reported."),
            Capability("assistant_phase", provider == "openai", "Only supplied assistant phase is serialized; model support requires separate qualification."),
            Capability("explicit_prompt_cache_boundary", provider == "openai", "Serialized content boundary and policy; no cache-hit or account/model support claim."),
            Capability("linked_function_history_input", provider is "openai" or "xai",
                "Supplied observed call/result linkage in Ordered input only; no tool-definition, execution or orchestration contract."));
        return new LlmConfigurationReport(provider, apiMode, request.ModelId, model, identity,
            provider == "openai" ? "native_count_available_separately; unmeasured" :
                "provider_usage_after_response; pre_dispatch_count_not_provided", controls, capabilities,
            issues.ToImmutable(), new(LlmCapabilitySupport.Supported, "Agentica.Clients/" + version,
                asOf, "Actual installed serializer inspected without HTTP; as-of is report creation, not model qualification."),
            legacy
                ? new(LlmCapabilitySupport.Unknown, "sdk_ambient_route_unresolved", asOf,
                    "The legacy SDK also resolves static/environment mode, base URL and project/location defaults; no resolved route is captured here.")
                : new(routeResolved ? LlmCapabilitySupport.Supported : LlmCapabilitySupport.Unsupported,
                    "adapter_options_and_endpoint_builder", asOf,
                    "Endpoint configuration is derived without a network readiness claim; rejected requests remain rejected."),
            Observation(context?.TechnicalReadiness, identity, legacy),
            Observation(context?.ModelAccountQualification, identity, legacy),
            context?.HostRouteReference, context?.HostExecutionBundleReference);
    }

    private static ImmutableArray<LlmControlReport> Controls(LlmRequest request, JsonElement wire,
        string provider, string apiMode, ImmutableArray<LlmConfigurationIssue>.Builder issues)
    {
        var thinking = request.GenerationOptions?.Thinking;
        var content = apiMode.Contains("generate_content", StringComparison.Ordinal);
        var effortPath = provider switch
        {
            "openai" or "xai" => "reasoning.effort",
            "anthropic" => "output_config.effort",
            "ollama" => "think",
            _ when content => "generationConfig.thinkingConfig.thinkingLevel",
            _ => "generation_config.thinking_level"
        };
        var effort = Read(wire, effortPath);
        if (effort?.ValueKind != JsonValueKind.String) effort = null;
        var budgetPath = provider == "anthropic" ? "thinking.budget_tokens"
            : content ? "generationConfig.thinkingConfig.thinkingBudget" : "__no_numeric_budget";
        var maxPath = provider switch
        {
            "anthropic" => "max_tokens",
            "ollama" => "options.num_predict",
            _ when content => "generationConfig.maxOutputTokens",
            _ => "generation_config.max_output_tokens"
        };
        if (provider is "openai" or "xai") maxPath = "max_output_tokens";
        var temperaturePath = provider == "ollama" ? "options.temperature"
            : content ? "generationConfig.temperature" : "__unsupported_temperature";
        var thoughtPath = provider switch
        {
            "openai" or "xai" => "reasoning.summary",
            "anthropic" => "thinking.display",
            "ollama" => "__no_separate_thought_output_switch",
            _ when content => "generationConfig.thinkingConfig.includeThoughts",
            _ => "generation_config.thinking_summaries"
        };
        var formatPath = provider switch
        {
            "openai" or "xai" => "text.format.type",
            "ollama" => "format",
            "anthropic" => "__unconstrained_text",
            _ when content => "generationConfig.responseMimeType",
            _ => "response_format.mime_type"
        };
        var format = Read(wire, formatPath);
        var formatValue = provider == "ollama" && !string.IsNullOrWhiteSpace(request.StructuredOutput?.JsonSchema)
            && format is not null ? "json_schema"
            : format?.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? "json_schema" : Value(format);
        var structuredRequested = request.StructuredOutput is null ? null
            : string.IsNullOrWhiteSpace(request.StructuredOutput.JsonSchema)
                ? request.StructuredOutput.ResponseMimeType : "json_schema";
        var result = ImmutableArray.CreateBuilder<LlmControlReport>();
        Add("reasoning_effort", thinking?.Effort?.ToString().ToLowerInvariant(), Value(effort),
            "Exact named provider control; no numeric ceiling or model downgrade is inferred.");
        Add("thinking_budget_tokens", thinking?.ThinkingBudgetTokens?.ToString(CultureInfo.InvariantCulture),
            Value(Read(wire, budgetPath)), "Dynamic (-1) may leave provider defaults or select adaptive/boolean thinking; it is not a numeric ceiling.");
        Add("thinking_mode", null, Value(Read(wire, provider == "ollama" ? "think" : "thinking.type")),
            "Adaptive, manual and boolean thinking retain provider-specific meanings.");
        Add("thought_output", thinking?.IncludeThoughts.ToString().ToLowerInvariant(), Value(Read(wire, thoughtPath)),
            provider == "ollama" ? "Ollama's think control selects native thinking; it does not provide a separate public reasoning-summary switch."
                : "Public summaries/display controls are separate from opaque native reasoning replay.");
        Add("max_output_tokens", request.GenerationOptions?.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture),
            Value(Read(wire, maxPath)), "Omitted limits remain provider defaults except Anthropic's explicit adapter default of 4096.");
        Add("temperature", request.GenerationOptions?.Temperature?.ToString("R", CultureInfo.InvariantCulture),
            Value(Read(wire, temperaturePath)), "Only values actually emitted by this adapter are reported as applied.");
        Add("structured_output", structuredRequested, formatValue,
            provider == "anthropic" ? "No native format/schema constraint is emitted; output remains text and host validation is required."
                : "The provider's schema subset and model support require separate qualification.");
        Add("schema_enforcement", request.StructuredOutput?.SchemaEnforcement.ToString(),
            Value(Read(wire, "text.format.strict")), provider == "xai"
                ? "The strict compatibility flag is serialized; equal OpenAI enforcement guarantees are not claimed."
                : "Only Responses schema requests emit a strict flag; other adapters retain provider-defined behavior.");
        Add("history_mode", provider == "openai" ? request.OpenAiHistoryMode.ToString() : null,
            provider == "openai" && wire.ValueKind == JsonValueKind.Object ? request.OpenAiHistoryMode.ToString() : null,
            "TranscriptOnly preserves visible author roles and returns no native continuation; native mode retains its existing guards.");
        Add("message_mode", request.MessageMode.ToString(),
            wire.ValueKind == JsonValueKind.Object ? request.MessageMode.ToString() : null,
            "Ordered maps native text items. Native carrier input must explicitly append only new items.");
        Add("instruction_role_mapping", request.MessageMode == LlmMessageMode.Ordered ? request.InstructionRolePolicy.ToString() : null,
            request.MessageMode == LlmMessageMode.Ordered && wire.ValueKind == JsonValueKind.Object
                ? provider == "openai" ? "native" : "labelled_system_fallback" : null,
            "Developer fallback is qualified co-location, not a second provider-enforced priority level.");
        Add("prompt_cache_mode", request.PromptCache?.Mode.ToString().ToLowerInvariant(),
            Value(Read(wire, "prompt_cache_options.mode")), "Supported wire policy; actual cache use remains provider evidence.");
        Add("web_search_mode", request.WebSearch?.Mode.ToString().ToLowerInvariant(), Value(Read(wire, "tool_choice")),
            "Null leaves search disabled; auto and required retain their provider meanings.");
        Add("max_web_search_calls", request.WebSearch?.MaxToolCalls.ToString(CultureInfo.InvariantCulture), Value(Read(wire, "max_tool_calls")),
            "Requested provider ceiling and observed-call validation; cumulative admission and billing remain host-owned.");
        return result.ToImmutable();

        void Add(string name, string? requested, string? applied, string explanation)
        {
            var rejected = issues.Any(issue => RejectedControl(issue.Code, name));
            var unresolved = wire.ValueKind == JsonValueKind.Undefined && issues.Count > 0 && !rejected;
            var disposition = rejected ? LlmControlDisposition.Unsupported
                : unresolved ? LlmControlDisposition.Unresolved
                : applied is not null ? requested is null ? LlmControlDisposition.AdapterDefault : LlmControlDisposition.Applied
                : requested is null || name is "reasoning_effort" or "thinking_budget_tokens" or "max_output_tokens"
                    ? LlmControlDisposition.ProviderDefault : LlmControlDisposition.NotApplied;
            var support = rejected ? LlmCapabilitySupport.Unsupported
                : unresolved ? LlmCapabilitySupport.Unknown
                : KnownControlSupport(name, provider, content, thinking?.ThinkingBudgetTokens);
            result.Add(new(name, requested, applied, disposition, support, explanation));
        }
    }

    private static LlmCapabilitySupport KnownControlSupport(string control, string provider,
        bool geminiContent, int? requestedBudget)
    {
        var supported = control switch
        {
            "history_mode" or "web_search_mode" or "max_web_search_calls" or "prompt_cache_mode" => provider == "openai",
            "message_mode" or "instruction_role_mapping" => true,
            "temperature" => provider == "ollama" || geminiContent,
            "thought_output" => provider != "ollama",
            "structured_output" => provider != "anthropic",
            "schema_enforcement" => provider is "openai" or "xai",
            "thinking_budget_tokens" => provider == "anthropic" || geminiContent ||
                requestedBudget == LlmThinkingOptions.DynamicBudget || provider == "ollama" && requestedBudget == 0,
            "thinking_mode" => provider is "anthropic" or "ollama",
            "reasoning_effort" or "max_output_tokens" => true,
            _ => false
        };
        return supported ? LlmCapabilitySupport.Supported : LlmCapabilitySupport.Unsupported;
    }

    private static bool RejectedControl(string code, string control) => code switch
    {
        "unsupported_message_mode" or "ordered_message_mode_required" or "ordered_history_mode_conflict" => control == "message_mode",
        "native_developer_role_required" or "invalid_instruction_role_policy" => control == "instruction_role_mapping",
        "unsupported_message_metadata" => control == "prompt_cache_mode",
        "invalid_prompt_cache_options" or "prompt_cache_breakpoint_limit" => control == "prompt_cache_mode",
        "unsupported_history_mode" or "transcript_continuation_conflict" => control == "history_mode",
        "unsupported_web_search" => control is "web_search_mode" or "max_web_search_calls",
        "unsupported_reasoning_effort" or "invalid_reasoning_effort" => control == "reasoning_effort",
        "unsupported_thinking_budget" or "invalid_thinking_budget" => control == "thinking_budget_tokens",
        "conflicting_thinking_controls" => control is "reasoning_effort" or "thinking_budget_tokens",
        "disabled_thinking_summary" => control == "thought_output",
        "unsupported_temperature" or "invalid_temperature" => control == "temperature",
        "invalid_max_output_tokens" => control == "max_output_tokens",
        "unsupported_response_format" or "invalid_json_schema" or "unsupported_json_schema" => control == "structured_output",
        "unsupported_schema_enforcement" => control == "schema_enforcement",
        _ => false
    };

    private static LlmConfigurationEvidence Observation(LlmConfigurationObservation? observation, string identity, bool unresolvedSdkRoute)
    {
        if (unresolvedSdkRoute)
            return new(LlmCapabilitySupport.Unknown, "sdk_route_unresolved", null,
                "Readiness observations cannot transfer through an unresolved SDK transport route.");
        if (observation is null)
            return new(LlmCapabilitySupport.Unknown, "not_observed", null, "No matching host observation was supplied; no network probe was performed.");
        if (!string.Equals(identity, observation.ConfigurationIdentity, StringComparison.Ordinal))
            return new(LlmCapabilitySupport.Unknown, "observation_binding_mismatch", observation.AsOf,
                "The supplied observation belongs to a different effective configuration.");
        return new(observation.State, observation.Source, observation.AsOf, observation.Explanation);
    }

    private static LlmCapabilityReport Capability(string name, bool supported, string explanation) =>
        new(name, supported ? LlmCapabilitySupport.Supported : LlmCapabilitySupport.Unsupported, explanation);

    private static JsonElement? Read(JsonElement root, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(part, out root)) return null;
        }
        return root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : root;
    }

    private static string? Value(JsonElement? value) => value is null ? null
        : value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.GetRawText();

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

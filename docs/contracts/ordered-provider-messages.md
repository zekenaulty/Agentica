# Ordered provider messages

`LlmRequest.MessageMode = LlmMessageMode.Ordered` opts into explicit message
boundaries. Existing constructors and Legacy/TranscriptOnly behavior remain.
The native OpenAI slice is delivered first; the mapping table below identifies
the supported translation and limitations on each existing route for
[issue #65](https://github.com/zekenaulty/Agentica/issues/65).

## Fresh composition

`Messages` is the complete selected frame, in caller order. Each `LlmMessage`
contains one text block. The adapter preserves System, Developer, User and
Assistant as native input roles, with no duplicate top-level `instructions`.
Host-triggered activations need no synthetic User message. A host owns source
classification, escaping and interpretation inside its text; the library adds
no memory/identity/provenance ontology or authority based on source labels.

```csharp
var request = new LlmRequest(model,
[
    new(LlmMessageRole.System, stableIdentityAndInterpretation)
        { CacheBreakpoint = new() },
    new(LlmMessageRole.System, escapedQualifiedContinuity),
    new(LlmMessageRole.Developer, hostStageAndOutputContract),
    new(LlmMessageRole.User, earlierParticipantText),
    new(LlmMessageRole.Assistant, publishedAssistantText),
    new(LlmMessageRole.User, currentParticipantText)
], generationOptions, outputSchema)
{
    MessageMode = LlmMessageMode.Ordered,
    PromptCache = new(LlmPromptCacheMode.Explicit)
};
```

`LlmMessage.Phase` is optional `Commentary` or `FinalAnswer`, only on Assistant.
Supply an actually known phase; operation/stage names are not assistant phases.
Absent phase remains absent. Native output items preserve their original phase
privately; this API does not reconstruct historical reasoning or classify speech.

`CacheBreakpoint` marks this text block's end. Its optional `Lifetime` and the
request's `LlmPromptCacheOptions.Lifetime` use `ThirtyMinutes` or `TwelveHours`.
At most four breakpoints can be present in the composed request. Metadata records
the requested/applied wire policy; model/account support, cache hits and savings
require separate evidence. No extra prewarming or cache-service call is made.

## Current native continuation

Choose one composition before preparation:

1. **Fresh:** the complete current frame, no `NativeContinuation`, append flag false.
2. **Append:** an Ordered carrier plus `AppendToNativeContinuation = true`.
   `Messages` contains only new items after that carrier's entire original
   input/output prefix. Do not submit the earlier frame or authored Assistant
   history again. Appended Assistant history is rejected. New qualified host
   observations and application directions may be appended as System/Developer
   at their actual point of effect.

The adapter never guesses overlap, rewrites the retained prefix or drops a
supplied carrier to make a call succeed. When earlier context must change, the
host chooses a fresh frame without the carrier and retains its own recovery
decision. A fresh evidence projection is not equivalent private reasoning replay.
See OpenAI's [preserved reasoning guidance](https://developers.openai.com/api/docs/guides/preserved-reasoning).

The existing consumer seam remains:

```text
base Ordered request without carrier/append flag
  -> Describe -> LlmNativeContinuationBinding.FromRequest -> ImportSensitive
  -> choose complete fresh frame OR attach carrier + explicit new-message delta
  -> Prepare -> CountAsync(prepared) -> GenerateAsync(requiredAdmission)
```

If import is unavailable/incompatible, the host must select the complete fresh
frame, not send only its planned delta. Model/API/configuration binding remains.
The append flag is a composition choice, not a model configuration change.
Ordered direct calls also bind newly returned carriers to their captured controls;
a later changed model configuration or instruction-role policy rejects before
another POST. Prepared calls reuse their already captured descriptor. The saved
prefix comes from the actual sent body, not a caller collection reread after the
response arrives.

Routes with top-level instruction channels retain their original compiled
instruction prefix privately. Their current Ordered append accepts new dialogue;
rewriting/inserting instruction blocks requires a fresh frame. Unsupported
instruction placement returns an explicit error instead of moving instructions
earlier in chronology. OpenAI, xAI and Ollama can append supported instruction
items as well. The SDK compatibility route still has no native carrier.

Ordered carriers use version **2** of the sensitive envelope, with explicit
`message_mode: ordered`. Legacy carriers continue exporting version **1**.
Cross-mode import returns `MessageModeMismatch`; old readers reject version2.
An Ordered carrier retains its exact role/order/content/phase prefix internally,
rather than comparing a flattened System+Developer string. Hosts still own
authenticated private storage, scope, retention and attempt reconciliation.

## Exact preparation and limits

Finish stage, schema and repair changes before `LlmCallPreparation.Prepare`.
The same mapping builds the frozen generation body, its report, descriptor,
native count input and physical request. Counting uses its own valid endpoint
schema and attempt identity linked to the full generation hash. Source labels
and XML do not authorize effects; required host admission still precedes dispatch.

The API supports bounded text messages. Unlinked Tool roles return
`native_tool_linkage_required`; host observations belong in caller-qualified text,
not fabricated `call_id` items.

OpenAI/xAI Ordered input can carry **previously observed** native call metadata:

```csharp
new LlmMessage(LlmMessageRole.Assistant, "")
    { FunctionCall = new(actualCallId, actualFunctionName, actualArgumentsJson) };
new LlmMessage(LlmMessageRole.Tool, receiptQualifiedResult)
    { ToolCallId = actualCallId };
```

These map to `function_call` / `function_call_output` items, never `role: tool`.
The result must match an earlier unresolved supplied call in the actual composed
input. Missing IDs, duplicate calls and duplicate results reject before dispatch.
The library never synthesizes IDs or converts a host's structured action proposal
into a provider call. This is input-history mapping, not a function-definition,
native-call output API, callback executor or tool orchestration framework. Other
routes explicitly report `unsupported_native_tool_mapping` for these new fields.
Synthetic linkage fixtures authenticate no real provider authorship.

## Route mapping and truthful fallback

`InstructionRolePolicy` defaults to `AllowInterpretedFallback`, the approved
mapping policy. `RequireNativeSeparation` rejects a Developer request on routes
where this adapter has no qualified distinct native Developer channel. No further
per-call human approval is needed for the declared policy.

| Route | Ordered native dialogue | Instruction mapping and limits |
| --- | --- | --- |
| OpenAI Responses | user/assistant message items | Native System/Developer in place; optional supplied phase and explicit cache controls |
| xAI Responses | user/assistant message items | Native System; Developer is a separately labelled subordinate System projection. Exact-route native Developer support remains unverified; OpenAI phase/cache controls reject |
| Anthropic Messages | user/assistant messages | Leading System/Developer blocks are escaped, ordered and interpreted in top-level system. Inline System placement remains explicitly unsupported by this adapter; no beta/model-route assumption |
| Gemini GenerateContent HTTP | ordered user/model contents and text parts | Leading System/Developer interpreted in systemInstruction; no invented signatures or mid-dialogue instruction relocation |
| Gemini Interactions HTTP | ordered user_input/model_output text steps | Leading System/Developer interpreted in system_instruction; existing stateless typed-step/native-signature handling remains |
| Gemini SDK, including configured Vertex | SDK-native Content user/model objects | Same leading instruction interpretation; route resolution remains unresolved and SDK native/prepared-call capabilities remain unsupported |
| Ollama native /api/chat | system/user/assistant messages | Developer uses labelled subordinate System projection; actual model/template behavior remains separately qualified |

For single-channel instruction mapping, System/Developer boundaries are retained
in escaped labelled blocks, with the interpretation of subordinate application
instructions supplied outside those blocks. This communicates semantics without
manufacturing a second provider-enforced priority. It does not reinterpret memory
or tool payloads; their original host interpretation remains authoritative.
`Describe` reports the translation, installed capabilities and unsupported
requests separately from model/account/live support. Phase/cache metadata on
unqualified routes rejects explicitly rather than disappearing.

Primary references checked November8,2026: [OpenAI roles](https://developers.openai.com/api/docs/guides/prompt-engineering),
[OpenAI cache](https://developers.openai.com/api/docs/guides/prompt-caching),
[xAI Responses](https://docs.x.ai/developers/rest-api-reference/inference/responses),
[Anthropic Messages](https://platform.claude.com/docs/en/api/messages/create),
[Anthropic model-specific inline placement](https://platform.claude.com/docs/en/build-with-claude/mid-conversation-system-messages),
[Google migration/typed steps](https://ai.google.dev/gemini-api/docs/migrate-to-interactions)
and [Ollama native chat](https://docs.ollama.com/api/chat).
Google reference redirects and Markdown MIME limits in the web reader were
resolved by reading the public official migration Markdown directly; no live
schema probe or Google model call was made.

Native function orchestration, multimedia, cross-session replay and historical
request migration remain outside this delivery.

## Qualification

- `OpenAiOrderedMessageTests`: actual serializers, fresh/append boundaries, mode
  rejection, role/phase/cache semantics, frozen caller input and linked count/send.
- `OrderedProviderMappingTests`: native dialogue and explicit fallback across six
  primary protocols, current native roundtrip/prepared follow-up, SDK physical
  serialization, strict native-separation rejection, placement limits and supplied
  call/result linkage. These use terminal offline transports and synthetic calls.
- `eng/Verify-ProviderConsumer.ps1`: fresh PackageReferences and paired artifact
  checks, existing35 cases, plus the Ordered provider-only three-decision host
  loop with two independently validated effects, receipts and host completion.
- The packed fixture emits `ordered-message-prepared-example.json` alongside its
  executable, plus append and fresh-rewrite examples. It is an actual prepared synthetic host activation, not installed
  Nyx identity or evidence of model behavior.

All provider traffic in these checks terminates in offline handlers. Required CI
and exact package/source pins qualify their reviewed cut; installed Bridge and
live behavioral/cache acceptance remain separate consumer observations.

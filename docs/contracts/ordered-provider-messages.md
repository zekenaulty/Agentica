# Ordered provider messages

`LlmRequest.MessageMode = LlmMessageMode.Ordered` opts into explicit message
boundaries. Existing constructors and Legacy/TranscriptOnly behavior remain.
The initial implementation is OpenAI Responses; other routes explicitly reject
Ordered instead of discarding its meaning. This is the first delivery of
[issue #65](https://github.com/zekenaulty/Agentica/issues/65), not full issue closure.

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

The first delivery supports bounded text messages. It rejects unlinked Tool roles
with `native_tool_linkage_required`; host observations belong in caller-qualified
text, not fabricated `call_id` items. Native function orchestration, multimedia,
cross-session replay and historical request migration are outside this delivery.

## Qualification

- `OpenAiOrderedMessageTests`: actual serializers, fresh/append boundaries, mode
  rejection, role/phase/cache semantics, frozen caller input and linked count/send.
- `eng/Verify-ProviderConsumer.ps1`: fresh PackageReferences and paired artifact
  checks, existing35 cases, plus the Ordered provider-only three-decision host
  loop with two independently validated effects, receipts and host completion.
- The packed fixture emits `ordered-message-prepared-example.json` alongside its
  executable. It is an actual prepared synthetic host activation, not installed
  Nyx identity or evidence of model behavior.

All provider traffic in these checks terminates in offline handlers. Required CI
and exact package/source pins qualify their reviewed cut; installed Bridge and
live behavioral/cache acceptance remain separate consumer observations.

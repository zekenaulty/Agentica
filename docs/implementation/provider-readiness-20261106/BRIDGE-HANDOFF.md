# Agentica → Bridge provider-only handoff: A1–A6

**Cut:** 2026-11-06. Implements the producer side of the approved
[plan](../../planning/provider-migration-readiness-2026-11-06/PLAN.md) and
[Bridge alignment](../../planning/provider-migration-readiness-2026-11-06/BRIDGE-ALIGNMENT.md).
Bridge retains its planner, executor, canonical state, operation resolver,
admission journal, storage, memory policy and completion checks. A provider-only
consumer references `Agentica.Clients`; it does not construct `AgenticaRunner`,
a Lab service or a second scheduler.

## Release and evidence status

| Boundary | Pin / evidence | Status at this cut |
| --- | --- | --- |
| Shared P01–P06 contracts and adapters | Main `a26e8d8cfbd7e3d6772d5e74c9c014c33b1cf9e7`; [PR #61](https://github.com/zekenaulty/Agentica/pull/61) completed both required Windows/Linux gates | Merged; offline contract/transport qualification |
| Early A5 packed embedding consumer | Main `004ad7acaa8e7bd29fd85036721906c5896424c6`; [CI run 42313237152](https://github.com/zekenaulty/Agentica/actions/runs/42313237152) | Previously qualified producer seam; no installed claim |
| P07 Lab adoption | Main `8b466e5`, [PR #63](https://github.com/zekenaulty/Agentica/pull/63), CI `42325652843`; [P07 evidence](P07-LAB.md) | Merged; Windows/Linux passed; real Lab proof in P09 |
| P08 full provider-only recovery host | Main `74d470a`, [PR #62](https://github.com/zekenaulty/Agentica/pull/62), CI `42325135654`; [qualification](../../../eng/provider-consumer/QUALIFICATION.md) | Merged; 35 packed scenarios, including 20 recovery cases; Windows/Linux passed |
| P09 live OpenAI / final artifact publication | [Qualification](P09-QUALIFICATION.md), [issue #56](https://github.com/zekenaulty/Agentica/issues/56) | Real Luna/high full loop and reopen passed; final exact-head CI/merge recorded on the issue |
| Actual Bridge / Nyx / Cloud adoption | External PM04–PM18 acceptance | Not established by Agentica producer tests |

All examples below use existing public APIs from merged core unless explicitly
marked with a later source cut. Model/account support and successful billing are separate from
adapter support. The local SDK pin is **10.0.302**. Packages remain internal
`0.1.0-research.1` previews; no public feed publication is assumed. Record source
commit, nupkg SHA-256 and restored DLL identity rather than trusting that reused
preview version alone. Root publishes the final qualified candidate/artifact pins.

## A1 — Retained attempt facts and usage

Artifacts: [references](../../../Agentica.Clients/Llm/LlmAttemptReferences.cs),
[capture](../../../Agentica.Clients/Llm/LlmAttemptCapture.cs),
[evidence](../../../Agentica.Clients/Llm/LlmAttemptEvidence.cs),
[usage](../../../Agentica.Clients/Llm/LlmUsage.cs), and the
[attempt contract](../../contracts/provider-attempt-evidence.md).

```csharp
var references = new LlmAttemptReferences(
    attemptId, operationId, phaseId, routeFingerprint, executionBundleReference);
var capture = new LlmAttemptCapture(references);
// For ordinary, unprepared clients only:
var observedRequest = request with { AttemptCapture = capture };
```

Prepared calls own their capture; pass the references to `Prepare` instead.
Read `response.AttemptEvidence`, `LlmClientException.AttemptEvidence`, terminal
event evidence and, especially after ordinary `OperationCanceledException` or
early disposal, `capture.Latest` / `prepared.Capture.Latest`. Capture is single-use
and in memory. Only the successful reserver owns an existing journal record:
a rejected duplicate must not overwrite its original outcome or usage.
References are bounded nonsecret identifiers: attempt/operation/phase up to 256
characters, route/bundle up to 512, with control characters rejected. Source text,
credentials, private native bytes and thought summaries do not belong in them.

Keep these facts independent in Bridge's mapping:

| Shared field | Host treatment |
| --- | --- |
| `References`, requested/effective/provider-reported model | Preserve original coordinates; do not infer operation ownership from Stage/Contract |
| `DispatchCertainty` | `NotDispatched`, `MayHaveDispatched`, `ResponseObserved`; a response header alone is not accepted completion |
| `Outcome`, `ProviderStatus`, `FinishReason`, `ReasonCode` | Client delivery, provider terminal state and host acceptance are different facts |
| HTTP status and provider request/response IDs | Nullable observed facts; do not manufacture missing identifiers |
| `Usage` and `OutputTokenSemantics` | Retain raw nullable counters; missing/conflicting facts remain unknown |

OpenAI output includes reasoning: `GeneratedOutputTokens` already includes it;
`NonThinkingOutputTokens` subtracts it only when supported by valid counters.
Do not add `ThinkingTokens` again or treat cached/tool counters as disjoint totals.
Cumulative stream counters replace prior counters; they are not deltas. Price
separately from evidence using current host price/cached-input/tool rules; these
derived counters are not a complete bill or context-occupancy measurement.

Proof: [retention cases](../../../Agentica.Clients.Tests/ProviderAttemptRetentionTests.cs),
[delivery cleanup](../../../Agentica.Clients.Tests/ProviderAttemptDeliveryCleanupTests.cs)
and [capture contracts](../../../Agentica.Clients.Tests/LlmAttemptEvidenceTests.cs).
Supported adapters include OpenAI, xAI, Anthropic, Ollama, Gemini Interactions,
GenerateContent and observed legacy SDK transport. This is offline evidence;
it does not qualify every live model/account.

## A3 — Exact preparation, reports and required host admission

A3 is the entrypoint for text transport. Exact public signatures are:

```csharp
LlmPreparedCall LlmCallPreparation.Prepare(
    LlmRequest request, OpenAiResponsesClientOptions options,
    LlmAttemptReferences references, string hostScopeId,
    HttpClient? httpClient = null);

ValueTask ILlmCallAdmission.AdmitAsync(
    LlmPreparedRequestDescriptor request, CancellationToken cancellationToken);

Task<LlmResponse> LlmPreparedCall.GenerateAsync(
    ILlmCallAdmission admission, CancellationToken cancellationToken = default);
IAsyncEnumerable<LlmStreamEvent> LlmPreparedCall.StreamAsync(
    ILlmCallAdmission admission, CancellationToken cancellationToken = default);
```

Example inside the existing Bridge adapter; `modelId`, `messages`, `strictSchema`,
`references`, `scopeId`, `options`, `controlledHttp` and `requiredAdmission` are
resolved/captured host inputs, not new Agentica concepts:

```csharp
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

var request = new LlmRequest(modelId, messages,
    new(MaxOutputTokens: maximumOutputTokens,
        Thinking: LlmThinkingOptions.AtEffort(
            LlmReasoningEffort.High, includeThoughts: true)),
    new(JsonSchema: strictSchema), NativeContinuation: restoredNative);

var report = LlmConfigurationReports.Describe(request, options,
    new(HostRouteReference: routeFingerprint,
        HostExecutionBundleReference: executionBundleReference));
if (!report.AdapterAcceptsRequest)
    throw new InvalidOperationException(string.Join(",", report.Issues.Select(x => x.Code)));

using var prepared = LlmCallPreparation.Prepare(
    request, options, references, scopeId, controlledHttp);
// Descriptor binds the frozen request. Retain report over the same captured inputs.
LlmResponse? delivered = null;
try
{
    await foreach (var item in prepared.StreamAsync(requiredAdmission, callerToken))
    {
        // Optional UI uses bounded best-effort delivery; it is never the journal.
        if (item.Kind == LlmStreamEventKind.Completed) delivered = item.Response;
    }
    if (delivered is null) throw new InvalidDataException("No completed delivery.");
    // Existing host: retain delivered material, validate schema/policy/evidence,
    // accept effects, update obligations and continue until host completion.
}
finally
{
    var observed = prepared.Capture.Latest;
    // Existing required journal: retain observed facts and delivered/unaccepted
    // state using its own bounded persistence lifetime, even after caller cancel.
}
// Caller owns delivered?.NativeContinuation; export or dispose it explicitly.
```

The comments above identify required **host operations**; they are not package
methods. The complete executable composition is the merged
[RecoveryHost](../../../eng/provider-consumer/RecoveryHost.cs),
[physical transport](../../../eng/provider-consumer/RecoveryTransport.cs) and
[recovery cases](../../../eng/provider-consumer/RecoveryCases.cs).

Implement `AdmitAsync` in Bridge's existing durable executor. Before returning,
atomically reserve this distinct attempt and its cumulative exposure; recheck
standing authority, revocation, source eligibility, scope, exact operation/phase,
captured route and bundle. A persistence exception prevents the associated POST.
Captured routes freeze configuration, not authority. Existing/nested/restarted
work retains its bundle; newly admitted work uses current settings. Memory and
conversation embedding profiles are independent references within that bundle.

`ConfigurationIdentity` describes nonsecret effective controls and route;
`RequestIdentity` additionally binds exact serialized content/protocol/endpoint,
scope and host coordinates. Neither is the complete host bundle ID or an authority
grant. Inspect `Controls`, `Capabilities`, `Issues`, `RouteResolution`,
`TechnicalReadiness` and `ModelAccountQualification`; adapter acceptance does not
prove live readiness. Exact unsupported controls are reported rather than silently
downgraded. A named effort such as High is not a token ceiling. Candidate P07 adds
`prepared.ConfigurationReport`; merged core at this cut uses `Describe` over the
same captured source request/options alongside `prepared.Descriptor`.

Preparation does no network work. Its request/native snapshot is detached and
one-use, including failed admission. Borrowed HTTP defaults are checked before
and after admission; arbitrary handler internals remain host-owned. Disable
redirects and hidden retries in the complete handler chain. Do not wrap this
custodied attempt in an automatic retry policy; a replacement has a new attempt
ID and a recorded host decision after original reconciliation.
Preparation bounds messages to 8,192 and serialized bodies to 16 MiB.
`CopySensitiveRequestBody()` returns an explicitly sensitive owned copy; never
place it in structural receipts. Dispose preparation and clear host byte copies;
this does not erase runtime strings or independent copies.

Native input counting is separate, admitted work:

```csharp
var counter = new OpenAiInputTokenCounter(controlledHttp, options, maximumCountRequests);
var countCapture = new LlmAttemptCapture(new LlmAttemptReferences(
    countAttemptId, operationId, phaseId, routeFingerprint, executionBundleReference));
var measurement = await counter.CountAsync(
    prepared, countCapture, requiredCountAdmission, callerToken);
// measurement.GenerationRequestIdentity / GenerationBodyHash bind its input.
// measurement.Tokens informs inference admission; it is not count-call billed usage.
```

Count requests have separate identities, limits and reservation. Failure does not
silently estimate, generate or retry. Bytes/characters are sizes, not token counts.
See [prepared-call contract](../../contracts/prepared-provider-calls.md),
[report API](../../../Agentica.Clients/Llm/LlmConfigurationReports.cs),
[preparation tests](../../../Agentica.Clients.Tests/LlmPreparedCallTests.cs) and
[count tests](../../../Agentica.Clients.Tests/OpenAiInputTokenCounterTests.cs).

## A2 — Private native exchange and durable recovery

Use the actual captured source request/report for export, and the separately
compiled actual target request/report for import. Never relabel an old unbound
carrier with a guessed target configuration.

```csharp
using var sourceBinding = LlmNativeContinuationBinding.FromRequest(sourceReport, sourceRequest);
var exported = LlmNativeContinuationCodec.ExportSensitive(native, sourceBinding);
if (!exported.Succeeded) throw new InvalidDataException(exported.Rejection.ToString());
using var payload = exported.SensitivePayload!;
var bytes = payload.CopySensitiveBytes();
try
{
    // Host: authenticate + encrypt bytes verbatim, with episode/scope/bundle,
    // format/key version and retention coordinates bound to its envelope.
}
finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }

using var targetBinding = LlmNativeContinuationBinding.FromRequest(targetReport, targetRequest);
// plaintext is bounded, authenticated host-decrypted bytes; clear it after import.
var imported = LlmNativeContinuationCodec.ImportSensitive(plaintext, targetBinding);
using var restored = imported.Continuation;
if (imported.Succeeded)
{
    var compatible = LlmNativeContinuationCodec.ValidateBinding(restored, targetBinding);
    // Prepare the target with NativeContinuation = restored only if compatible.
}
else
{
    // Host records rejection and reconstructs accepted evidence, or parks work.
}
```

The codec's SHA-256 checksum detects corruption, **not authenticity**. Host
authenticated storage is required where integrity matters. P08 demonstrates
AES-GCM bound to episode/scope/bundle; its adjacent fixture key is not production
key management. Encrypt source/private payloads separately from structural receipts.
Dispose owned bindings/payloads/carriers; clear host byte copies. Disposal cannot
erase immutable strings or parser/runtime copies.

Version 1 limits: encoded envelope **16 MiB**, native carrier **4,194,304
characters**, instructions **1,048,576 characters**, depth **64**, collection items
**8,192**, JSON tokens **131,072**. This token bound is parser structure, not model
context. Unknown versions, malformed/oversized/native-shape failures and exact
provider/API/model/instruction/configuration mismatches return explicit rejection.
The codec preserves provider signatures/encrypted items; it does not expose private
reasoning or translate it between providers. Unbound legacy carriers explicitly
report `ConfigurationBound = false` until a host-asserted source binding is attached.

See [exchange API](../../../Agentica.Clients/Llm/LlmNativeContinuationExchange.cs),
[codec](../../../Agentica.Clients/Llm/LlmNativeContinuationCodec.cs),
[contract](../../contracts/native-continuation-exchange.md) and
[codec tests](../../../Agentica.Clients.Tests/LlmNativeContinuationCodecTests.cs).

### Required episode recovery rules learned in P08

Persist objective, accepted evidence, obligations, original attempts/reservations
and captured bindings independently from the native capsule. Every unaccepted paid
outcome needs reconciliation, including delivered-but-unaccepted, known failure,
cancellation and host parsing rejection. Persist the delivered proposal/capsule
for revalidation, or park it. A `returned` flag without that material must not
trigger reinference or replay an older capsule. P08 verifies this with separate
writer and reader OS processes, then an explicit distinct successor through host
completion. Original evidence/reservation remains unchanged.

When native state is absent/rejected, reconstruct from **accepted host evidence**.
Retain semantic author, source ID, source revision and source-attempt identity
separately from provider envelope role. Host observations carried in a User input
remain **Host** evidence; prior assistant claims remain **Assistant** evidence.
They are neither participant statements nor trusted application instructions.
The fallback does not claim equivalent private reasoning. Bridge revalidates live
eligibility/authorization and independently proves completion against canonical
state and receipts, then continues or parks the exact original objective.

Default OpenAI native mode rejects synthesized assistant-history replay. The
merged P07 `OpenAiHistoryMode.TranscriptOnly` is a separate explicit visible
transcript mode, returns no native continuation and does not weaken the default.

## A4 — Shared OpenAI search and source activity

```csharp
// Ambient search on an otherwise compiled request:
var ambient = request with { WebSearch = new(LlmWebSearchMode.Auto, maxToolCalls: 3) };
// Explicit isolated search: no inherited transcript or application instructions.
var explicitSearch = OpenAiWebSearch.CreateRequest(modelId, query,
    new(MaxOutputTokens: maximumOutputTokens), maxToolCalls: 3);
// Prepare/admit either request using A3 with its own attempt identity.
```

The [helper](../../../Agentica.Clients/OpenAI/OpenAiWebSearch.cs) uses Required
native search; the model decides whether to search in Auto mode. Keep host tool
bindings and per-turn limits. Search bounds are 1–20 calls/request and 32,768
characters for the isolated query. `LlmResponse.WebSearch`, typed exception/event
search evidence and capture channels retain bounded observed calls, optional
query/queries, sources, citations and truncation independently from structural
attempt metadata. Missing queries are not inferred from participant text.

Preserve canonical Markdown and citation offsets/index units; render only supported
UTF-16 projections. Unknown/invalid offsets remain unavailable. The evidence is
`external-untrusted`, not verified truth or tool authority. Native search items
survive private export/import. See [contracts](../../../Agentica.Clients/Llm/LlmWebSearch.cs),
[shared parser tests](../../../Agentica.Clients.Tests/OpenAiWebSearchTests.cs) and
merged P08's dedicated search case. Bound activity, replay, URLs, retention and
UI independently; do not put search queries/source content into structural receipts.

## A5 — Existing embedding client with host-owned observed transport

```csharp
using Agentica.Clients.Embeddings;
using Agentica.Memory;

// Capture memoryProfile/conversationProfile independently in the host bundle.
var client = new OpenAiEmbeddingClient(
    new OpenAiEmbeddingClientOptions(memoryProfile, apiKey), admittedObservedHttp);
var batch = await client.EmbedAsync(
    memoryProfile, MemoryEmbeddingPurpose.Document, orderedTexts, callerToken);
// batch.ProfileId, ordered batch.Vectors and nullable batch.InputTokens.
```

`OpenAiEmbeddingClient` is not IDisposable; the host owns/disposes
`admittedObservedHttp`. Query calls use the exact matching profile and
`MemoryEmbeddingPurpose.Query`. Preserve source ID/revision and order when mapping
vectors; retain author/perspective/scope in the existing canonical citation model.
The API is `Task<MemoryEmbeddingBatch> EmbedAsync(MemoryEmbeddingProfile profile,
MemoryEmbeddingPurpose purpose, IReadOnlyList<string> inputs,
CancellationToken cancellationToken = default)`.

The required composition is:

```text
existing OpenAiEmbeddingClient
  → host admitted/observed HttpMessageHandler
    → qualified physical transport (no hidden retry/redirect)
```

The handler verifies actual model/dimensions/encoding/ordered input, binds exact
profile/purpose/scope/operation/source revisions plus body hash, persists reservation
before forwarding, then retains bounded response IDs/status/nullable Int64 input
usage **before vector parsing**. Restore the same bounded body for the client.
`EmbeddingClientException` alone cannot recover usage discarded by downstream
vector validation. Missing usage stays unknown; cancellation/lost response retains
original custody and does not authorize automatic retry.

Source/profile/eligibility checks must execute in actual required reservation.
Pure validator tests do not establish zero POSTs. P08 mutates live profile, source
revision and eligibility after request capture, proves zero sends/no reservation
on rejection, and retains an eligible positive case. A rejected duplicate cannot
overwrite the original record. Use the existing bounded examples:
[EmbeddingAttempt](../../../eng/provider-consumer/EmbeddingAttempt.cs),
[durable admission cases](../../../eng/provider-consumer/EmbeddingRecoveryCases.cs)
and [A5 scenarios](../../../eng/provider-consumer/Program.cs).

Current client bounds: 512 inputs, 32,768 characters/input, 1 MiB total UTF-8 input,
8 MiB serialized request and configured response cap up to 32 MiB. These are
client bounds, not guarantees of every model's accepted token/dimension limits.
Bridge PM11 supplies durable mapping/admission/observations; PM12–PM14 supply
indexing, source-version coverage, deletion/eligibility, recall and coherent
activation. New LLM admission APIs do not implicitly extend
`IMemoryEmbeddingProvider` or create a memory scheduler.

## A6 — Reproducible package/consumer qualification and cutover gates

Run [Verify-ProviderConsumer.ps1](../../../eng/Verify-ProviderConsumer.ps1) using
the exact SDK executable: `-DotnetPath <absolute-SDK-10.0.302-dotnet>`.
It performs locked library restore/build/pack, copies named consumer sources,
restores PackageReferences in a unique cache, checks fresh/restored nupkg bytes
and builds alongside **Google.GenAI 1.13.0**. No ProjectReference is used. This
dependency coexistence makes no Google request and grants no migration fallback.

Candidate P08's 35 local scenarios establish packed API wiring, a deterministic
model → bound host action → observation → update → continue loop, authoritative
completion, actual process reopen, native rejection reconstruction, required
journal/eligibility gates, retained failures/unknown originals, explicit successors,
cumulative reservations and counted offline single-send transport. Its atomic
file journal has one synthetic owner; it is not concurrent database qualification.
The [recovery guide](../../../eng/provider-consumer/RECOVERY.md) pins representative
Bridge foreground, memory/evaluation and document schema shapes to `187b203c` and
documents synthetic substitutions. Bridge must qualify its actual current dynamic
schemas, semantic validators, complete handler chain and all operation routes.

### Upgrade, rollback and remaining ownership

1. Pin package bytes/source and the existing Google dependency; qualify Bridge's
   own packed consumer before activating a route. Retain old artifacts and versioned
   native envelopes; explicitly reject unsupported readers. Upgrade does not reset
   reservations, retry counts, budgets or original outcomes.
2. Resolve each actual operation/phase, then task default, then bundle default.
   Preserve captured route/bundle/profile references for existing work; recheck live
   policy. Do not infer a route from broad Stage/Contract labels.
3. Keep Nyx's first cutover **offline and OpenAI-only**. No new Google inference or
   embedding calls through probes, recovery, fallback, retries or query encoders.
   Retain old vectors/capsules/results and uncertain original attempts. Recover
   eligible old Google work through a recorded OpenAI successor boundary using
   accepted evidence; never import a Gemini capsule as OpenAI native state.
   Drive/Docs source transport remains available under its existing grants.
4. Required external gates remain PM10 media, PM11–PM14 embedding/reindex/profile
   eligibility/recall, PM16 native TypeScript Cloud adapter, PM17 actual integrated
   loops/recovery and PM18 installed return-online acceptance. No C# service or
   .NET dependency is imposed on the Cloud Worker. Preserve unaffected capabilities
   and name unavailable operations explicitly.
5. P09's isolated Agentica proof allowance (US$5, six inference attempts, eight
   count requests with its per-request limits) does not fund Bridge reindex or
   production. Live proof, current prices, final integration CI/browser acceptance
   and exact release hashes are still pending at this cut. Producer fixtures do
   not declare installed Nyx, canonical memory migration or the whole program done.

# Explicit native-continuation exchange

`LlmNativeContinuationCodec` exports and imports the existing private carriers.
It is independent of the planner and performs no provider call or storage write.
The result remains provider-native replay state. It is not accepted work state,
canonical memory, an attempt receipt, a tool grant or an account of private reasoning.

## Public flow

1. Capture the actual source request/options and its resolved configuration report.
2. Construct `LlmNativeContinuationBinding.FromRequest(report, request)`, or supply
   the exact provider/API/model/configuration/instruction binding explicitly.
3. Call `ExportSensitive(continuation, sourceBinding)`. Inspect its rejection result.
4. Explicitly call `SensitivePayload.CopySensitiveBytes()`. The host protects and
   persists those bytes in its own authenticated envelope.
5. On reopen, recheck host scope, authorization, expiry and deletion policy. Compile
   the actual target configuration and call `ImportSensitive(bytes, expectedBinding)`.
6. Before dispatch, call `ValidateBinding` against that captured target binding.
   The normal adapter guards still apply. Import is not permission to dispatch.

`LlmSensitiveContinuationPayload`, the export/import result objects and binding
display are redacted by default. Ordinary request/response JSON omits continuation.
Binding JSON includes only nonsecret coordinates; its instruction body is private.
There is no automatic serialization path for the sensitive exported bytes.

Dispose the payload, binding and owned continuation when finished. Disposal releases
private string references and zeroes the payload instance's byte array. It cannot
erase immutable strings, parser/runtime copies, or byte arrays copied by a caller.
The host owns those copies, encryption/key management, retention, expiry and deletion.

## Version and bounds

Version 1 is an envelope containing `payload` and an unkeyed SHA-256 checksum over
the exact payload bytes. The payload identifies its format/version, provider,
API mode, exact model, source configuration, instructions and ordered native history.
Store bytes verbatim; reformatting the payload changes its checksum.

The checksum detects accidental corruption. **It does not prove authenticity or
provider authorship.** Anyone able to rewrite unsigned bytes can recompute it.
Hosts that require integrity must authenticate their own envelope and bind its scope,
episode/source identity and expiry. The offline host-envelope fixture demonstrates
AES-GCM rejection of altered ciphertext or mismatched associated scope; core does
not choose or implement host storage/encryption policy.

Limits are fixed and checked before native state materialization:

| Limit | Value |
| --- | --- |
| Encoded envelope | 16 MiB |
| Native history | Existing 4,194,304-character carrier ceiling |
| Instructions | 1,048,576 characters |
| JSON depth | 64, including envelope structure |
| Items per array/object | 8,192 |
| Total JSON tokens | 131,072 |

Malformed JSON, duplicate property names and exceeded limits produce stable
rejection codes. These exchange bounds can reject a larger in-memory carrier;
failure remains explicit and never silently truncates/reorders native state.

## Formats and compatibility

One codec handles all currently implemented carrier modes:

| Provider | API mode | Preserved native representation |
| --- | --- | --- |
| OpenAI | `responses` | Ordered user and output items, encrypted reasoning, assistant phase and fields |
| xAI | `responses` | Its own provider-bound Responses items and encrypted state |
| Anthropic | `messages` | Ordered messages and signed/redacted thinking blocks |
| Ollama | `chat` | Ordered messages and native `thinking` fields |
| Gemini | `interactions` | Ordered user/model/thought steps and opaque signatures |
| Gemini.GenerateContent | `generate_content` | Ordered contents/parts, including signed empty parts |

The codec validates native shapes at these existing adapter boundaries. It does not
translate formats into a generic assistant transcript. Unknown provider fields on
accepted native objects are retained where the current adapter permits them. Legacy
Gemini SDK mode has no replay carrier and remains unsupported. New provider-native
item kinds require explicit boundary qualification, including search items when that
shared capability lands.

Provider, API mode, exact model, instructions and captured configuration must match.
No automatic cross-provider transfer, model-family widening or instruction/config
normalization is performed. A changed response schema/effort/route can therefore
require a new logical native context even when the provider might support broader
reuse; qualify any broader compatibility policy separately.

Existing in-memory carriers predate configuration metadata. `ValidateBinding`
preserves their provider/model/instruction guards and returns
`ConfigurationBound = false`. That is explicitly unknown source configuration.
The first explicit export attaches the host's asserted source configuration; the
codec cannot infer which endpoint/settings produced an unbound carrier. The host
must supply the actual captured source configuration, not relabel it as the target.
Imported/attached bindings cannot be changed by re-exporting. Owned clones preserve
the metadata, and independently disposing a source does not dispose its clone.

Unavailable, disposed, invalid or incompatible state is a recoverable host decision.
Reconstruct context from durable accepted evidence or suspend the episode with a
reason; do not claim equivalent private-reasoning replay, erase an uncertain original
attempt, or dispatch a replacement call implicitly.

## Verification and implementation lessons

`LlmNativeContinuationCodecTests` round-trips every mode through matching follow-up
request builders. It checks opaque fields, native order, assistant phase, signed
empty parts, clone ownership/bindings, default JSON/display redaction, corruption,
unsigned-content limits, authenticated host envelopes and explicit failure paths.
`eng/NativeContinuationProbe` reopens synthetic persisted bytes in a fresh process
using public APIs and prints only the re-export digest. These are offline contract
and consumer proofs, not live model qualification or Bridge episode recovery.

The ownership clone must carry configuration metadata as well as private strings:
reconstructing it with the old four-argument constructor discards an imported guard.
Likewise, a signed empty provider part is significant native state; projecting only
visible text would lose it. Keep work-episode recovery and canonical-memory admission
in the host when native replay is unavailable.

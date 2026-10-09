# Gemini GenerateContent streaming transport

The Lab Web service defaults to stateless Gemini Interactions. An explicit
provider setting selects the Developer API GenerateContent stream instead:

~~~json
{
  "provider": "gemini",
  "geminiApi": "generateContent",
  "model": "gemini-2.5-flash",
  "includeThoughtSummaries": true
}
~~~

The existing "legacy" transport spelling selects the same streaming adapter.
Omitting geminiApi, or selecting "interactions", continues to use Interactions.
This selection does not change the host WebSocket contract or browser SDK.

## Adapter boundary

- GeminiGenerateContentLlmClient implements ILlmStreamingClient using the Developer
  API POST models/{model}:streamGenerateContent?alt=sse endpoint. HTTP headers are
  read first; text and optional thought-summary deltas are delivered while bytes
  continue arriving.
- GEMINI_API_KEY or GOOGLE_API_KEY is service configuration. The key travels in
  the x-goog-api-key header. ProviderSettings does not accept keys or endpoints.
- Requests carry systemInstruction, explicit user contents, and generationConfig.
  JSON response schema, temperature, output budget, thinking budget or named
  thinking level, and summary opt-in use their GenerateContent field names.
  Model-specific thinking support remains Google's contract: in particular,
  Gemini 2.5 uses thinking budgets; Gemini 3 supports named thinking levels.
- Provider text output proposes Agentica plans. Provider-native function calls,
  code execution, multimedia output, and multiple candidates are rejected by
  this adapter; existing Agentica host execution still handles bound tools.
- The existing SDK-backed GeminiLlmClient, including its Vertex configuration,
  remains a separate compatibility API. The new route is the Developer API.

## Continuation and telemetry

Each ordered native model part is retained exactly as a part, including empty
text parts that carry thoughtSignature. Signed parts are never concatenated,
reassigned, or exposed as ordinary text. Gemini.GenerateContent is a distinct
private continuation identity from Gemini Interactions because their native
history formats differ.

Continuation binds the transport, model, and system instruction. A follow-up
sends the previous user/model contents and a new user message; it does not use
remote conversation IDs. Only a stopped response with text can produce a private
continuation. Truncation, safety blocks, errors, and cancellation cannot create
one. Existing planning-session custody and input budgets still apply.

Thinking activity and summary text are separate telemetry. Returned thought
summaries are observable only when explicitly requested; opaque signatures never
appear in generic event serialization. These summaries are provider-supplied
summaries, not raw internal reasoning or execution proof.

Completion requires an explicit finishReason or prompt-block result and a clean
end to the stream. Partial text, malformed events, provider errors, unexpected
parts, or candidate data after a terminal result do not establish completion.
Usage metadata can arrive after the final candidate.

## Evidence and limits

Provider-free tests cover gated streaming before completion, cancellation,
signature-bearing empty parts, exact multi-turn replay, transport/model/policy
binding rejection, summary opt-in, generation controls, terminal failures,
unsupported output, size limits, HTTP classification, and both Web selector
spellings. Test results are reported by the coordinated repository test run.
These fixtures do not qualify live Gemini availability or model behavior.

The SSE event limit is 1 Mi characters; text is bounded at 4 Mi characters,
summaries at 256 Ki characters, and retained native output at 4 Mi characters and
8192 parts. StreamReader checks each decoded line at the event boundary.

## Primary API references

- [GenerateContent streaming endpoint and request](https://ai.google.dev/api/generate-content#method:-models.streamgeneratecontent)
- [Thought signatures and empty streaming text parts](https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures)
- [Thinking controls and incremental thought summaries](https://ai.google.dev/gemini-api/docs/generate-content/thinking)

Checked against Google's primary documentation on 2026-10-09.

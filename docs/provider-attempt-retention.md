# Provider attempt retention

P02 of the [provider readiness plan](planning/provider-migration-readiness-2026-11-06/PLAN.md).

## Ownership and use

All five text provider families use `LlmAttemptRecorder` through one stream-delivery
boundary. Ordinary `GenerateAsync` drains that same boundary. The compatibility
`GeminiLlmClient` records the same evidence through its SDK HTTP seam.

Supply a new `LlmAttemptCapture` on each `LlmRequest` when evidence must remain
available after ordinary cancellation or early stream disposal. Read its `Latest`
after the operation settles. A successful response and a `LlmClientException`
also carry retained snapshots. Cancellation remains `OperationCanceledException`;
its token and cancellation behavior are preserved. Capture is observation, and
does not reserve funds, persist receipts, authorize dispatch, or permit retry.

The recorder observes IDs, reported model, status and usage **before** content,
refusal, native continuation or terminal consistency validation. Later errors
cannot erase those observations. Raw signatures, native reasoning and response
content remain in their existing private continuation channels. They are not
copied into attempt evidence. Missing usage remains unknown.

`Completed` is held until provider enumeration and stream disposal succeed.
Receiving a provider terminal record does not establish successful client delivery.
Text and summary deltas remain live. A late malformed event, disconnected stream,
or cancellation prevents a successful `Completed`; known terminal facts remain.
Disposing a stream early records `Abandoned`, and caller cancellation records
`Cancelled`. A client-delivered `MaxTokens`, `Safety` or `Blocked` finish remains
distinct from host acceptance or goal fulfillment. Existing adapter finish behavior
is preserved: OpenAI/xAI incomplete terminal records reject; the other adapters can
return their existing truncated response with `MaxTokens`.

## Physical request boundary

`LlmProviderTransport.SendAsync` is the sole HTTP inference send used by these
adapters. It sends once with `ResponseHeadersRead`, observes response status and
bounded request-ID headers, and has no retry loop. The default clients explicitly
disable automatic redirects. The host owns injected handler chains; a handler
that retries, redirects or dispatches other work can violate this physical-request
claim. Qualify that chain separately. Authentication token acquisition, when a
host selects Vertex ADC, is not an inference request and is not a provider usage
observation.

`RetryingLlmClient` rejects a request carrying an attempt capture when configured
for more than one attempt, before any transport send. Its historical uncaptured
retry behavior remains available for compatibility. A host requiring durable
single-attempt custody must omit this wrapper or configure one attempt. Recovery
uses a new capture and host-admitted attempt identity; reusing a capture fails.

## Google SDK qualification

The compatibility adapter updates Google.GenAI from 1.6.1 to **1.13.0**. The older
SDK constructed its own private HTTP client and lacked a supported factory. The
new version supplies `ClientOptions.HttpClientFactory`. Each invocation receives
an observed handler with a one-send guard, using the same common transport as the
direct HTTP adapters. The SDK message is detached byte-for-byte before the second
`HttpClient` boundary because .NET rejects sending an already-sent message again.
An ordinary SDK response is buffered once; structural observations are recorded
before SDK deserialization or `GeminiResponseMapper` can reject the result.

Upstream source pinned by the NuGet metadata:

- [Google.GenAI 1.6.1 ApiClient](https://github.com/googleapis/dotnet-genai/blob/c0788f3f2b1c794f836b2443166655d824f22a3f/Google.GenAI/ApiClient.cs): private default client construction.
- [Google.GenAI 1.13.0 ApiClient](https://github.com/googleapis/dotnet-genai/blob/6bea845f6bddc5215ff4a89aed521ae4084678c9/Google.GenAI/ApiClient.cs): supported factory.
- [Google.GenAI 1.13.0 HttpApiClient](https://github.com/googleapis/dotnet-genai/blob/6bea845f6bddc5215ff4a89aed521ae4084678c9/Google.GenAI/HttpApiClient.cs): one `SendAsync` per request method, no automatic retry loop.

The Developer SDK path explicitly selects `vertexAI: false` so a later ambient
mode environment variable cannot silently replace the configured mode. Existing
Vertex selection and authentication remain supported through the same observed
inference handler. Offline SDK transport fixtures exercise Developer requests,
including 429 and 503. They do not qualify ADC credentials, token acquisition,
Vertex service behavior, or any live provider.

## Evidence and limits

`ProviderAttemptRetentionTests` covers six wire modes (OpenAI, xAI, Anthropic,
Gemini Interactions, GenerateContent and Ollama) and ordinary SDK compatibility:
success; refusal/invalid output with co-located usage; late malformed data;
disconnect and cancellation after terminal usage; HTTP failures; early disposal;
pre-dispatch cancellation; truncation; SDK response-deserialization failure;
inclusive versus exclusive thinking usage; and captured retry-wrapper rejection.
Existing protocol/native-continuation fixtures remain required.

Malformed JSON cannot yield structural fields that were never parsed. Direct HTTP
error paths retain received headers/status and do not read arbitrary error bodies.
The ordinary SDK buffers its body before status/error mapping; any parsed usage
metadata is retained as provider-reported evidence even when the HTTP status is
unsuccessful. Those counters do not establish successful delivery or acceptance. Whole-body/line limits and protocol-supported states retain the
adapter's existing bounds. A request ID or provider status outside the structural
length/control-character bounds is omitted; it cannot displace valid usage.
These fixtures prove deterministic local behavior, with zero live provider calls.

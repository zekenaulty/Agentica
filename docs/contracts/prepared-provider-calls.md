# Prepared provider calls and required host admission

`LlmCallPreparation.Prepare` is an optional provider-only seam. It does not require
an Agentica runner, planning request, Lab service or new host authority model.
Ordinary client entrypoints remain compatible. Standing grants can satisfy a
host's admission policy; admission does not imply a new user confirmation step.

## Exact request ownership

Preparation performs no HTTP. Overloads cover OpenAI Responses, xAI Responses,
Anthropic Messages, Ollama chat, Gemini Interactions and streaming GenerateContent.
They reuse the installed serializers and capture options, effective model,
credentials, messages and separately disposable native history. Caller mutations,
credential environment changes and disposal of the original capsule cannot change
the prepared request. An imported capsule retains and rechecks its exact binding.
Borrowed HTTP defaults and timeout are fingerprinted and checked before and after
the awaited admission hook. Changing them invalidates preparation. The host still
owns custom handler behavior and concurrent mutation of its HTTP transport.

`LlmPreparedCall.Descriptor` exposes bounded metadata: work kind, effective model,
scope, opaque operation/phase/route/bundle references, configuration identity,
request identity, body hash, serialized characters and wire bytes. Its displayed
endpoint omits query/userinfo/fragment. The request hash binds the complete actual
endpoint and protocol headers as well as exact body bytes and host scope. The
attempt ID is separate from logical request identity: a replacement can have the
same request identity but requires a new attempt and host decision.

The request body is sensitive. `CopySensitiveRequestBody()` explicitly returns an
owned copy for host inspection or counting. Ordinary JSON and display exclude the
body, credentials, capture and native payload. Hosts own any additional copies,
encryption and retention. Disposing a prepared call zeroes its owned bytes and
releases its capsule; this cannot erase runtime strings or host copies.

## Required reservation before transport

Pass `ILlmCallAdmission` to `GenerateAsync` or `StreamAsync`. At the configured
transport boundary Agentica verifies the exact method, endpoint, protocol headers,
content type, body bytes and attempt owner, then awaits `AdmitAsync`. A failed
reservation, persistence error, revocation or budget rejection produces zero
inference sends. The host implements durable intent/reservation, live eligibility
and authority checks, cumulative budgets and duplicate handling. Changing a scope,
captured route, bundle, input or configuration changes the request identity.

The prepared call and internal dispatch binding are consumed once, including
failed admission. A host retries or replaces work by explicitly preparing a new
attempt after reconciling the original. `RetryingLlmClient` cannot secretly multiply
a captured/prepared attempt. Default HTTP transports disable redirects; arbitrary
borrowed handlers still require the host's own physical-send qualification.

Read `prepared.Capture.Latest` after cancellation/disposal. Final parsed evidence
remains available on responses/exceptions, and the host persists and settles it.
The in-memory capture and admission interface are not a durable episode store or
automatic settlement worker. Provider completion, client delivery and host
acceptance retain separate meanings.

## Separate native input counting

`OpenAiInputTokenCounter.CountAsync` accepts the exact prepared OpenAI call, a
different caller-owned attempt capture and its own required admission. It submits
the complete applicable stateless input, including instructions, tools, native
items, reasoning and schema. It returns a measurement linked to the generation
identity/body hash and a separate count descriptor/evidence. It neither consumes
the prepared inference nor creates a conversation turn.

Counted input tokens are not billed usage of the count request. They are never
written into that request's `LlmUsage`. Count HTTP attempts have a separate local
limit and host reservation/budget. Failures, cancellation and lost responses do
not trigger fallback estimates, generation or retries. A count can inform the
host's later inference reservation; it is not an inference grant. Other modes
report unmeasured input; wire bytes/characters are not mislabeled as tokens.

The legacy Gemini SDK entrypoint has observed single-send transport under P02,
but its static/environment route and SDK serialization are not an exact frozen
preparation seam. Its configuration report remains explicitly unresolved. Use
the explicit Interactions/GenerateContent adapters for prepared Gemini work.
Vertex ADC authentication and live model/account behavior require separate proof.

## Qualification

P03 tests send actual serializer output through counted offline HTTP handlers in
all six modes. They cover failed reservation, immutable content, exact protocol
binding, separate count work, cancellation, scope/route/bundle identity, imported
capsule compatibility and a sequential persisted host admission reopen. Host
concurrent database transactions, installed Lab behavior, complete P08 recovery
and paid-provider behavior remain their separate gates.

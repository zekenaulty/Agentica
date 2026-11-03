# Responses structured-output contract

Issue: [H-01 / #21](https://github.com/zekenaulty/Agentica/issues/21).

## Request policy

The existing `LlmStructuredOutputOptions(ResponseMimeType, JsonSchema)` constructor
is unchanged. Its optional `SchemaEnforcement` init property defaults to
`LlmJsonSchemaEnforcement.Strict`; callers may explicitly select `BestEffort`.
Provider enforcement differs, so this property is not a universal schema guarantee.

OpenAI and xAI Responses requests map a supplied object schema to `text.format`
with `type: "json_schema"`, the stable legal name `agentica_response`, the original
schema, and `strict` corresponding to the selected policy. No schema is rewritten,
stripped, or silently replaced with JSON mode. With no schema, the existing
`json_object` mode remains. Streaming and `store: false` remain mandatory.

OpenAI documents strict schema adherence for a supported subset: the root must be
an object, object fields must all be required, and objects must disallow additional
properties. The adapter rejects obvious incompatible strict shapes before HTTP,
including open objects, missing required properties, and documented unsupported
composition keywords. This bounded check is not a complete schema validator;
remaining provider or model restrictions can reject the request. That failure is
returned without retrying a weaker format. Malformed JSON, nonobject schema
documents, unsupported MIME types, and unknown enforcement policies also fail
before dispatch. [OpenAI guide](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses),
[Responses reference](https://developers.openai.com/api/reference/resources/responses/methods/create).

xAI accepts the same Responses schema envelope, but its reference describes
`strict`, `name`, and `description` as compatibility fields. Its supported schema
features and enforcement rules differ. The OpenAI shape preflight therefore does
not apply to xAI, and sending `strict: true` does not establish OpenAI-equivalent
strict guarantees. [xAI Responses reference](https://docs.x.ai/developers/rest-api-reference/inference/responses),
[xAI structured outputs](https://docs.x.ai/developers/model-capabilities/text/structured-outputs).

## Existing planner compatibility

Workflow plan schemas contain open `steps[].input` objects because bound tools
own their input contracts. Closing those objects would change the workflow
contract. `WorkflowPlanPromptBuilder` explicitly selects `BestEffort` for its
initial and refinement schemas. Repairs preserve that original selection. The
Responses adapter still transmits the complete schema with `strict: false`.

The workflow parser, compiled tool manifest, plan validation, and effect checks
retain their existing responsibilities. A schema-shaped provider response is a
proposal, not proof that a plan is legal or its objective fulfilled. Gemini,
Ollama, and Anthropic retain their existing request projections in this slice;
their enforcement parity is not implied by this new request property.

The CLI `ChatArtistPromptComposer` also explicitly selects `BestEffort`: its
existing schema has optional `aspectRatio`/`sourceSignals` fields and open root
and layer objects. Those fields and their optional meaning remain unchanged.
Its schema regression uses the actual composer and Responses request builder
through fake HTTP, without a request-rewriting shim. The host supplies a small
artist profile with its configured text model and nullable temperature. OpenAI,
xAI and Anthropic omit the unsupported temperature; Gemini SDK and Ollama retain
the existing 0.55. An explicit composerModel overrides the configured profile;
the image tool no longer substitutes a Gemini text model for every provider.
An unconfigured Ollama artist requires a model before dispatch. Image generation
itself remains the separate Gemini capability and stays quarantined as before.
This is a bounded partial correction for [provider audit #10](https://github.com/zekenaulty/Agentica/issues/10),
not completion of its model-default freshness or live-provider qualification.
The other supplied Lab schemas in the ChessQuest probes/projection retain their
current Gemini-oriented callers; moving them to Responses requires an explicit
schema and generation-option compatibility check.

## Evidence boundary

Provider-free HTTP fixtures inspect exact OpenAI/xAI wire envelopes, strict direct
requests, schema-free JSON mode, explicit best-effort planning with open tool
inputs, host parse rejection, and failure without format fallback. They do not
qualify live provider/model compatibility, response quality, or complete JSON
Schema conformance. Existing reasoning, private continuation, usage and stream
integrity tests remain separate regression evidence.

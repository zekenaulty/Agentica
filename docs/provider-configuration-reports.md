# Provider-only configuration reports

`Agentica.Clients.Llm.LlmConfigurationReports.Describe(request, options, context)`
is an optional, network-free report. Supply the **same captured options** used by
the client. Overloads cover OpenAI Responses, xAI Responses, Anthropic Messages,
Ollama Chat, Gemini Interactions, Gemini streaming GenerateContent and the legacy
Gemini SDK Developer/Vertex modes. It does not select models or resolve routes.

The report uses the actual adapter builders to expose requested, applied, adapter
default, provider default and unapplied controls. Named effort is distinct from a
numeric thinking budget. An omitted effort has no claimed effective level. A
dynamic-budget sentinel can leave a default, select adaptive thinking or enable a
boolean control depending on the provider. Public summaries, native thought
material and opaque replay remain separate concepts.

## Evidence and validation

- `AdapterEvidence`: installed serializer behavior, assembly source/version and
  report as-of time. This is not a provider capability census.
- `TechnicalReadiness`: unknown unless the host supplies a matching observation.
- `ModelAccountQualification`: independently unknown unless the host supplies a
  matching observation. A recognized model name or present API key proves neither.
- `RouteResolution`: distinguishes resolved adapter endpoints from the legacy
  SDK's unresolved static/environment routing. An SDK option snapshot alone does
  not determine that SDK's actual transport route.
- `Issues`: stable classifications from rejected requests, with no provider error
  message or source content. No model/effort substitute is selected after rejection.
- `AdapterAcceptsRequest`: local builder acceptance only. Network access,
  credentials, authorization, live model support and successful inference require
  their own evidence.

A rejected control is classified unsupported. Other controls that were not
evaluated before that rejection remain unresolved/unknown, rather than inheriting
the rejection. An accepted adapter can also leave a particular option unapplied;
the report preserves that distinction. `AdapterSupport` describes the installed
control/value mapping independently of application: an omitted unsupported
temperature stays unsupported, while an omitted supported summary switch does
not become unsupported merely because it was not emitted.

Host observations bind to `ConfigurationIdentity`. A changed model, endpoint or
applied configuration invalidates a mismatched observation. Hosts also own
credential/account scoping, freshness and revocation; rotating credentials can
require requalification even when the nonsecret configuration identity is unchanged.

## Current adapter matrix

| Adapter mode | Named effort | Positive numeric budget | Native format | Streaming |
| --- | --- | --- | --- | --- |
| OpenAI Responses | none/minimal/low/medium/high/xhigh/max | Rejected | JSON object / schema, strict flag | Yes |
| xAI Responses | low/medium/high/xhigh | Rejected | JSON object / schema, compatibility strict flag | Yes |
| Anthropic Messages | low/medium/high/xhigh/max | Manual, at least 1024 and below output limit | Unconstrained text; host validation | Yes |
| Ollama Chat | low/medium/high | Rejected; boolean on/off supported | JSON / schema | Yes |
| Gemini Interactions | minimal/low/medium/high | Rejected | JSON / schema | Yes |
| Gemini GenerateContent | minimal/low/medium/high | Serialized budget; incompatible with named level | JSON / schema | Yes |
| Legacy Gemini SDK | minimal/low/medium/high | Serialized budget; incompatible with named level | JSON / schema | No |

These are **adapter mappings**, not claims that every current or future model or
account supports them. Provider schema subsets and strictness are not equivalent.
In particular, Anthropic currently parses supplied schema syntax without emitting
a native output constraint. The report makes that omission visible. Broad model
and default research remains tracked separately by issue #10.

The report marks native search and native pre-dispatch input counting unsupported
on the shared client surface at this source snapshot. Lab's separate implementations
do not establish shared-client support. Extend these entries alongside P03/P06 when
their seams land. Explicit [durable continuation exchange](contracts/native-continuation-exchange.md)
is available for the existing native carriers; legacy SDK replay remains unsupported.
Response usage is identified as post-response accounting; proxy estimates are not
called native counts.

## Identity and host routing

`ConfigurationIdentity` hashes nonsecret provider/API mode, effective model,
sanitized endpoint, SDK route coordinates and applied controls/schema. It excludes
API keys, endpoint userinfo/query/fragment, prompts, metadata, native history,
source content and host bundle references. Endpoint query parameters are excluded
conservatively because they may contain credentials. A host using query parameters
for semantic routing must bind those nonsecret coordinates in its own route record.
This identity is **not** a complete request hash, idempotency key, admission grant
or full execution-bundle identity.

Legacy Gemini SDK reports use an `unresolved:` identity prefix and always retain
unknown readiness/model qualification, even if a host supplies an observation with
the same partial fingerprint. The pinned SDK additionally resolves mutable static
base URLs and environment mode, endpoint, project and location defaults. Its actual
route is not captured by this optional options-only report. Prefer the explicit
Interactions/streaming GenerateContent adapters for a resolved route. A future SDK
capture seam must bind the actual resolved route before permitting observation reuse.
See pinned [Client.cs](https://github.com/googleapis/dotnet-genai/blob/c0788f3f2b1c794f836b2443166655d824f22a3f/Google.GenAI/Client.cs)
and [ApiClient.cs](https://github.com/googleapis/dotnet-genai/blob/c0788f3f2b1c794f836b2443166655d824f22a3f/Google.GenAI/ApiClient.cs).

`HostRouteReference` and `HostExecutionBundleReference` remain separate opaque
references. The host resolves the actual operation and inference phase, then
reports that resolved request. A tool's model assignment applies to its own
inference phases; it does not rewrite generation, evaluation or unrelated tool
routes. Existing combined requests, including memory prose and keywords, remain
one request. Deterministic tools have no inference model assignment and should not
produce an inference configuration report merely because they are bound tools.

Collections are immutable snapshots. Reporting performs no HTTP request and does
not mutate client options, conversation state, tool bindings or provider settings.

## Implementation lessons

An accepted option is not necessarily an applied provider control: Anthropic's
schema parsing is one concrete example. Read the actual compiled fields before
claiming native constraint support. The offline transport fixtures in
`LlmConfigurationReportTests` compare reported controls with outgoing request bodies.

Configuration validation also lives outside body serialization. GenerateContent's
default endpoint validates the model ID, while an explicit endpoint override
bypasses that helper. The report reuses both helpers so an invalid default-route
model cannot be presented as an accepted request. This is source and offline-fixture
evidence; it does not qualify a real model/account or deployment.

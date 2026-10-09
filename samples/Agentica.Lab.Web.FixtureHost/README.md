# Loopback streaming fixture host

This nonpackable sample launches the existing Lab service with an injected planner factory. It exercises real HTTP SSE parsing, planner sessions, validation, remote host actions, receipts and completion without a model account. It does not edit the main host or select a production provider.

## Launch

Run these commands from the repository root in separate terminals.

1. Start the supplied Node fixture (Node 22 or newer):

   ~~~powershell
   node samples/Agentica.Lab.Web.FixtureHost/responses-fixture.mjs --port 5081
   ~~~

2. Install .NET SDK **10.0.302**, then start the Lab fixture host. The repository's `global.json` selects that exact SDK and disables roll-forward:

   ~~~powershell
   dotnet --version
   dotnet run --project samples/Agentica.Lab.Web.FixtureHost -- --fixture-endpoint http://127.0.0.1:5081/v1/responses --urls http://127.0.0.1:5079
   ~~~

   `dotnet --version` must report `10.0.302`. The fixture endpoint argument is mandatory. Only explicit loopback HTTP URLs are accepted. Automatic redirects and proxies are disabled on the factory-owned HTTP client.

3. Run the existing browser SDK integration against this service:

   ~~~powershell
   $env:AGENTICA_LAB_TEST_URL = 'http://127.0.0.1:5079'
   $env:AGENTICA_LAB_TEST_PROVIDER = 'fixture'
   node --test browser-tests/service-integration.test.mjs
   ~~~

The host start request must use provider: { provider: "fixture" }. The optional model must be fixture-model. Credentials are the fixed, nonsecret string agentica-loopback-fixture; environment provider credentials are never read. Unsupported providers, models, Gemini routing and named reasoning controls fail explicitly. Thought-summary visibility and bounded output/context budgets remain available for custom fixture responses.

## Supply a host-owned deterministic fixture

Replace the Node process with any local server implementing POST /v1/responses. Pass its URL using --fixture-endpoint; no source edits are needed.

The incoming body uses the existing OpenAI Responses client contract:

- model: "fixture-model", stream: true, store: false.
- instructions contains the planner's system instruction.
- input contains user planning prompts and may contain replayed native history. Read the **last user message** to select the current plan.
- The latest prompt contains the permitted tools, observation and completion context. Return an initial plan directly. When it requests "refinedPlan", return { "reason": "observation", "refinedPlan": plan }.
- Step IDs must be new for newly requested actions. Tool IDs, kinds, effects and inputs must match the host's bound capability manifest.

The supplied example returns demo.inspect, then demo.accept for sample-1. Its process counter creates unique IDs; the current prompt determines the operation. It is a labeled inventory fixture, not a general planner or an evaluation of intelligence.

The compact SSE writer in [responses-fixture.mjs](responses-fixture.mjs) demonstrates the required wire shape:

1. Optional response.created activity.
2. Actual response.output_text.delta events containing sequential fragments of the JSON plan/envelope.
3. Required response.completed, with response.status: "completed" and a message output containing the exact complete text.

Delta text must equal the final output text. Missing completion, malformed JSON, redirects and unavailable endpoints fail the run. No fallback or JSON repair request is issued by this fixture factory. The sample emits no synthetic reasoning or token-usage claims.

Fixture qualification proves transport/runtime integration. It does not establish live-provider or production behavior. The calling host still owns canonical state, action deduplication, revision checks, persistence and completion evidence.

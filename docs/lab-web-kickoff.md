# Agentica Lab web kickoff

## Purpose and delivered surface

`Agentica.Lab.Web` is a local ASP.NET Core host for the existing Agentica loop, streaming provider clients and bounded host context. External applications initiate a WebSocket connection, supply their objective and perspective, and execute their bound capabilities against their own canonical state. The service waits for receipted results and continues planning until completion, cancellation, a blocker or a budget limit.

The implementation is domain-neutral. The included browser-owned scoped inventory is a deterministic proving fixture. Maze Battle has adopted the same protocol for an isolated browser expedition: the real browser, SDK, service and streaming fixture completed the first-chest objective, lost-acknowledgment recovery, and takeover followed by a new bounded run. See the [versioned integration evidence](maze-battle-integration-proof.md).

Delivered pieces:

- A local service with health/provider metadata, WebSocket host transport, run snapshots, SSE progress, cancellation and restart custody recovery.
- The actual Agentica planning/validation/execution/completion loop across multiple streamed provider calls and remote actions.
- A browser dashboard with run list, provider readiness, activity/counts, available thought summaries, context/evidence inspection and terminal outcomes.
- A dependency-free ES module host SDK with explicit start/resume/cancel/recovery, persisted action reservation/result hooks and duplicate protection.
- Bounded context projection, durable host observations, corrective facts, model hypotheses and exact retained evidence retrieval.
- A labeled deterministic inventory sample plus .NET, SDK and real-browser qualification seams.

The complete external contract is in [External browser hosts](external-host-browser.md). Its field names and bounds follow the C# DTOs; the source remains authoritative when changing an implementation and its examples together.

## Start locally

Run commands from the repository root. [`global.json`](../global.json) pins **.NET SDK 10.0.302**, disables roll-forward and excludes prerelease SDKs. Use that SDK; changing the pin or compiling under another SDK changes the qualification environment.

```powershell
dotnet --version
dotnet restore Agentica.slnx --locked-mode
dotnet build Agentica.Lab.Web/Agentica.Lab.Web.csproj -c Release --no-restore
dotnet run --project Agentica.Lab.Web/Agentica.Lab.Web.csproj -c Release --no-build -- --urls http://127.0.0.1:5078
```

Open `http://127.0.0.1:5078/`. The default binding is already loopback port 5078 when no URL configuration is supplied. The current kickoff enables cross-origin access for external local applications and does not add authentication. Local transport security and deployment hardening are outside this slice.

The default store is `.agentica/lab-web` under the application's content root, with separate `context` and `custody` subdirectories. Set an explicit isolated directory when running separate fixtures or instances:

```powershell
$env:Agentica__StorageDirectory = 'C:\temp\agentica-lab-proving-ground'
dotnet run --project Agentica.Lab.Web/Agentica.Lab.Web.csproj -c Release --no-build -- --urls http://127.0.0.1:5078
```

Retain the same storage directory across a service restart when reconciling existing effects. A fresh or deleted directory does not establish that earlier effects are absent. Use one service process per storage directory; shared multi-process custody is not qualified by this kickoff.

Keep live storage outside directories that a build or test runner cleans, such as `test-results`, `playwright-report`, `bin`, and `obj`. A fixture launcher can create a dedicated temporary directory outside its report directory and retain that path until the service stops and its original effects are reconciled. Removing the initialized custody ledger while the service is running fences admission; it must not silently recreate an empty ledger or repeat an effect.

`GET /api/health` proves that the process is serving protocol version 1. It does not contact providers or prove that an external host is connected.

## First run without provider credentials

1. Open **Scoped inventory** in the dashboard.
2. Leave **Demo · deterministic fixture** selected.
3. Start the example and inspect the run's Activity, Context, and Actions & outcome tabs.
4. The browser retains the isolated inventory and action records. The service requests inspection, replans, requests acceptance and evaluates host completion evidence.

The sample's expected result is one accepted eligible record, one deferred record, exactly two host calls, no pending action and a successful host-backed outcome. The provider stream is scripted and passes through `LlmWorkflowPlanner`; it is useful for protocol and loop qualification, with no model intelligence claim. Its two tool IDs, `demo.inspect` and `demo.accept`, are sample bindings and are not required by real adapters.

Progress arrives during the provider call. Actions execute only after a complete proposal passes normal validation. The dashboard distinguishes its SSE connection from the external host socket and shows retained progress gaps or truncated payloads. Provider character counters and available thought summaries remain observational; authoritative host results establish effects and fulfillment.

## Provider configuration

The service reads credentials and endpoint configuration from its environment. OpenAI also supports an operator-supplied key held in service memory through the dashboard. Read endpoints return only configuration metadata. Never put a credential in a start payload, browser URL or host observation.

| Provider selection | Service configuration | Streamed API |
| --- | --- | --- |
| `gemini` / `google` | `GEMINI_API_KEY`, fallback `GOOGLE_API_KEY`; optional `AGENTICA_GEMINI_MODEL` | Gemini Developer API Interactions by default; optional streaming GenerateContent |
| `openai` | `OPENAI_API_KEY` or a service-memory key; optional `AGENTICA_OPENAI_MODEL`; Lab default `gpt-6-luna` | OpenAI Responses |
| `anthropic` / `claude` | `ANTHROPIC_API_KEY`; optional `AGENTICA_ANTHROPIC_MODEL` | Anthropic Messages |
| `grok` / `xai` | `XAI_API_KEY`; optional `AGENTICA_GROK_MODEL` | xAI Responses |
| `ollama` | `OLLAMA_MODEL` or an explicit run model; optional `AGENTICA_OLLAMA_ENDPOINT` or `OLLAMA_HOST` | Ollama chat |
| `demo` | None | Scripted streaming fixture |

`AGENTICA_OLLAMA_ENDPOINT` is the full chat endpoint. Otherwise, `OLLAMA_HOST` supplies the base origin and the factory adds `/api/chat`; its final fallback is the existing Ollama client default. The factory accepts HTTPS or loopback HTTP endpoints. Ollama metadata can show `configured:true` with “Select a model when starting a run”; configuration does not prove a reachable Ollama server or installed model.

`GET /api/providers` reports `{provider,defaultModel,configured,configurationIssue,api,streams}`. It makes no provider request. Refer to the returned model defaults rather than treating a document's example model name as current provider availability.

Per-run settings are `provider`, `model`, `thinkingEffort`, `maxOutputTokens`, `contextWindowTokens`, `includeThoughtSummaries`, and `geminiApi`. The default provider is Gemini; the browser sample deliberately selects demo. See the contract document for exact limits. Unsupported provider/model reasoning controls can fail closed in the underlying adapter; setting a generic effort does not promise identical behavior across models.

The default application context budget is 131,072 tokens. The current estimator conservatively counts UTF-8 bytes as tokens; the default accommodates the mandatory runtime/tool contract plus a scoped observation. This budget is an application limit, not discovery of a model's actual capacity. Hosts targeting a smaller model must choose a compatible budget; compilation fails before dispatch if mandatory context cannot fit.

### OpenAI Luna preparation

Under **Providers → OpenAI configuration**, select the model and reasoning effort, then **Save settings**. The Lab defaults to `gpt-6-luna` with **High** reasoning. `AGENTICA_OPENAI_MODEL` remains the startup model override. A typed API key replaces the environment credential for new OpenAI planners; an empty key keeps the current credential. **Use service environment key** discards the service-memory override. Model, effort and key overrides reset when the service restarts. Active planners retain the settings and credential captured when they were created.

Saving settings makes no provider request. `configured` means a credential is available; account access, model availability and successful live execution remain unverified until a run is deliberately started. The browser clears the password after submission and does not persist it. The service returns no credential bytes and writes no settings or keys to its context/custody store.

The sample remains on **Demo** by default. Select **OpenAI** to see the model and per-run reasoning controls. For an external host's first Luna test, use the existing start field:

```json
"provider": {
  "provider": "openai",
  "model": "gpt-6-luna",
  "thinkingEffort": "high",
  "includeThoughtSummaries": true
}
```

An omitted model uses the configured service default. An omitted effort inherits the service effort when using that configured model; an explicit different model does not inherit it. Explicit per-run efforts, including `none`, take precedence. The Lab settings form exposes Luna's `none`, `low`, `medium`, `high`, `xhigh`, and `max` efforts plus **Provider default** to omit the control. A custom startup `AGENTICA_OPENAI_MODEL` retains omitted reasoning until an operator selects an effort. Other model names remain operator choices and may reject unsupported controls.

[OpenAI's Luna documentation](https://developers.openai.com/api/docs/models/gpt-6-luna) confirms High effort on Responses. It is a named effort, not a numeric thinking-token budget. The adapter uses `stream:true`, `store:false` and private encrypted continuation within each planner. Available provider summaries may appear in telemetry; encrypted carriers remain private. See [stateless reasoning](https://developers.openai.com/api/docs/guides/reasoning#preserve-reasoning-without-stored-responses).

This configuration slice prepares the initial live test. It does not qualify NS-6 temporal continuity, long-distance travel or the Maze Battle PR under review.

### Stateless Gemini and reasoning custody

The web service uses `GeminiInteractionsLlmClient` with streaming and `store:false`. Agentica supplies context and privately retains the native continuation needed within that planner session. It does not depend on a stored provider conversation to resume the run. Provider-native thought signatures and opaque continuation remain private to the planner/client path; the UI receives only the supported summary/telemetry projection.

Interactions is itself a Gemini Developer API path. Set `geminiApi:"generatecontent"` (or its `"legacy"` alias) to select the separate Developer API `streamGenerateContent` SSE transport. The sample UI exposes this choice for Gemini. Both routes stream during the call and retain transport-specific native signatures privately; neither reuses the other route's continuation. See [GenerateContent streaming](gemini-generate-content-streaming.md) for supported controls and qualification. Vertex routing is not exposed by the web factory.

Each run gets a fresh planner. A browser disconnect within the same process can reattach to a retained run. A service process restart preserves durable context/effect custody where recorded, while provider conversation and in-memory runtime continuation are lost.

## Effects, cancellation and restart operations

The host's supplied capability manifest is the run's standing binding. Host rules and revision fencing still determine whether an individual operation is legal. Keep one authoritative writer or an equivalent transactional revision guard for each host session.

The reliable action path is:

```text
validated runtime tool call
  -> durable original-attempt custody
  -> awaited action.request
  -> host reservation
  -> authoritative host effect + exact result
  -> action.result
  -> runtime receipt / fresh context / completion evaluation
```

SSE and WebSocket progress do not dispatch effects. A slow display can lose progress without duplicating an action. Every result binds the original action/session/epoch and world revision. Repeated matching results are recognized; changed identities or contents are rejected.

Cancellation stops future work. An already dispatched effect may still require its original result. A timeout, disconnect, absent run object or changed epoch does not prove the effect failed. Inspect `pendingActions` even when the run status is terminal.

Host consumers handle both normal `outcome` and reliable `run.terminated` messages. Exceptional provider setup/runtime failures retain a `{status,code,message,snapshotUrl}` termination record and replay it on resume. They can leave the normal outcome null; the dashboard shows the termination and stops its activity indicator without manufacturing runtime completion evidence.

For a retained run, reconnect with `resume` and answer `action.reconcile` from stored results or inspected authoritative state. The SDK never invokes a capability while handling reconciliation. For a lost process, query `GET /api/recovery` and resolve the durable original action with `POST /api/recovery` or `host.recover()`. The restart fence applies to the host/session across epochs. HTTP resolution is rejected while the live run is retained so that the result reaches its original runner over WebSocket.

A missing or pending host record stays unresolved until there is evidence for the original effect. Hosts should atomically commit the effect and its result when possible. The browser sample uses Web Locks and one localStorage state/result write; production adapters should use their own authoritative transaction facility. The service's restart fence does not turn this into a general distributed exactly-once transaction protocol.

## Context and domain adaptation

The host observation boundary is where perspective is enforced. For a spatial simulation, this means visible or already discovered topology, lawful interactions, bounded actor state and explicit unknowns. Never send the omniscient world object simply because the renderer currently holds it.

The service stores generic facts and source references. It can support learned maps, resource knowledge, corrections and hypotheses through the same schema. The host supplies observed/supportable facts; the model can record inferred, refuted or stale hypotheses through `lab.hypothesis.record`. Those hypotheses cannot promote themselves into host truth or completion.

The frame is bounded and can omit values. `lab.knowledge.query` retrieves bounded current knowledge pages, and `lab.evidence.read` retrieves exact retained source observations. Source hashes, omission counts, pruning counts and availability prevent an omitted or pruned source from becoming implied knowledge. An expired cursor requires restarting the query against the changed knowledge snapshot.

Planner JSON sections use compact serialization. This reduces formatting overhead while preserving the objective, capability schemas, completed-step identities and exact newest evidence. Both character and estimated-token ceilings still apply. A scripted provider fixture should parse each JSON section and use evidence references when a frame is compacted; whitespace is not a data contract. If mandatory content still exceeds the budget, the run reports a planning failure rather than silently dropping required evidence.

A first external integration should prove:

1. One host-owned objective and permitted perspective.
2. Multiple streamed provider/planning calls under one run.
3. A legal operation and at least one refused/conflicting operation.
4. Fresh observations, accumulated bounded knowledge and a correction or explicit unknown.
5. Disconnect/reconciliation without a second effect.
6. Host-backed completion or an honest blocked/budget outcome.

Domain-specific traversal, combat, resource policies and acceptance tests stay in the host adapter. The current service is independent of an MCP transport and does not require the host to install an MCP server. Capability vocabulary can be projected through an MCP adapter later if useful; that integration remains separate work.

## Qualification commands and evidence

Use the pinned SDK from the repository root:

```powershell
dotnet test Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj -c Release --no-restore
node --test browser-tests/host-sdk.test.mjs browser-tests/host-recovery.test.mjs browser-tests/host-result-validation.test.mjs
```

The .NET suite owns provider factory seams, host context, reliable remote execution and service boundaries. Read the current test output for the exact number and outcomes; a test name alone is not a pass claim.

For the opt-in real WebSocket/service test, start an isolated service first, then:

```powershell
$env:AGENTICA_LAB_TEST_URL = 'http://127.0.0.1:5078'
node --test browser-tests/service-integration.test.mjs
```

This test always selects `provider:"demo"`. Without `AGENTICA_LAB_TEST_URL`, the test is explicitly skipped. Node 22 supplies the WebSocket/fetch APIs used by the integration test.

The browser smoke requires a locally installed Playwright package and Chromium:

```powershell
$env:AGENTICA_LAB_TEST_URL = 'http://127.0.0.1:5078'
# Optional when Playwright is installed outside this repository:
$env:AGENTICA_PLAYWRIGHT_MODULE = 'file:///C:/path/to/node_modules/@playwright/test/index.mjs'
$env:AGENTICA_BROWSER_ARTIFACTS = 'browser-tests/artifacts'
node browser-tests/ui-smoke.mjs
```

No frontend package build is required: the dashboard uses static HTML, CSS and ES modules served by ASP.NET Core. Screenshot files under `browser-tests/artifacts` are Git ignored.

### External deterministic planner fixture

Use the [loopback fixture launcher](../samples/Agentica.Lab.Web.FixtureHost/README.md) to connect a host-owned deterministic planner to the real service without provider calls or service source edits. It injects the existing streaming Responses client through `LabWebApplication.Create`, accepts an explicit loopback HTTP fixture endpoint, and disables redirects/proxies. The included Node fixture demonstrates the SSE protocol with the isolated inventory. Other hosts supply their own observed-state planning fixture behind that endpoint.

Responses input may contain retained earlier user and assistant messages before the current user prompt. A scripted fixture must select the last user message, require its planning sections, and derive both the frame and refinement evidence from that same message. Do not scan arbitrary strings or fall back to an older prompt if the current message is malformed. The included fixture follows this ordering. When a frame is compacted, first check the current action-result observation before requesting a retained-source read; see [context evidence paths](external-host-browser.md#bounded-context-and-learned-knowledge).

The actual browser SDK → service → loopback HTTP SSE fixture → planning/refinement → host action/receipt path passed locally. This additionally exercises the real planner session's default input budget, which the built-in demo does not configure. The integration test accepts only `demo` or `fixture`; it rejects a live provider selection.

Recorded focused evidence from this implementation session:

| Evidence | Observed result | Qualification boundary |
| --- | --- | --- |
| Lab web .NET suite | 99 passed | Service/context/custody/provider-factory seams, sourced thought tests, telemetry correlation, compaction and later-run evidence retrieval; missing/corrupt initialized storage is identified before run admission |
| Focused provider client suite | 119 passed | Includes 20 GenerateContent streaming cases, eight refinement-prompt evidence/accounting cases and two structured-context cases retaining 96 completed steps and exact nested evidence within unchanged budgets; no live provider calls |
| Aggregate Agentica suite | 752 passed, 7 skipped | Core and provider-client regression scope; seven opt-in live-provider checks remain deferred; workspace search also rejects an elapsed deadline when its timer callback is delayed |
| Browser host SDK seam suite | 27 passed | In-process protocol, bounded queues, exact deduplication, reservation ordering, deadline/cancellation after slow persistence, local-only callback diagnostics, disconnect, late durable results, explicit state reconciliation and exceptional terminality |
| Restart recovery SDK suite | 8 passed | Mocked recovery HTTP; original identity/fingerprint, no invocation, persistence before resolution, live-run refusal and prior unresolved record recovery |
| SDK result validation | 33 passed | Revision, observation and completion constraints before completed persistence, including retained results, WebSocket reconciliation and HTTP recovery; invalid results cannot cause another effect |
| Real SDK to refreshed local service | Passed | Actual WebSocket, scripted provider stream, inspect → replan → accept, two retained results, zero pending actions and succeeded outcome |
| External HTTP streaming fixture | Passed | Actual browser SDK → service → loopback HTTP SSE fixture → planning/refinement → host actions/receipts; deterministic fixture only, with no provider API call |
| Browser UI smoke on refreshed service | Passed | Deterministic sample, context inspection, progress counters, disabled terminal cancellation, no page exceptions and no horizontal overflow at 390 px |
| Browser exceptional termination | Passed | Explicitly unconfigured OpenAI factory rejected before provider network execution; reliable `run.terminated`, null normal outcome, snapshot failure, zero host actions, stopped activity and no page exceptions |
| Desktop/mobile screenshot review | Visually inspected | Layout and readable execution state; screenshots are local artifacts |
| Gemini transport controls | Passed | Real browser with intercepted HTTP and WebSocket; Interactions default, optional GenerateContent, no Gemini field on other providers, zero live calls |
| Maze Battle isolated integration | Three browser scenarios passed | Real SDK/service/provider-fixture loop, same-call live text, refusal and first chest across three spaces, exact result replay, and takeover with exact remembered-cell recovery in a later run; see the versioned proof |

These observations do not establish live-provider parity, ordinary saved-game adoption, full process-restart continuation, production deployment readiness or a benchmark of model reasoning. Provider fixture seams and installed/live-provider execution are separate proof levels. Subsequent changes should rerun the affected focused gate and report its actual result.

The SDK suites total **68 passed**. `node browser-tests/ui-termination-smoke.mjs` reproduces the exceptional browser path only when provider metadata reports an explicitly unconfigured remote provider; it stops before creating a run if none is available. Its purpose is to qualify reliable local setup-failure reporting, with no provider API call.

`node browser-tests/ui-provider-controls.mjs` verifies the Gemini transport controls with fully intercepted fixture traffic. The same Playwright module override applies. Refinement prompt `workflow-plan-refinement-prompt-v3` renders the newest observation once, uses compact JSON, preserves inclusion decisions and rejects conflicting reuse of an observation identity. The two-run context fixture forces real prompt compaction, reopens the scoped store with a fresh view, and retrieves the original exact evidence through the planner/tool loop. No model intelligence or speed benchmark is inferred from these deterministic checks.

`node browser-tests/ui-provider-settings.mjs` qualifies OpenAI settings and per-run reasoning through intercepted HTTP/WebSocket traffic: credential set/keep/environment, clearing on failure, no key in browser storage or run payloads, preserved edits, stale refresh handling, and mobile layout. It makes no paid calls. `OpenAiSettingsTests` qualifies the real settings HTTP endpoint, metadata redaction, validation atomicity, process lifetime and captured per-planner credentials. The Responses replay regression uses Luna/high on both turns and checks that opaque reasoning never reaches stream telemetry.

## Subsequent integration slices

- Additional external hosts: their own scoped observation compiler, capability mapping, authoritative result persistence, revision checks and takeover rules.
- Maze Battle ordinary saved play after single-writer ownership, autosave coordination and reload/reconciliation qualification; combat, inventory, quests and town expand separately.
- Live-provider qualification selected explicitly by the operator, including model/effort support and actual stream behavior.
- Richer nested schema contracts and longer-lived durable run continuation as distinct follow-on slices. The initial sourced prediction/counterevidence exercise is described in [Lab thought testing](lab-thought-testing.md).

The initial service, browser surface and reusable adapter seam were qualified before Maze Battle adopted the contract. The [cross-runtime proving-ground report](maze-battle-integration-proof.md) records the resulting browser/service evidence and lessons. The separate omniscient baseline and standalone host planner are not used as Agentica integration proof.

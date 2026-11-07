# P07 — Lab adoption of shared provider contracts

Source base: `2c76f0a` (P01–P06 integration). Branch: `codex/lab-shared-provider-adoption`.
Implementation commit: `b6d587b32943a4a01c8c5f21c098e0e1dbfdce10`.
Synthetic disposable state only. No existing preview restart, saved-chat modification,
production access or paid provider work was performed by this lane.

## Implemented

- Direct chat, actual harness planner/repair and isolated explicit search use
  `ExperimentPreparedClient` → shared `LlmCallPreparation` / `LlmPreparedCall`.
  The required Lab admission measures that immutable full request, checks limits,
  journals its descriptor and admits one exact body before inference transport.
  A request cannot enter the Lab forwarding handler without that reservation.
- The Lab input counter is a turn-owned exact-body cache over shared
  `OpenAiInputTokenCounter`, including its separately admitted count operation.
  Validation precedes cache lookup; a detached owned body prevents caller mutation
  from separating the measured bytes and cache identity. Count failures remain
  fail-closed. Noncaller count timeouts retain the existing safe Lab error contract.
- Explicit `OpenAiHistoryMode.TranscriptOnly` preserves ordered visible user/assistant
  roles and exact nonempty instructions, including whitespace. It rejects native
  continuation and returns none. Native mode defaults and other provider guards
  remain strict. Configuration identity reports the selected history mode.
- The raw complete-body count bridge shares the existing counter serializer,
  validation, admission and retained-outcome implementation; it does not generate
  a turn. Host scope, full generation body hash, generation references and separate
  count references bind the operation.
- Shared terminal/search parsing replaces Lab's competing `DirectState` parser.
  Ambient and explicit search retain their independent bindings, limits and query
  isolation. Canonical answer text and bounded safe sources feed the existing
  Markdown/citation UI.
- Native source records remain a redacted inspection projection. Optional observer
  failure or an oversized private inspection event cannot discard a shared terminal
  outcome. Strict optional decoding also cannot reject a comment ignored by the shared parser. Inspection omissions are flagged; required request-journal failure
  prevents inference. Default owned HTTP clients disable automatic redirects.
- Saved turn usage includes bounded sanitized attempt records on every observed
  terminal path. Known provider usage/status survives late failure and cancellation;
  dispatched unresolved work stays unresolved, with no automatic retry. Typed search outcomes remain separate from structural evidence, survive failure/cancellation, and share a bounded 1 MiB per-turn snapshot allowance; original counts and host-retention truncation identify omitted detail. A new
  **collapsed** attempts/recovery inspector shows saved evidence. The compact usage
  label distinguishes undispatched attempts, known counters and unknown usage,
  without adding thinking tokens to inclusive output a second time.
- The real runner still selects a bound utility, records its result, refines and
  emits a receipt-backed response artifact. Its host outcome exposes completion
  evidence, blockers and remaining response obligations.

## Compatibility and explicit changes

Conversation settings, storage schema, drafts, Markdown source, message IDs,
conversation isolation, recall-off/personal-memory-off defaults and trace expansion
state are unchanged. Harness native state is still run-local; reopening uses visible
transcript history. Exact-history mode is opt-in and does not weaken default native
continuation guards or silently apply to another provider.

Quoted recall evidence now occupies a dedicated user input immediately before the
unchanged current user input, rather than two `input_text` parts in one user item.
The original source author is retained inside the attributed quotation. Complete
request compilation/counting and recall fixture readers use this same new shape.
No content is promoted to system authority or another conversation.

Shared Responses validation is the single source of terminal acceptance. In
particular, a late error after provider `completed` keeps its observed usage/status
and fails the turn; a completed provider stream is not accepted prematurely.

## Evidence

Pinned SDK: `10.0.302`, workspace-local CLI home and package cache.
Inherited live flags explicitly disabled. Restore used locked dependency files.

| Gate | Observation |
| --- | --- |
| Client regressions | **506/506 PASS**, including exact prepared report identity, raw count/stateless negatives and transcript guards |
| Initial full Lab aggregate | 600 pass, 1 skip, 1 old recall-input-shape assertion; corrected from 9 combined inputs to 10 separately attributed inputs |
| Full Lab aggregate | **602 PASS, 1 platform SKIP, 0 FAIL** before final review corrections; includes the 5000-turn retention baseline |
| Affected Lab host suite | **169 PASS, 1 platform SKIP**, excluding only the unchanged storage baseline; final review additions separately **29/29 PASS** at the implementation above |
| Chat UI | 30/30 pass, including retained failed/unknown usage and existing Markdown/source behavior |
| Required Windows/Linux CI | Pending root PR integration |
| Browser candidate and live OpenAI | Root P09 gate; not claimed by this lane |

Reproducible commands (use the pinned executable and local environment):

```powershell
dotnet restore Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj --locked-mode --configfile NuGet.config
dotnet test Agentica.Clients.Tests/Agentica.Clients.Tests.csproj --configuration Release --no-restore -- RunConfiguration.TreatNoTestsAsError=true
dotnet test Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~ExperimentExecutorTests' --logger 'trx;LogFileName=p07-review-regressions.trx' --results-directory .agentica/verification/p07 -- RunConfiguration.TreatNoTestsAsError=true
dotnet test Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj --configuration Release --no-restore --logger 'trx;LogFileName=p07-lab-final.trx' --results-directory .agentica/verification/p07 -- RunConfiguration.TreatNoTestsAsError=true
node --test Agentica.Lab.Web.Tests/chat-ui.test.mjs
```

Focused host regressions include a late error after completed usage, cancellation
only after the shared parser has consumed the terminal record, a required journal
failure before POST versus an optional inspection failure, a 1.1 MB private carrier
omitted from display while preserving known terminal usage, invalid UTF-8 inside an ignored SSE comment, retained typed search after inspection failure, bounded saved source exports, the actual clock tool
→ observation → refinement → accepted response loop, explicit search isolation,
repair/call ceilings, persisted chat/recall and explicit public DI composition.

This establishes deterministic provider/host behavior. It does not establish model
judgement, live account capability, Bridge installation or production recovery.


## Review corrections and learning

Independent source review found that structural attempt capture alone did not retain
external search output on failed/cancelled turns when native inspection was absent.
The adapter now exports a separate typed, attributed search snapshot with explicit
host retention coverage. A second finding showed that a strict optional SSE
inspection decoder could change a valid shared outcome; inspection now becomes
unavailable while the provider parser receives the original bytes. Both have
host regressions in the final 29-case run.

A diagnostic fallback experiment also briefly replaced specific provider classes
with the generic structural `client_failure` code. The focused run caught two
regressions; that fallback was removed. Structural reason codes remain evidence,
while the specific client/provider error retains its existing display contract.

The accepted reusable lesson is recorded in
`.agents/learning/records/agentica-optional-inspection-cannot-own-provider-outcome.json`.
Its proof is deterministic host behavior at the implementation pin, not live or
installed acceptance. No shared/global memory store was modified.

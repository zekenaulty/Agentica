# Experimental chat qualification — 2 November 2026

## Subject and boundary

Branch `codex/lab-clean-room-chat`, isolated worktree `C:/Users/Zythis/.codex/worktrees/lab-clean-room-chat/Agentica`, base `977d0cab5421fd4e09fd33f1224f241b7e425abe`. The primary dirty checkout and existing Lab service on 5078 were preserved. Nyx/Bridge was inspected read-only at `187b203c45d316e8f166dcb1cbd239e5a564a321`; no runtime/participant database was opened.

The source brief was read and refreshed through native Google Docs. Title: Agentica Lab — Clean-Room Experimental Chat and Instrumented Harness — Refined Implementation Brief — 2026-11-02. Observed revision unchanged at final reconciliation:

`AHj4eMTiwICPnZn5Fo4Q1IUtC2ae9eTnmg9-XOSp-oE1sYy4UbSPfos_mnjJV-JbHKw-t-rNYvHBpL9aZh-_IAnreZA7EJJUxaEZPzmGJ5Q`

See the [implementation map](../../lab-experimental-chat.md) for ownership, source URL, schema, capabilities and limits. Official Responses conversation-state/reasoning guides were read; stateless transcript replay is explicitly distinguished from retained native reasoning.

## Commands and observed results

The repository pins SDK **10.0.302** with roll-forward disabled; this machine has **10.0.401** and 9.0.318. Verification ran from the sibling `verification/` directory so the installed 10.0.401 SDK was explicitly selected without changing `global.json`. This is alternate-SDK evidence, not qualification of the pinned SDK or GitHub CI.

```text
dotnet restore <worktree>/Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj --force-evaluate --verbosity minimal
dotnet test <worktree>/Agentica.Lab.Web.Tests/Agentica.Lab.Web.Tests.csproj -c Release --no-restore --verbosity minimal
node --test Agentica.Lab.Web.Tests/chat-ui.test.mjs
git diff --check
```

- Restore passes. SQLite dependencies match the existing CLI donor versions, including the newer native bundle override. Web, test and fixture lockfiles are updated.
- Full Lab Web Release suite after search integration: **200 passed, 1 skipped, 0 failed**, total 201, about 15 seconds. Build has no warnings. The skip is the symlink-creation case when Windows cannot create a symbolic link; traversal/device/stream/root-scope tests still execute. Final result: `verification/test-results/lab-chat-search-final.trx` outside Git. The earlier initial-feature result was 186 passed and 1 skipped.
- Core Release suite after the test-package correction: **752 passed, 7 live-provider tests skipped**, zero failures (`core-after-xunit.trx`). Focused provider suite: **119 passed**, zero failures (`clients-after-xunit.trx`).
- Focused chat JavaScript: **16 passed**, zero failures. The unchanged host SDK's four JavaScript suites separately pass 83 cases; both CI jobs now include the chat suite.
- Full solution Release build: zero warnings/errors. Full solution formatting verification passes on SDK 10.0.401; formatting required a temporary local pin for MSBuild discovery, restored byte-for-byte afterward. No SDK pin change is committed.
- `git diff --check`: passes.

## What the tests establish

| Claim | Evidence level / observation |
| --- | --- |
| Uncontaminated baseline | Fake HTTP captures actual outgoing request: `store:false`, no tools, instructions, persona, planner schema or cross-chat content; exact optional instructions and model/effort mapping |
| Restartable dialogue | Real SQLite and restarted WebApplication restore title, messages, settings and traces; canonical user/assistant replay; native-continuity limit retained |
| Atomic/custodied persistence | Settings conflicts, request-ID replay, transaction failure rollback, service lease and failed-schema lease recovery; running restart becomes interrupted without dispatch replay |
| Scope and privacy | Separate conversations cannot read each other's turn payloads; memory enabled is refused; unrelated browser Origin refused; key/private-carrier removal tested |
| Actual Harness | Real AgenticaRunner plus fake provider HTTP performs query, canonical result/observation, refinement and response-artifact completion; plan/outcome evidence inspected |
| Admission and recovery | Complete wire budget checked before network; repair call cannot exceed provider-call ceiling; default external-host factory behavior preserved; no accidental injected global factory in production chat composition |
| Context and evidence | Explicit stop/recent selection, complete-pair omission, current observation retention and cumulative evidence limits; canonical outcome proof is not deleted |
| Streaming and source fidelity | Summary/text/item correlation, original native type/indices/IDs, source-before-normalized order, incomplete stream, partial cancellation, no blind retry, usage and terminal status |
| Capabilities | Nine actual typed tools; binding/provenance/effects, arithmetic rejection, JSON pointers, bounded Unicode/escaped results, transcript isolation, sandbox text create/read/list and path restrictions |

## Browser evidence

The optional sample fixture ran on **5090**, with no key and default model `fixture-model`. It supplies synthetic provider-like events through the actual Lab APIs/SQLite/SSE. It makes no provider requests and is not itself a real Harness or model proof.

```text
AGENTICA_LAB_TEST_URL=http://127.0.0.1:5090
AGENTICA_EXPERIMENT_FIXTURE=1
AGENTICA_PLAYWRIGHT_MODULE=file:///C:/Users/Zythis/source/repos/maze-battle/node_modules/@playwright/test/index.mjs
AGENTICA_BROWSER_ARTIFACTS=<task-verification-directory>/browser
node browser-tests/ui-experiment-chat.mjs
```

Independent UI worker executed the real REST/browser flow and visually inspected screenshots:

- Create, save, rename, reopen and new default clean room; no browser API mocks.
- Exact imported CRLF/CR instructions survive unrelated settings edits.
- Model/effort/context controls; explicit Harness switch, tool bindings and revocation on returning to Clean Room.
- Partial output observed while running, then saved transcript, summary, context, request, native items, normalized timeline, usage and terminal reload.
- 390 px and 320 px viewports have no horizontal overflow; composer remains visible. Zero browser page errors.
- A separate narrow check loads an initially unmaterialized provider-record payload only after explicit inspection, preserves original type/metadata and excludes that source record from the normalized timeline. Exactly one lazy payload GET observed.

Artifacts remain outside Git under `C:/Users/Zythis/.codex/worktrees/lab-clean-room-chat/verification/browser/`: desktop/mobile/trace/provider-source PNGs and `provider-source-qualification.json`. Root additionally inspected the trace screenshot. Fixture conversations are isolated from the normal preview store.

The model-picker browser check uses account-visible IDs with intercepted synthetic chat state: all eight current GPT 5.6+ family IDs are present, selection preserves instructions/effort, and no real conversation or inference is touched. Evidence: `model-catalog-qualification.json` and `experiment-chat-model-catalog.png`.

The search browser check uses the actual credential-free 5090 service and SQLite/SSE: off/default-3, saved ambient enable/budget/reload, clickable assistant citation with unchanged raw text, restored citations without expanding trace, explicit Harness binding, tool-source separation, visible native-source trace, and clean defaults in a new chat. Two synthetic turns, zero real-provider calls, zero page errors. Evidence: `web-search-qualification.json` and `experiment-chat-web-search.png`; root inspected the screenshot. Unicode/invalid annotation offsets retain safe source links without guessing inline positions.

## OpenAI search qualification

Official [web-search guide](https://developers.openai.com/api/docs/guides/tools-web-search) and [Responses create reference](https://developers.openai.com/api/reference/resources/responses/methods/create) were consulted. Actual outgoing-request fixtures establish:

- No search surface by default; explicit ambient opt-in adds native `web_search`, `tool_choice:auto`, sources inclusion and `max_tool_calls` without an instruction overlay.
- The actual Harness performs plan → query-only dedicated search → untrusted result → refinement → response artifact. Neither conversation history nor custom instructions enter the search subrequest, and the research answer is not appended to the assistant transcript.
- Search subrequests share the parent provider-call guard; native-search allowance decreases across multiple searches, with unresolved reservations retained on an incomplete attempt.
- Required-but-absent search, unsupported unbound native output, citation URL handling and metadata are tested. Citation-only retrieval remains conversation-scoped and survives a real service restart.
- Utility binding/input/output quotas, invalid evidence, cancellation, safe error codes and no automatic retry are tested.

The first integrated search run had three `PlanInvalid` failures: an external-effect tool was described as `ToolKind.Query`. Existing core validation requires `Action` for any non-read-only effect. Descriptor and fixtures now use `Action`, retaining external-effect accounting and existing validation. Focused executor tests then passed 21/21, followed by the full 200-pass Web result above. Independent read-only review found no further actionable defect in supported search paths. No paid web retrieval was performed for qualification.

## Corrections made during qualification

Initial focused run: 47 passed, 2 failed, 1 skipped. Found/fixed:

1. Store registered as an externally created singleton was not disposed by DI, retaining its lease after restart. Factory registration now gives DI ownership; restart test passes.
2. Intended planner JSON repair appended native-incompatible generic assistant history before reaching the HTTP guard. A narrow, default-preserving Lab factory option lets chat use the existing stateless repair contract; the second actual request is refused at the call ceiling. The following compatibility fixture initially expected the wrong public exception wrapper and was corrected without relaxing its behavior assertion.

Independent review also found and corrected optional DI planner injection bypassing the observed chat HTTP path, constructor lease cleanup, transactional turn admission, double timeout ownership, snapshot/output event race, textarea line-ending normalization, duplicate native summaries and an incorrect incomplete-trace display after complete SSE coverage. Retained original failures do not imply final acceptance failed.

Final read-only review additionally checked early workspace-initialization lease cleanup and idempotent cancellation racing terminal disposal. Both were corrected. The advertised tool-result ceiling was aligned to the actual 12000-character utility/page ceiling in server validation and UI; an oversized-setting regression is exercised.

## Initial GitHub CI and dependency correction

[CI run 41536490870](https://github.com/zekenaulty/Agentica/actions/runs/41536490870) checked PR #11 head `3e3b9a45e7a0d6cbf53cd264344d679812b101cc` through merge ref `f4121b1133f504897e3a48ed7e2cc7245cfffd64`, against base `977d0cab5421fd4e09fd33f1224f241b7e425abe`.

- Windows job `125589522151`, “Windows locked build and filesystem proof”: **success**, using pinned SDK 10.0.302. This is evidence for that original PR head.
- Linux job `125589522722`, “Locked build, proof tests, and package validation”: **failed before tests** in “Reject vulnerable or deprecated dependencies.” The vulnerability audit reported **clean**. The subsequent deprecation audit reported two transitive findings, in `Agentica.Lab.Web.Tests` and `Agentica.Tests`.
- Exact package: `Microsoft.Testing.Platform.MSBuild` **1.9.1**, marked **Legacy** by NuGet. The dependency path is `xunit.v3` 3.2.2 → `xunit.v3.mtp-v1` → `xunit.v3.core.mtp-v1` → that MSBuild package. The focused `Agentica.Clients.Tests` project uses the same selection and requires the same correction before its separate audit.

The source correction selects **`xunit.v3.mtp-off` 3.2.2** in the three test projects. xUnit remains at the same version; this selects its supported VSTest-only packaging for the runner already used by CI. `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, the coverage collector, test populations and all workflow/audit gates remain configured. The project files explain the selection. Lockfiles must be generated by restore.

Primary references: [NuGet deprecation metadata for MSBuild 1.9.1](https://www.nuget.org/packages/Microsoft.Testing.Platform.MSBuild/1.9.1), [xUnit documentation on platform selection and separate VSTest support](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform), and [the same-version supported package](https://www.nuget.org/packages/xunit.v3.mtp-off/3.2.2).

Local correction evidence: regenerated solution/focused restores pass; both vulnerability and deprecation audits are clean for the solution and focused provider project. The changed package graph passes the core, provider and Web test populations listed above. The original Windows success qualifies only the original PR head; a new GitHub CI head remains separate evidence.

Follow-up [run 41542375896](https://github.com/zekenaulty/Agentica/actions/runs/41542375896), head `909dbfccd87650a641d6abe59e0193dc8ba04095`: Windows **success** on the pinned SDK. Linux passed locked restores, dependency audits, formatting, builds, security analyzers, provider/Web/host tests, core coverage, package validation and container build/smoke. Its final secret scan flagged one **unverified URI** in `chat-ui.test.mjs:19`: the synthetic user-info URL used to test citation rejection. The test now constructs the same invalid URL at runtime and still asserts rejection for username-only and username/password cases. No detector/path exclusion or gate suppression was added. The unmerged feature commit was amended to remove that fixture literal from the PR history; a fresh CI run qualifies the replacement head separately.

## Context admission incident correction

Read-only inspection of the operator's affected chat confirmed saved settings and the form agreed: configured window 131072, output reservation 64000, safety reservation 4096, recent-history omission enabled. The resulting conservative input allowance is 62976 units. The current draft has 62427 UTF-16 characters and 62441 UTF-8 bytes; the previous content estimator added 1024 even with no retained history, exceeding that allowance. This is a local conservative proxy rejection, not evidence of actual provider token-window exhaustion or a concurrent writer. The draft was not submitted or modified during diagnosis. No private message content is copied into source, fixtures or this report.

Two defects were corrected:

1. The browser appended concurrency/refresh advice to every HTTP 409. It now keys revision advice to `settings.conflict`; context limits, quotas, busy state and request-identity conflicts retain their actual messages.
2. Direct history selection counted unescaped content, while final admission counted serialized JSON. They now share the exact direct request builder, serialized character count and UTF-8-byte-plus-512 proxy. `recent` removes complete pairs against that same gate. The current input is preserved; no heuristic token conversion, automatic summarization or new memory/context feature is introduced. Harness projection keeps its existing path.

Verification: full Lab Web Release **206 passed, 1 Windows symlink skip, 0 failed**, total 207 (`verification/test-results/lab-context-bug.trx`). Six new synthetic regressions cover the size-shaped oversized current draft, escaping overhead, complete-pair omission, preflight/dispatch equality, full settings fidelity and unchanged Harness projection. Focused JavaScript **17 passed**. Independent read-only review found no actionable regression in supported paths.

The isolated intercepted browser regression proves autosave before rejection, accurate context-limit advice, preserved draft/request ID, retry with revised settings, a single accepted turn, clearing only after success, and genuine settings-conflict handling. Zero user-chat/service/provider calls and zero page errors. Evidence: `verification/browser/send-recovery-qualification.json`, `experiment-context-limit-recovery.png` and `experiment-settings-conflict.png`. Local diagnosis reads operator metadata/trace only; the failed send has no new admitted turn.

## Explicit limits

- No paid/live inference, real-provider answer quality, account-specific model/effort compatibility, production deployment or Nyx migration is claimed.
- Native opaque reasoning is not persisted between turns or process restarts; replay is visibly transcript-only. No private chain of thought is displayed.
- No cross-conversation memory, Google/media/project/Cloud faculties, arbitrary HTTP fetch, generic shell, MCP server, fork/comparison UI or automatic compaction is introduced. OpenAI search is an explicit opt-in as qualified above.
- Scratch is an application-scoped namespace with path/link checks; it is not OS process isolation against a malicious same-user native process.
- Pinned SDK 10.0.302 passed the original PR head's Windows CI job; the revised package graph and complete remote CI remain pending as described above. The existing host regression suite passed locally on 10.0.401.

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
- Full Lab Web Release suite: **186 passed, 1 skipped, 0 failed**, total 187, about 16 seconds. Build has no warnings. The skip is the symlink-creation case when Windows cannot create a symbolic link; traversal/device/stream/root-scope tests still execute. This is the full relevant Web suite, not the entire repository test estate. The final repeat retains `verification/test-results/lab-chat-release.trx` outside Git.
- Focused JavaScript: **9 passed**, zero failures.
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

## Corrections made during qualification

Initial focused run: 47 passed, 2 failed, 1 skipped. Found/fixed:

1. Store registered as an externally created singleton was not disposed by DI, retaining its lease after restart. Factory registration now gives DI ownership; restart test passes.
2. Intended planner JSON repair appended native-incompatible generic assistant history before reaching the HTTP guard. A narrow, default-preserving Lab factory option lets chat use the existing stateless repair contract; the second actual request is refused at the call ceiling. The following compatibility fixture initially expected the wrong public exception wrapper and was corrected without relaxing its behavior assertion.

Independent review also found and corrected optional DI planner injection bypassing the observed chat HTTP path, constructor lease cleanup, transactional turn admission, double timeout ownership, snapshot/output event race, textarea line-ending normalization, duplicate native summaries and an incorrect incomplete-trace display after complete SSE coverage. Retained original failures do not imply final acceptance failed.

Final read-only review additionally checked early workspace-initialization lease cleanup and idempotent cancellation racing terminal disposal. Both were corrected. The advertised tool-result ceiling was aligned to the actual 12000-character utility/page ceiling in server validation and UI; an oversized-setting regression is exercised.

## Explicit limits

- No paid/live inference, real-provider answer quality, account-specific model/effort compatibility, production deployment or Nyx migration is claimed.
- Native opaque reasoning is not persisted between turns or process restarts; replay is visibly transcript-only. No private chain of thought is displayed.
- No cross-conversation memory, Google/media/project/Cloud faculties, HTTP fetch/search tool, generic shell, MCP server, fork/comparison UI or automatic compaction is introduced.
- Scratch is an application-scoped namespace with path/link checks; it is not OS process isolation against a malicious same-user native process.
- SDK 10.0.302 and full repository/remote CI remain separately qualified surfaces. The existing host regression suite passed locally on 10.0.401.

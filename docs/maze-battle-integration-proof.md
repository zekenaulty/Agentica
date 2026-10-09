# Maze Battle external-host integration proof

Qualification date: 2026-10-09. Scope: the initial isolated proving ground and Agentica Lab web kickoff.

## What was exercised

```text
Maze Battle browser and scoped observation
  -> reusable Agentica browser SDK / WebSocket protocol
  -> Agentica.Lab.Web / AgenticaRunner / LlmWorkflowPlanner
  -> streaming Responses adapter / local deterministic SSE fixture
  -> validated remote capability request
  -> Maze-owned state transition and durable original result
  -> Agentica receipt, new observation, refinement and host-backed completion
```

The browser owns world state, observation eligibility, commands, persistence and completion evidence. Agentica owns bounded planning, proposal validation, remote invocation custody, evidence projection and run outcomes. No Maze simulation or capability names were added to Agentica core or the web service. The external fixture uses the normal provider interface and public planning input; it cannot query the private maze.

This is an actual Chromium/browser-SDK/service integration with a scripted provider. It qualifies the transport and execution loop, not a model's exploration intelligence or live-provider behavior.

## Versioned evidence

- Agentica service runtime: `10e14c6914f318761ee09b823d7f4b27af0b7c37`.
- Browser SDK revision: `4291c7ff`; its bytes are unchanged in the service runtime above.
- The subsequent Agentica `975037b` change repairs an unrelated workspace-search deadline race. It does not change the web runtime, SDK or provider fixture used here.
- The [captured G1 evidence](evidence/maze-battle-g1-2026-10-09.json) contains all three named scenario summaries, run IDs, exact source-artifact hashes, the tested host harness hashes and independent service-outcome checks. The Maze harness was a working-tree addition based on `3a4beda89a68a99e00d396eabceef745f91f652f` when captured; its later host commit packaging is separate from the executed evidence.

SDK follow-up `6dc1c6d510dde06aa67c3b0f5118e13894711af5` additionally checks deadlines after awaited reservation and keeps raw host callback diagnostics off the wire. It passed 68 SDK checks and the real SDK/service loop plus refreshed desktop/mobile UI smoke. The recorded Maze cohort below used SDK `4291c7ff`; do not silently relabel its version. The wire schema is unchanged.

The original isolated seed `20261008` was retained. The planner's 128-call cap and Agentica context budgets were not increased to obtain completion.

| Scenario | Agentica run | Observed result |
| --- | --- | --- |
| First chest | `labrun_8fbac5a1e3fa4b97a611febe28d05bf8` | One chest, three spaces, revision 107, 108 host receipts and 109 streamed provider calls |
| Applied action with lost acknowledgment | `labrun_e059c6295117402ab642ca685cf87865` | Same objective result; exact original dropped payload retransmitted after reconnect and matched durable browser storage |
| Takeover | `labrun_3811cb0252d04a3c9ec2ddb9143be808` | Cancelled at revision 8; reserved action settled as a conflict with no additional host effect |
| Later bounded run | `labrun_2fe72a054a5c4f3f95e14cd167f8706f` | New epoch 2, same objective, one chest across three spaces; 38 remembered cells and their original `observedAt` values matched in the exact recovered source |

All four service records were independently inspected: the three completing runs succeeded, the original takeover run was cancelled, and every run had zero pending actions. Core receipts numbered 109, 109, 11 and 102 respectively; the normal runs' 108 host receipts exclude the local exact-evidence read.

## Acceptance boundaries

- **Streaming:** the browser records unique provider `callId` values and their start, positive text delta and completion events. Sequential fixture ordinals identify the corresponding provider interval. The captured delta precedes completion of that same call; merely receiving progress after a finished response would fail the test.
- **Full loop:** the normal and reconnect runs include many planning/refinement calls, multiple host operations, a refusal and one host completion. A complete validated plan dispatches the effect; partial text never does.
- **Effect custody:** the dropped-result test interrupts the socket after application, captures the exact retransmitted original payload, and compares it with the durable browser result. Completion replay after reload leaves the authoritative snapshot unchanged.
- **Context:** a deliberate retained-source read exercises exact evidence after frame compaction. Current validated action-result observations are also reusable directly, so normal movement does not require an extra read every turn.
- **Continuity:** later-run knowledge comes from the host's saved, authorized observation projection. A new epoch does not inherit private provider continuation or old service hypotheses. Remembered entries must retain their original observation time.
- **Truth:** successful outcomes require host completion evidence. A refused operation, cancelled run or exceeded budget stays explicit; provider narration cannot establish a chest or an effect.

The Agentica suites separately cover changed-input duplicates, stale and malformed results, cancellation with unsettled effects, restart fencing, slow telemetry consumers, bounded retention, scoped retrieval, correction and sourced hypothesis/counterevidence. Those focused fixtures supplement the three browser cases; they are not claims that every failure mode occurred in one expedition.

## Lessons incorporated during integration

1. **Keep live custody outside test cleanup directories.** Deleting initialized storage while the service runs now reports `storage.unavailable` and fences admission. The fixture launcher uses an isolated directory outside Playwright's reports.
2. **Spend context on evidence.** A real 59-step request exceeded its 122,880 estimated-token allowance at 123,849. Lossless compact JSON reduced the same mandatory input to 85,141. Objective, capabilities, exact newest evidence and completed-step identities were preserved. Initial and refinement regression fixtures also retain 96 completed steps and nested evidence under unchanged limits.
3. **Select the current planning message explicitly.** Stateless Responses input can include earlier user messages. The scripted planner selects the last user message and validates its sections; scanning arbitrary strings had reused revision zero.
4. **Reuse fresh linked results.** A move's validated result already contains the current observation. Ignoring it caused a redundant query after every move and exhausted the call budget. Exact retained-source retrieval remains available for omitted history and is tested deliberately.
5. **Test deadlines independently of timer scheduling.** Linux coverage exposed a delayed cancellation callback in unrelated workspace search. The search now checks monotonic elapsed time before reporting success, with deterministic tests at and beyond the deadline.

## Reproduction and remaining scope

Use the [external-host protocol](external-host-browser.md), [Lab startup guide](lab-web-kickoff.md) and [loopback fixture launcher](../samples/Agentica.Lab.Web.FixtureHost/README.md). The Maze repository owns its observation compiler, deterministic provider, browser tests and proof launcher. The provider fixture is restricted to loopback; no live credentials are needed.

This closes the initial isolated proving-ground gate. Ordinary saved-play adoption, expanded combat/inventory/town capabilities, live-provider qualification, broader MCP transport, dynamic skills, Nyx migration and durable runtime/provider continuation remain separate slices. Nyx Lab and active user checkouts were preserved.

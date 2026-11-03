# Durable host frontier review — Q07 / issue #32

Reviewed head: `27cb2ce9717438edfe19487e3920faa183112c97`, compared with `977d0cab5421fd4e09fd33f1224f241b7e425abe`.
Peer checkout: `C:/Users/Zythis/.codex/worktrees/maze-action-recovery/Agentica` (read only, clean at inspection).

## Verdict

**Conditional go for consolidation and qualification; not yet a stable-merge approval.** The bounded custody design is coherent, and no confirmed P0/P1 defect or automatic unknown-effect replay was found. Address the two P2 items below, retain both sides of the CI conflict, and qualify the resulting exact merged head. This pass ran no builds, tests, providers, or mutation against the peer checkout.

The frontier contains 26 commits / 59 changed files (7,484 additions, 99 deletions): explicit decision Stop, separately recorded decision adoption, durable initial-start admission, compact display outcomes, wake attachment/replay ordering, HTTP action recovery retention, and exact/paged recovery reads. Domain state and effects remain host-owned; no Maze-specific behavior or migration of Nyx is introduced.

## Findings

### P2 — Rejected wake/adoption can evict an unrelated retained window

`Agentica.Lab.Web/Runtime/HostRunRegistry.cs:73` calls `MakeRunCapacity()` before validating the new observation/adoption and before `HostOperationStore.Apply`. At 64 retained runs, a rejected wake removes and disposes the oldest terminal run, even though no successor was admitted. Its full outcome and known in-memory usage then become unavailable. Durable action receipts remain intact, so this is a retention/availability defect rather than a repeated effect or budget reset.

Ordinary wake already had this ordering at baseline `977d0ca`; this stack adds adoption rejection paths to it. `CreateDurable` has the correct pattern at `HostRunRegistry.Starts.cs:28`: find a candidate without eviction, then release it only after successful durable admission and `HostRun` construction. Use that pattern in `Wake` as a focused correction.

Regression: park one operation and retain 63 additional terminal runs, attempt an invalid observation or adoption at the 64-run limit, assert all prior run references and ledger bytes remain unchanged, no successor/planner is created, and the valid original decision can still be claimed once. Also exercise a storage rejection after candidate selection.

### P2 — Public recovery documentation describes the old behavior

`docs/external-host-browser.md:339` says any retained original run rejects HTTP recovery. Current `HostRunRegistry.Recover` rejects only a nonterminal run, and reconciles a retained terminal run through `AcceptRecoveredResult`. The text would steer a host back to a dead execution window.

The same document's completed-history pruning description at line 354 omits the new protections for operation-admission results and HTTP-pinned terminal receipts. Neither `GET /api/recovery/action` nor paged `GET /api/recovery/run` is documented in the consumer contract. Add their exact key fields, response shapes, `Cache-Control: no-store`, 16-item action-ID cursor behavior, and the rule that missing/empty retained evidence never proves no prior attempt occurred. These are additive HTTP seams; no new SDK method is required to make the existing endpoints truthful.

## Custody and budget observations

| Concern | Current source and test evidence | Assessment |
| --- | --- | --- |
| Admit before work | `HostRunRegistry.Starts.cs:8`, `HostStartStore.cs:54`; `HostStartRegistryTests.Admission_precedes_planning_and_freezes_full_request_across_exact_retry`, failed-context and socket tests | Original and accepted typed requests, independent hashes, manifest and immutable run identity are flushed before context opening or planner creation. Context failure leaves a recoverable admission rather than a retryable fresh execution. |
| Exact replay / restart | `CreateDurable`, `Wake`, `HostStartStoreTests`, `HostDecisionAdoptionTests.Lost_process_claim_cannot_replan_with_missing_changed_or_fresh_adoption` | Changed duplicates fail. A retained live claim reattaches to the same run; a vanished process returns interrupted, never an automatically recreated planner. |
| Stop before start | `HostRun.Cancel` / `Start`; `HostStartRegistryTests.Stop_before_start_cannot_be_undone_by_the_socket_finally_block` | Cancellation fences a not-yet-started task, including the socket handler's `finally` path. |
| Initial Stop settlement | `HostRunRegistry.Starts.cs:54`; original-action and parked-operation tests | Stop persists first. Settlement waits for nonterminal windows, unresolved action custody and open operations. A missing run or handed-off usage is interrupted/unknown, not zero. Repeating a settled Stop does not cancel a newer objective. |
| Decision Stop | `HostOperationStore.cs:247`, `CheckStoppedParent:152`; `HostDecisionStopTests` | Preserves the exact decision and settled usage. Refuses live claims, admitted children, unresolved original custody and completed work. Reserved late child admission is fenced. |
| Adoption | `HostOperationStore.cs:415`, history replay at 475; `HostDecisionAdoptionTests` | Original decision remains immutable; adoption is a separate source and observation. Ancestor epochs, session-scoped adoption identity and observation rewind are checked on ordinary writes and reload, including reordered source entries. |
| Cumulative limits | `HostRunRegistry.Wake` and `HostRun.ExecuteAsync` | Wake retains original objective/provider/capabilities/limits, consumes one continuation and passes remaining steps/refinements/continuations to the runner. Provider calls are cumulative accounting; this seam does not add a new cross-window provider-call cap. Host-local usage stays opaque and separately owned. |
| Capacity funding | `HostStartStore.Persistence.cs`, `ActionCustodyStore.cs:341`; allowance/capacity tests | Initial starts reserve 512 logical bytes per unsettled terminal record; action dispatch reserves result/recovery space. Bounds are logical envelope budgets, not physical disk-space promises. Storage exhaustion fails closed without pruning required custody. |
| HTTP recovery | `HostRunRegistry.cs:181`, `HostRun.AcceptRecoveredResult`, `ActionCustodyStore.cs:113` | A terminal in-memory run and a lost-process original can accept exact result reconciliation without invoking the capability. Duplicate recovery persists the HTTP retention pin before reporting success. Write failure leaves unresolved custody recoverable. |
| Receipt retention | `ActionCustodyStore.cs:338`; `ActionCustodyDurableRetentionTests` | Operation admissions and HTTP-pinned terminal receipts survive per-session/count/byte pruning. Ordinary completed socket receipts retain bounded pruning. Legacy already-pruned evidence is not reconstructed. |
| Recovery reads | `ActionCustodyStore.Recovery.cs:9`; lookup/run discovery tests | Exact host/session/epoch/run filtering, deterministic ordinal action-ID pagination with at most 16 metadata items, exact action lookup, validation before storage reads, no model or effects. Paging bounds response size, not full-ledger validation cost. |
| Socket binding | `HostSocket.cs:40`; `HostStartTransportOrderingTests`, `HostOperationSocketTests.WakeReplay` | Attachment and binding snapshot occur under the send gate; authoritative actions cannot overtake the binding reply. Exact wake replay includes pending reconciliation and terminal evidence. |
| SDK compatibility | `agentica-host.mjs`, new `agentica-host.d.mts`, real SDK loopback fixture | Durable APIs freeze caller data, compare typed defaults and complete authority data, and install binding synchronously before queued effects. No client recomputation of .NET digests or automatic retry is claimed. Existing `start` remains available but cannot bypass retained durable lineage. |

### Operational limits to preserve

- One store instance/service process per storage directory; checksums detect structural changes but do not authenticate hostile disk edits.
- Start and operation ledgers retain at most 512 entries each. Starts retain tombstones with no implicit eviction/reset. Action custody retains up to 1,024 entries / 128 unresolved actions; required retention can fill the store and must fail closed.
- New records remain ledger version 1 with additive optional fields omitted when absent. Existing legacy digests are covered by byte-preservation tests. A rollback to an older binary after writing new fields is not guaranteed: strict unmapped-member reads can fail closed. Do not remove new fields or reset ledgers to force a rollback.
- Claims survive restart; in-process provider stacks do not. Reconciliation remains explicit. A stopped/partial execution window is not host objective completion.
- No outside host/game/Bridge/Cloud acceptance was re-run or admitted by this review.

## Existing qualification and current CI

`docs/durable-host-starts.md:111` pins **342 Web / 328 SDK checks** to candidate `9af8801`, using local SDK 10.0.401 and the opt-in typed serializer test. It explicitly does not claim hosted CI or the pinned SDK 10.0.302. Seven later commits in this frontier add qualification notes, display compaction, wake replay ordering, and the HTTP/action/run recovery work. The historical counts do not qualify the final `27cb2ce` head.

Read-only GitHub inspection found no PR for the durable stack and no Actions runs for `codex/maze-action-recovery`. Existing merged PR #9 qualifies the earlier handoff head `1e1a2df`, not this stack. The source includes new Windows/Linux CI commands, but configuration is not execution evidence. Root must publish the new consolidation PR and use its exact-head checks.

The October 12 document mentions a separate Maze browser cohort. This Agentica-only review did not inspect that repository or treat the statement as current paired-host proof.

## Merge resolution

Read-only three-argument `git merge-tree` compared clean-room `7f68dc03272a768761757ef45ecb06a80113fc47` with durable `27cb2ce`, using merge base `1e1a2df3c489d633b62b739b7bd61eba2cfa3c5d`.

Only `.github/workflows/ci.yml` has textual conflicts: both Windows/Linux host-SDK commands. Retain the union and the serializer environment switch:

```yaml
env:
  AGENTICA_TEST_START_SERIALIZATION: '1'
run: node --test browser-tests/host-sdk.test.mjs browser-tests/host-recovery.test.mjs browser-tests/host-result-validation.test.mjs browser-tests/host-operations.test.mjs browser-tests/host-decision-stop.test.mjs browser-tests/host-decision-adoption.test.mjs browser-tests/host-starts.test.mjs Agentica.Lab.Web.Tests/chat-ui.test.mjs
```

`Agentica.Lab.Web/LabWebApplication.cs` is the only other overlapping file and auto-merges textually. Semantically verify `partial` class composition, clean-room experiment DI/storage/routes and durable `HostStartStore`/start/recovery routes all remain. No SDK/runtime file has a textual overlap with this clean-room head. Refresh against the actual main head before action.

## Focused qualification plan

Root owns serialized builds and execution in its new consolidation worktree:

1. Add the rejected-wake retention regression and update recovery contract documentation.
2. Locked restore; Release build with analyzers; project-aware formatting under the pinned SDK in CI. Restore/build local evidence must identify the actually installed SDK.
3. Fast Web filter: `HostDecisionStopTests|HostDecisionAdoptionTests|HostStart|HostOperationSocketTests|ActionCustody|HttpActionRecoveryTests|HostRuntimeTests`. Include Windows-only atomic-replace failure injection; record expected Linux skips separately.
4. Run the complete Lab Web suite on the combined head. This includes the actual Node SDK / real Kestrel initial-start/stop proof, raw sockets, restart, funded capacity and HTTP recovery.
5. Run the union Node command above with serializer opt-in. The serializer fixture compiles linked real .NET contracts rather than testing a second model. Keep it in the serialized build lane because the Node process invokes `dotnet`.
6. Run the existing provider and core suites, then exact-head Windows/Linux CI including package/audit/security/container gates. Preserve clean-room Markdown, recall, native token counting, trace and isolation evidence.
7. Merge only after reviewing changed-head results. This qualifies reusable Agentica service/SDK behavior; live providers and paired Maze controller adoption remain separate explicit acceptance.

## Focused correction prepared

The two P2 corrections are implemented in root-owned `codex/host-custody-consolidation`, initially based on `27cb2ce`. `Wake` now selects a capacity candidate without removing it and releases it only after the durable claim, context opening and run construction succeed. The consumer recovery document now covers terminal retained-run recovery, protected receipt retention and both read APIs.

`HostWakeRegistryCapacityTests` adds five cases: invalid observation, invalid decision sequence, invalid adoption, failed operation-ledger replacement, and context-write failure after a durable claim. Rejections preserve all 64 prior windows; a valid retry remains capacity bounded and preserves cumulative usage. The post-claim failure intentionally retains its interrupted identity rather than rolling back or retrying it. The two file-sharing injections are Windows-specific. Tests have been handed to root's serialized execution lane; this paragraph records prepared source, not a passing result or exact-head qualification.

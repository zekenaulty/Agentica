# Nyx learning reference baseline — synthetic corpus v1

Status: N-01 / NL-01 source-pinned reference and synthetic scenarios. No Nyx, Bridge,
Cloud or Maze Battle runtime, setting, source or data is changed. No participant
records or provider credentials are used.

Tracking: [N-01 #28](https://github.com/zekenaulty/Agentica/issues/28),
[C-01 #18](https://github.com/zekenaulty/Agentica/issues/18).

## Source cuts and limitations

Agentica baseline before this additive contract:
`977d0cab5421fd4e09fd33f1224f241b7e425abe`.

The governing October 4 documents cite Agentica `e075d932bdf13db713b1e338eaa443a093a6953e`
and Bridge `b5f0cab5733ab1f4e7a240f8d2350a03d4c9be93`.
The later October 15 provider-adoption proposal cites Bridge
`0e5a5b4d01fb37e70359634bf96adc4f260bdea4`.

These are historical document source pins, not fresh proof of current Bridge
behavior. The combined companion topology and later proposal below are reported
at those cuts. Current installed topology and operational status remain UNKNOWN
in this Agentica-only slice. No production observer or live data export is needed
to reproduce the synthetic corpus.

Native source documents read on November 3:

| Document | Returned modification time UTC |
| --- | --- |
| [Parallel implementation plan](https://docs.google.com/document/d/18YbyqcqiOaNukNWcB1CW1GQk4YOqmAkXKwIM405E0eU/edit) | 2026-10-04T14:44:24.419Z |
| [Approved obligation refinement](https://docs.google.com/document/d/1VEKHUVs9pSkJZIs4ODgFclXjoEsN73KHb4EKmIXw5ew/edit) | 2026-10-04T13:34:35.971Z |
| [Long-horizon learning/replacement plan](https://docs.google.com/document/d/1fmkWlsQCbqLm2-cIEOTLu6X7lTm3dzGxaGN2WZWgmbA/edit) | 2026-10-04T14:42:27.028Z |
| [Memory provider-adoption proposal](https://docs.google.com/document/d/1a5LFWJTqRP5zHLk5Hk9HC3c6gP_Rsr5LSxTL3uAB9HQ/edit) | 2026-10-15T14:41:26.841Z |

The later proposal distinguishes first Agentica.Clients library adoption for
memory construction from first read-only historical MCP conformance. It does not
establish an installed migration. Dated Bridge correctness findings require fresh
owner reconciliation before treating them as open defects.

## Ownership map

| Owner | Current contract or proposed responsibility |
| --- | --- |
| Agentica | Generic bounded planning/execution, frames, canonical evidence, optional contracts and acceptance interfaces |
| Agentica learning host (future N-02/N-04) | Disposable experiment state, fixture replay, exact presentation, comparative evaluation and measured costs |
| Nyx host (future independent ownership) | Identity/voice, participant relationships, governed memory, source/perspective selection, disclosure and conversational acceptance |
| Bridge host | Owned canonical records, channel/device integration, credentials, transport/delivery and domain effect APIs |
| Effect owner | Original durable attempt, current authority, idempotency, reconciliation, cumulative allowance and authoritative result |
| MCP adapter | Versioned protocol discovery/invocation/results under host-owned standing bindings; no separate semantic authority |

At the historical Bridge cuts, Nyx and Lab were composed in the Bridge companion
application without a demonstrated Agentica runtime dependency. Independent Nyx
and Bridge processes remain a future design decision. Sharing a library does not
imply a shared database, identity, credential or supervisor.

## Portable scenarios

Executable fixture definitions are
`Agentica.Tests/Fixtures/NyxLearning/SyntheticObligationScenarios.cs`.
They contain only authored synthetic text and explicit expected semantics.
`CorpusVersion = 1`; scenario IDs and exact input identities below are pinned.
Golden identities are independently calculated from the documented canonical JSON
shape and asserted by the focused contract tests.

### historical-correction-v1

- Obligation: identify the current delivery preference through two synthetic messages.
- Perspective: history reader; both originals remain attributed to speaker.user.
- Exact evidence: earlier morning preference followed by an explicit evening correction.
- Coverage: Complete through messages 1–2, corpus synthetic.history.2.
- Bound capability: history.read; ready, with zero expected new effect dispatches.
- Acceptance: evening is current; cite the actual later user correction; do not
  rewrite or delete the earlier statement.
- Thought test: hypothesis that the correction changes current preference;
  support is the attributable correction/order; falsifier is a later eligible
  reversal. Model agreement is not independent evidence.

Manifest identity:
`sha256-v1:2900718ba2cc8b93e5b2ad1c356128573090935ddbb6fe9eebe7e806854f6594`.

### exact-source-unavailable-v1

- Obligation: return an exact passage from primary document revision 4.
- Perspective: scoped document reader.
- Exact presented evidence: a different, explicitly secondary note.
- Coverage: Unknown for the required primary passage.
- Bound capability: document.open-exact; installed/bound in fixture semantics,
  runtime-ready false, expected effect dispatches zero.
- Acceptance: the secondary note may be cited as secondary; it cannot satisfy
  the exact primary-source requirement.
- Expected result: Unavailable/Unsatisfied with the exact-source residual retained.
- Thought test: sufficient-secondary-evidence hypothesis is refuted by the wrong
  source identity. Technical outage does not erase the standing binding.

Manifest identity:
`sha256-v1:a7b16a19620a6b82cc968a2405306c4c86eb0ca03f727cbe63b04295560b20d6`.

### lost-effect-response-v1

- Obligation: accept one synthetic item exactly once under the same parent objective.
- Perspective: scoped inventory operator.
- Exact evidence: accepted state observation plus an original-attempt ledger
  statement that cancellation left its effect outcome unknown.
- Coverage: Complete through the declared observation/attempt cut; complete
  inspection of known uncertainty is not a confirmed effect.
- Bound capability: inventory.accept; ready but zero permitted new effect dispatches.
- Acceptance: reconcile the exact original attempt and establish authoritative
  completion. A new provider, similar observation or elapsed lease grants no retry.
- Expected result: Ready interpretation / Indeterminate fulfillment, original
  attempt and reconcile residual retained.
- Thought test: observed state proves original causation is unconfirmed; only
  the matching original result can settle it. Another actor's transition is
  disconfirming evidence for that causation claim.

Manifest identity:
`sha256-v1:82c52c580775c1c3a88c076822f1a5d451ebca42f29a5f65ebe0bd7b05cdab04`.

## Reproduction and next gates

The focused entry point is:

```powershell
dotnet test Agentica.Tests/Agentica.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ObligationActivationContractTests
```

Use the repository-pinned SDK and its restored dependency graph. Record actual
command/result and exact source head at qualification; the existence of this
command is not a test-pass claim.

The fixtures instantiate the optional C-01 contracts and declare expected
reference behavior. They do not run historical Nyx code, prove contemporary
Bridge behavior, exercise an actual model's judgment or execute the complete
Agentica loop. N-02 comparison and C-02 host acceptance integration remain required
to establish that boundary. N-03 must additionally qualify the real SDK MCP path;
a package reference or in-process fixture alone is insufficient.

For the later comparison, preserve these input identities and acceptance horizons.
Record unsupported citations, lost qualifications, source/perspective mistakes,
invalid reuse, duplicate effects, original UNKNOWN attempts, mounted context,
provider calls/tokens and actual event timing. Add before/after cases rather than
silently redefining an acceptance rule to make the candidate pass.

No automatic policy promotion, production migration or future operational routing
is enabled by these synthetic declarations. NL-05/NL-06 remain separate owner-host
adoption/retirement work.


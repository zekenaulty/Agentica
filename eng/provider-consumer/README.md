# Packed provider-only consumer: A5 and full recovery proof

The [P08 recovery host](RECOVERY.md) extends this original A5 consumer with actual
prepared streaming inference, required durable admission, fresh-process reopen,
native exchange, captured routes, explicit unknown-attempt recovery and host
completion checks. The current entrypoint runs both suites. Historical A5-only
qualification below remains evidence for its earlier source cut.

This standalone console consumer references packed `Agentica.Clients` through a
local feed. It has no project reference, Agentica runner, live endpoint, index,
maintenance worker or scheduler. Its explicit `Google.GenAI` 1.13.0 reference
qualifies dependency coexistence with the reviewed Bridge package level; it makes
no Google request.

Run from the repository using its exact pinned SDK:

```powershell
./eng/Verify-ProviderConsumer.ps1 -DotnetPath /absolute/path/to/pinned/dotnet
```

The entrypoint builds and packs current source, copies this consumer into a unique
ignored `artifacts/provider-consumer` directory, restores into an isolated package
cache and verifies that the consumed Agentica package bytes equal this run's
packages. A read-only global cache may seed third-party dependencies. NuGet source
mapping reserves the two exact Agentica IDs for the new local feed. Output remains
available for review; no cleanup of other workspaces or packages occurs.
The script verifies an already provisioned SDK; it does not download/install one.
It gives the CLI a run-local home, disables first-use HTTPS certificate generation
and CLI telemetry, and restores the caller's process environment on exit.
Only the explicitly named consumer source files and `global.json` are copied;
native continuation exports and private captures are outside that copy list.

## Host embedding example

`EmbeddingAttempt.cs` demonstrates an admitted/observed `HttpClient` around the
existing `OpenAiEmbeddingClient`. The host captures exact profile, purpose,
operation/scope and ordered source revisions. It inspects the actual serialized
request, computes a nonsecret identity and reserves the attempt before forwarding.
It retains safely observed response identifiers/status and nullable input usage
before downstream vector validation. A valid usage object with an invalid vector
therefore stays failed paid-work evidence. Source text, credentials and remote
error bodies never enter its receipt.

The journal is deliberately in-memory. A real consumer supplies durable reservation,
attempt recovery, encryption/retention and cumulative budgeting. The terminal
handler here is a counted deterministic fixture with no network transport, retry
handler or redirect logic. Production consumers must separately qualify their
handler chain, including automatic redirects and retries. One public client send
alone does not prove one physical request through an arbitrary injected handler.

The scenarios cover document/query ordering and source mapping, reservation
rejection, vector validation after measured usage, missing usage, HTTP failure,
cancellation before/after dispatch, lost response, failed body reads, wrong
profile/purpose, reordered inputs, repeated invocation, stable scoped identities
and duplicate source references. The stub returns vectors out of order to exercise
the packaged client's indexing behavior.

## Evidence limits

This original section describes the early A5 package/embedding boundary using
synthetic data. The linked recovery proof covers P08's subsequent LLM calls,
captured routes, durable reopen and multi-call loop. No paid call, canonical reindex, Bridge
deployment, model-quality assessment or installed acceptance occurs here.

New P01–P06 LLM contracts do not implicitly extend `IMemoryEmbeddingProvider`.
Bridge PM11 owns its embedding interface adaptation and durable admitted/observed
transport; PM12–PM14 own historical indexing, eligibility and source-set coverage.

## Original early A5 qualification — 2026-11-06

- Library source base: `ae69a2f`; consumer sources are this bounded change.
- Exact SDK: **10.0.302**, invoked explicitly; `global.json` unchanged.
- `eng/Verify-ProviderConsumer.ps1`: **PASS**. Locked library restore, Release
  library/consumer builds (zero warnings/errors), fresh package hashing, isolated
  restore and Google SDK **1.13.0** coexistence all passed.
- **15 deterministic scenarios passed**. No provider calls or credentials were
  obtained; the synthetic key never leaves the terminal fixture.
- Retained local output:
  `artifacts/provider-consumer/419f2ff5ddb841e2bc4ebb91b458032c/a5-results.log`.
- Full P08 LLM/captured-route/durable-recovery work, Linux CI, live model behavior
  and installed Bridge acceptance remain **NOT-RUN** for this increment.

Review lesson: a rejected duplicate attempt ID does not transfer ownership of the
original journal record to the rejected consumer. Update failure evidence only
after this consumer successfully reserves that record. The duplicate scenario
creates a second consumer over the same journal/ID, proves zero second sends and
preserves the first accepted record. Recheck this invariant when replacing the
in-memory journal with durable or concurrent host storage.

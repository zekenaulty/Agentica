# Isolated OpenAI provider proof

This driver qualifies the real Lab composition without touching an existing Lab
process or saved chat. Its default is **offline**. Building it and running
`self-test` reads no environment credential, starts no host, and sends no network
request. The fake-handler tests use the actual shared prepared provider and native
count seams.

## Offline qualification

Run from an Agentica workspace with its pinned .NET SDK:

```powershell
dotnet restore eng/provider-live-proof/ProviderLiveProof.csproj --configfile NuGet.config
dotnet run --project eng/provider-live-proof/ProviderLiveProof.csproj -c Release --no-restore -- self-test
```

The tests write only disposable evidence beneath this workspace's ignored
`.agentica/verification/provider-live-proof-self-tests/` directory. A missing mode
or `dry-run` also selects these offline tests. They cover physical limits across
reopen, reservation before send, journal failure, changed generation bodies,
fabricated persisted count links, excess input, lost responses, cancellation,
missing usage, and late errors. Reasoning tokens are retained separately and are
never added a second time to the inclusive output-token cost.

Implementation qualification on **2026-11-06**: exact SDK **10.0.302**, Release
build with warnings as errors passed; all **13 offline guard cases passed**.
Independent source review checked guard custody and matched the scenario verifier
to the current saved receipt/event/artifact shapes. No host startup, credential
read, live model call, browser acceptance or completed real-provider loop is
claimed by this offline result.

## Explicit live invocation

Only the proof owner should invoke these commands after reviewing offline results
and the existing cumulative ledger. Both require `--live`, a separate unused port,
and a state directory **inside the current workspace's `.agentica/` directory**.
Ports 5078 and 5089 are rejected. Reuse the same state directory on restart; do not
delete or replace its ledger to obtain fresh capacity.

```powershell
$proofState = Join-Path (Get-Location) '.agentica/verification/p09-live'
$labContent = Join-Path (Get-Location) 'Agentica.Lab.Web'
dotnet run --project eng/provider-live-proof/ProviderLiveProof.csproj -c Release --no-build -- serve --live --state $proofState --port 5097 --content-root $labContent
```

`serve` starts an isolated Lab at `http://127.0.0.1:5097/chat.html` and submits no
turn. Existing Lab routes/UI and the provider model-list GET use the guarded
transport. The driver maps the existing `OPENAI_REPLIKA_KEY` environment variable
to the configuration's `OPENAI_API_KEY` lookup **in memory**. It neither creates nor
saves a credential. The same mapping is recreated after restart.

Replace `serve` with `run` to submit the single self-contained proof through the
public loopback Lab HTTP API. It creates a new clean chat in Harness mode, writes a
fresh marker and two random operands only to that chat's scratch file, and asks the
model to read, then calculate, then emit the exact answer. Only `scratch.read` and
`utility.calculate` are bound; the normal host response tool remains available.
The model must make later planning decisions after both tool observations. The
verifier checks the canonical receipts, response artifact, completion evidence,
residual obligations, at least three provider attempts, at least two refinements,
and event order. A scripted tool sequence or a correct narrative alone cannot pass.

The submission intent is saved before the send. An existing intent is reconciled
from the Lab store and is **never automatically resubmitted**, including an
ambiguous API disconnect. The driver then stops and recreates its own host with
the same store/ledger, rereads the conversation, and verifies unchanged evidence
with zero new provider attempts. It does not restart any existing Lab process.

## Physical guard and cost policy

The borrowed provider `HttpClient` is guarded beneath both compiler counts and
actual dispatch counts. The guard accepts only HTTPS `api.openai.com` with no
query, redirect, alternate host, or port. It allows model-list GETs and exactly the
Responses inference/count POST routes. It implements no retry. The live socket
handler disables redirects.

| Control | Cumulative ceiling / required value |
| --- | --- |
| Inference POSTs | 6 physical handler sends |
| Native count POSTs | 8 physical handler sends, including compiler counts |
| Cost reservations | $5 across the same durable ledger |
| Model / effort | `gpt-6-luna` / `high` |
| Generation | `stream:true`, `store:false`, no stored conversation/response reference |
| Complete input | successful exact native count at most 8,000 tokens |
| Output | at most 8,192 tokens per inference |
| Native search | disabled in this proof cohort |
| Other provider tools/endpoints | denied, including embeddings |

The exact complete generation-body SHA256 must match a successful native count
observed through the same guard. Its separate filtered count-body hash and actual
count-attempt identity are retained. Links expire after 30 minutes and are checked
against the count ledger again on reopen. A count measurement does not authorize a
changed output limit or any other generation-body change. Every physical POST
consumes capacity and is persisted before sending. An unresolved generation body
cannot be resent after restart.

The guard's pinned policy rates, dated **2026-11-06**, are $0.25 per million input
tokens and $0.75 per million inclusive output tokens. The ledger retains the
$0.01 native-search rate as structural policy metadata, but native search is
disabled: its provider-side context is outside the initial measured-input bound.
It reserves the input/output maxima before inference. Full-drained, unambiguous
terminal usage can settle that reservation; missing usage, cancellation, disconnect
or late error retains it. Contradictory observations preserve maximum token facts
and retain the reservation. Known exposure above the original reservation remains
held and blocks further work. Settlement is an evidence-based estimate, not a provider
invoice. A reservation overrun blocks further work.

Native input counting is free according to the
[OpenAI token-counting Help Center](https://help.openai.com/en/articles/4936856-understanding-and-counting-tokens),
which also states that it does not consume normal model-inference quota. A native
count's measured generation input is **not billed count usage**. It still consumes
one of the eight physical count slots. Model/rate references:
[GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna),
[pricing](https://developers.openai.com/api/docs/pricing), and
[token-counting guide](https://developers.openai.com/api/docs/guides/token-counting).

## Evidence and limits

`provider-ledger.json` stores structural hashes, counts, bounded public IDs,
status, timestamps and cost reservations. It contains no credential, prompt,
answer, private reasoning or native continuation payload. An exclusive lock
prevents two driver processes from borrowing the same ledger concurrently.
`scenario-evidence.json` exports only verdict codes, hashes, IDs, sequence numbers,
counts and cost custody. Actual canonical chat/tool content remains solely in the
disposable Lab store and scratch workspace so the verifier can inspect it.

These controls are an isolated proof policy, not a production multi-tenant billing
service. The state files and workspace are trusted host storage. Do not copy a
ledger between independent experiments, edit its settlement, or delete it while
the approved proof is in flight. A build or offline self-test does not claim live
model correctness, browser acceptance, provider billing, or a completed migration.

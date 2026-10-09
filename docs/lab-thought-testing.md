# Lab thought testing

The Lab can retain a small, inspectable prediction exercise alongside a host run. It uses the existing host capability and observation contracts. It introduces no Agentica core type, host wire message, provider call, or new execution authority.

## Lifecycle

1. Call `lab.hypothesis.record` with its normal sourced hypothesis fields and all three optional test fields: `expectedResult`, `falsifier`, and `capabilityId`. Each statement is at most 2,048 characters. A test begins as an inferred model hypothesis.
2. Before possible delivery, the service binds current pending tests for that exact capability to the original action ID and canonical request hash. The first matching future action receives the binding. A prediction created after an action was recorded cannot attach to that earlier action.
3. After validating the host result and custody, the service atomically records the observation and its action/result linkage. Evidence includes the original action ID, host evidence ID, result hash, observation identity/revision and content hash.
4. Call `lab.hypothesis.assess` with the original `hypothesisId`, `actionId`, `hostEvidenceId`, `observationId`, a summary, and `supported`, `refuted`, or `inconclusive`.

An assessment requires a resolved result and the exact retained observation. Invented or mismatched receipt identities, post-action predictions, changed original requests, unavailable source payloads, and unresolved effects are refused. `inconclusive` means a known result did not settle the hypothesis; it cannot discharge unknown effect custody.

The model supplies the semantic comparison. Deterministic checks establish chronology and source binding; they do not prove that the model interpreted the evidence correctly. `supported` remains a model assessment. It neither creates an observed host fact nor supplies the completion artifact required by the runner. An assessment creates a new model knowledge version linked to its prior hypothesis. Its stored expectation, falsifier and assessment are immutable; a changed prediction starts a revised hypothesis and a new test.

## Example

- **Expectation:** accepting the eligible item will change its status to accepted.
- **Falsifier:** the host refuses the action and reports the item still pending.
- **Actual evidence:** the original `host.accept` action returns a refusal, its host receipt ID, and a matching scoped observation.
- **Assessment:** refuted, with those exact action/result/observation references. The host's observed pending state remains separate from the model's refuted prediction.

The same shape supports other host domains without a maze, coordinate, or game-specific schema.

## Persistence, bounds and inspection

Thought metadata is included in the context snapshot and matching `lab.knowledge.query` entries. Full and compact planning projections preserve source distinctions and include the thought record when it fits their existing byte budget; omitted knowledge remains queryable. Exact retained observations can be reopened through `lab.evidence.read`.

The store retains at most 128 thought tests and 128 action-evidence records. It retires assessed or superseded tests first and terminal action evidence first. It refuses additional metadata when only pending entries remain. Observation and knowledge retention keep their existing bounds; pruned source payloads cannot be reconstructed or newly assessed. This is bounded Lab learning history, not an indefinite archive.

Context files written with schema 1 remain readable: the original raw payload hash is checked before optional thought fields are populated. The next successful context write uses schema 2 and preserves earlier observations and knowledge. Identity isolation across host, session, epoch, scope and perspective is unchanged.

`RecordActionDispatch` records pre-dispatch intent, not proof of delivery. The runtime calls it before reserving durable effect custody and before sending. A later custody write can fail with no delivery; the prediction binding remains historical intent. Durable action custody independently determines whether an effect is unresolved. `RecordActionResult` is called only after result validation and custody prevalidation, before custody resolution. Exact replay permits recovery if a later custody write fails.

Completed thought records survive later runs and service restarts. Restart custody reconciliation alone does not reconstruct a missing scoped thought-result link: unless the trusted host service restores that exact link, the pre-crash test remains unassessed. A fresh observation cannot stand in for the original result.

## Qualification

`HostContextTests` exercises supported/refuted/inconclusive assessment, invented and mismatched evidence rejection, post-action prediction rejection, unknown-result refusal, exact replay, compact projection, later bounded-run reopening, and schema-1 migration. These context fixtures use deterministic host results. They establish source binding and state separation; actual host integration and live model judgment require their separately reported evidence.

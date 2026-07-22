# LLM Product-Proof Evidence

This directory contains durable, redacted benchmark evidence. It intentionally stores no prompts,
model response bodies, credentials, workspace contents, or hidden reasoning.

## 2026-07-21 Cohort

The cohort in
[`20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d`](20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d/)
ran the complete `agentica-product-proof-v1` matrix:

- five WorkbenchQuest cases, five repetitions each;
- the locked MazeQuest unlock/seed-173/7x7/visibility-2 holdout, five repetitions;
- Gemini 2.5 Flash with temperature 0 and thinking disabled; and
- versioned prompt, schema, harness, retry, timeout, and policy configuration.

The original live aggregate failed closed because its v1 price model did not classify the provider's
automatic implicit-cache token counts. The immutable `runs.jsonl` file was not changed or rerun.
After the official cached-input rate was added, the offline command strictly re-read and hashed those
same records, re-validated the fixed matrix and cohort identities, and wrote:

- [`aggregate.json`](20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d/aggregate.json) — current metrics and gate result;
- [`reaggregation.json`](20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d/reaggregation.json) — the run-file SHA-256 and pricing-review receipt;
- [`manifest.json`](20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d/manifest.json) — the untouched live-run manifest; and
- [`runs.jsonl`](20260721T143708466Z-agentica-product-proof-v1-9ad5c876785a46959a49511fb768102d/runs.jsonl) — one bounded telemetry record per run.

Final gate: **passed**. Overall verified success was 29/30 (96.7%), false success was 0/30,
WorkbenchQuest was 25/25, and the MazeQuest holdout was 4/5. Invalid-plan incidence was 2/30
overall, 0/25 for WorkbenchQuest, and 2/5 for the holdout: one holdout run required a JSON repair
and succeeded, while another was rejected as `PlanInvalid`. Neither became a false success.

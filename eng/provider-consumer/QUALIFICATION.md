# Local P08 qualification — 2026-11-06

- Library source: **2c76f0a32248c540fe5205a6dfffe79c859364ba**, with the P01–P06
  integration APIs. This fixture branch changes only consumer/verification/learning
  files; the library package sources are unchanged.
- Exact SDK **10.0.302**, invoked explicitly. Locked restore, Release library and
  standalone consumer builds passed with **zero warnings/errors**.
- Fresh local packages consumed through PackageReference and a new cache; both
  restored nupkg byte hashes matched this run's feed. Google.GenAI **1.13.0**
  coexistence passed.
- **34 offline scenarios passed:** 15 retained A5 scenarios and 19 P08 host cases.
  Four complete loops launch a fresh OS process and continue actual prepared
  streaming requests through authoritative host completion. Additional cases
  preserve unknown originals across reopen/explicit successors and complete a
  retained-Google-to-OpenAI transition with zero new Google model calls.
- Full command: `eng/Verify-ProviderConsumer.ps1 -DotnetPath <exact-SDK-dotnet>`.
- Retained output under this worktree:
  `artifacts/provider-consumer/41974d9608954f6ea7a244eead8cd3e0/provider-consumer-results.log`.
- Agentica nupkg SHA-256:
  `bb6b7a6624bd42c26bf6a0a38e39b671a20c8a61f10681307cf5b60540712cfd`.
- Agentica.Clients nupkg SHA-256:
  `fa0bd4ab6d6375813e02f9d2c1b4ee5dff5cd5c16830acbd6a99c7843ec3ba5e`.
- **NOT RUN:** Linux CI at this worker stage, live provider/account calls, installed
  Bridge, production/concurrent recovery, canonical memory/index migration or
  deployment. Root owns dependency reconciliation, independent review and merge/CI
  qualification. The [proof boundary and source projection limits](RECOVERY.md)
  remain part of this result.

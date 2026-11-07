# Local P08 qualification — 2026-11-06

- Library API source: **2c76f0a32248c540fe5205a6dfffe79c859364ba**, with the P01–P06
  integration APIs. Latest local build HEAD: **56ae681**, plus reviewed consumer
  corrections. The fixture changes only consumer/verification/learning files;
  package bytes still incorporate their own build revision metadata.
- Exact SDK **10.0.302**, invoked explicitly. Locked restore, Release library and
  standalone consumer builds passed with **zero warnings/errors**.
- Fresh local packages consumed through PackageReference and a new cache; both
  restored nupkg byte hashes matched this run's feed. Google.GenAI **1.13.0**
  coexistence passed.
- **35 offline scenarios passed:** 15 retained A5 scenarios and 20 P08 host cases.
  Four complete loops launch a fresh OS process and continue actual prepared
  streaming requests through authoritative host completion. Additional cases
  preserve unknown originals across reopen/explicit successors and complete a
  retained-Google-to-OpenAI transition with zero new Google model calls. The new
  returned-before-Accept writer/reader process regression parks without reinference
  until an explicit successor decision; other unaccepted paid outcomes also park.
  Retained embedding source/profile/eligibility checks run in actual reservation,
  and reconstructed wire evidence preserves explicit Host authorship independently
  of provider User roles.
- Full command: `eng/Verify-ProviderConsumer.ps1 -DotnetPath <exact-SDK-dotnet>`.
- Retained output under this worktree:
  `artifacts/provider-consumer/e53a78abae3540cb80ba06e7b92601f4/provider-consumer-results.log`.
- Agentica nupkg SHA-256:
  `35796bb6a4bdde35b319f4c50460fb15b97aa09c5a1d65e0dcf6f4322daf31dd`.
- Agentica.Clients nupkg SHA-256:
  `ebe233f450797ef170edb3fee7368ba7a22062607e050cfbc0c795653157e247`.
- **NOT RUN:** Linux CI at this worker stage, live provider/account calls, installed
  Bridge, production/concurrent recovery, canonical memory/index migration or
  deployment. Root owns dependency reconciliation, independent review and merge/CI
  qualification. The [proof boundary and source projection limits](RECOVERY.md)
  remain part of this result.

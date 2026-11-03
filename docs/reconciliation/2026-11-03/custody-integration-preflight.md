# Agentica custody integration preflight

Captured: 2026-11-03T14:46:36.118654+00:00. Rechecked: 2026-11-03T14:48:33.858564+00:00.

Pinned main: `9c96c8f672e2611a07f8f7d5d3455f336dddebf5`. Owned durable host frontier: `0ba24d19c31de8e62fe3035c421f5c78b16a8f03` (`codex/host-custody-consolidation`, descended from `27cb2ce9717438edfe19487e3920faa183112c97`). This follows the [original custody audit](git-custody.md) and preserves its capture unchanged.

## Result

- **27 registered Agentica worktrees, 32 local branch refs and 20 fetched origin branch refs** were inspected. Read-only `ls-remote --heads origin` matched the fetched refs. Every configured upstream matches its local head except local `main`.
- **All 23 refs in the original JSON branch ledger remain present and accounted for**: 7 main ancestors, 6 entirely patch-equivalent to main, 5 ancestors of the owned host frontier and 5 entirely patch-equivalent to that frontier. No legacy committed behavior requires an additional independent harvest. Host-frontier custody is not yet main admission.
- Count correction: the original report's prose said 24 local branches, but its preserved `git-custody.json.localBranches` has 23 entries. The exact legacy comparison set here is those 23 refs; current 32 includes 9 subsequently created owned branches. No baseline ref is missing.
- The primary checkout's **12 dirty files have unchanged byte lengths and SHA-256 values** against the original capture. Ten now match integrated main after line-ending normalization; README and ProductStatus retain older inventory/status prose plus already-preserved local corrections. Their inspected differences do not warrant replacing the newer main documents.
- Local `main` at `d58e3aada277d3210705541cce8bb13014b21399` is **not checked out in any registered worktree**. Its ahead-1 / behind-84 ancestry difference contains one patch-equivalent upload, already represented by main-reachable `144ab2aa25c360e68474bfbe43a058e3abdf9273`. It has no unique patch to cherry-pick.
- At final recheck all peer heads and refs were unchanged; the primary dirty hashes remained identical. The owned host worktree's temporary tracked `global.json` change observed during the first capture had cleared, leaving it clean. This review did not edit that file. All other checkouts were clean before this report was added. No stash exists.

## Active integration custody

The captured main contains PRs [#11](https://github.com/zekenaulty/Agentica/pull/11), [#35](https://github.com/zekenaulty/Agentica/pull/35), [#38](https://github.com/zekenaulty/Agentica/pull/38), [#34](https://github.com/zekenaulty/Agentica/pull/34), and [#36](https://github.com/zekenaulty/Agentica/pull/36). GitHub open-PR heads at this cut were:

| PR / lane | Exact head | Custody |
| --- | --- | --- |
| [#37 MCP binding/readiness](https://github.com/zekenaulty/Agentica/pull/37) | `ceb2f5690c8336024346d6eec730af972961218c` | Retain until exact-head admission |
| [#39 obligation learning host](https://github.com/zekenaulty/Agentica/pull/39) | `80ac346402f8016ee26f79a7c73e13d71ce05de9` | Retain until exact-head admission |
| [#40 historical evidence faculty](https://github.com/zekenaulty/Agentica/pull/40) | `2d2637fed1932ebdc225d83784a8d214a98bd687` | Contains the #37 and #39 work plus its own faculty slice; do not treat overlapping ancestry as separate missing work |
| [#41 fixture contention](https://github.com/zekenaulty/Agentica/pull/41) | `bcf5a4e87853db7e939e3491e0d59c7a0b456d1d` | Already ancestor of the owned host frontier; still unmerged in pinned main |
| Owned host consolidation / planned #42 | `0ba24d19c31de8e62fe3035c421f5c78b16a8f03` | All 26 original durable-stack commits retained by ancestry, plus correction/integration work; local-only ref at capture |

These are custodial mappings. This preflight does not replace CI, source review or exact-head merge gates. In particular the host frontier is not a retirement target while it is the only admitted-work candidate retaining the original durable stack.

## Local branch ledger

Ahead/behind are ancestry counts against pinned main, not counts of missing behavior. Patch-equivalent side branches can remain ahead despite complete content custody elsewhere. All configured upstreams are exact unless explicitly shown below.

| Local ref | Head | Ahead / behind | Representation | Checked out |
| --- | --- | --- | --- | --- |
| `codex/capability-test-suites` | `408407aa8d99` | 0 / 43 | Main ancestor | Yes |
| `codex/chessquest-replay-resume` | `30ee3540b2e2` | 0 / 99 | Main ancestor | No |
| `codex/fixture-contention-proof` | `bcf5a4e87853` | 2 / 0 | Ancestor of owned host frontier; unmerged | Yes |
| `codex/historical-evidence-faculty` | `2d2637fed193` | 13 / 0 | Owned active PR frontier; retain | Yes |
| `codex/host-custody-consolidation` | `0ba24d19c31d` | 32 / 0 | Ancestor of owned host frontier; unmerged | Yes |
| `codex/lab-clean-room-chat` | `cc813f6a9c2b` | 0 / 15 | Main ancestor | Yes |
| `codex/lab-durable-handoff` | `1e1a2df3c489` | 0 / 28 | Main ancestor | Yes |
| `codex/lab-provider-settings` | `54831b4031b3` | 0 / 29 | Main ancestor | No |
| `codex/lab-web-host` | `7e233cdf735d` | 0 / 31 | Main ancestor | No |
| `codex/maze-action-recovery` | `27cb2ce97174` | 26 / 28 | Ancestor of owned host frontier; unmerged | Yes |
| `codex/maze-adoption-tests` | `50d635c6f037` | 10 / 28 | All non-ancestor patches represented in host frontier; unmerged | Yes |
| `codex/maze-compact-outcome` | `a1f00ac142a4` | 21 / 28 | Ancestor of owned host frontier; unmerged | Yes |
| `codex/maze-decision-adoption` | `d65fb5b7a5d4` | 11 / 28 | Ancestor of owned host frontier; unmerged | Yes |
| `codex/maze-operation-stop` | `6d8db3b68a38` | 6 / 28 | Ancestor of owned host frontier; unmerged | No |
| `codex/maze-start-admission` | `adb5cfecdc73` | 23 / 28 | Ancestor of owned host frontier; unmerged | Yes |
| `codex/maze-start-sdk` | `ea8d808912a7` | 14 / 28 | All non-ancestor patches represented in host frontier; unmerged | Yes |
| `codex/maze-start-store` | `deff22970ab1` | 15 / 28 | All non-ancestor patches represented in host frontier; unmerged | Yes |
| `codex/maze-stop-tests` | `1888eefe31ff` | 3 / 28 | All non-ancestor patches represented in host frontier; unmerged | Yes |
| `codex/maze-wake-replay` | `577a214d020b` | 23 / 28 | All non-ancestor patches represented in host frontier; unmerged | Yes |
| `codex/mcp-binding-readiness` | `ceb2f5690c83` | 5 / 0 | Owned active PR frontier; retain | Yes |
| `codex/mcp-sdk-acceptance` | `6ee6224d78fb` | 1 / 54 | All non-ancestor patches represented in main | Yes |
| `codex/mcp-streaming-first-slice` | `e075d932bdf1` | 0 / 45 | Main ancestor | Yes |
| `codex/obligation-contracts` | `9186540f788e` | 0 / 12 | Main ancestor | Yes |
| `codex/obligation-learning-host` | `80ac346402f8` | 7 / 0 | Owned active PR frontier; retain | Yes |
| `codex/provider-reasoning-controls` | `eedd9df27590` | 1 / 54 | All non-ancestor patches represented in main | Yes |
| `codex/provider-run-continuation` | `cd34431a2be1` | 4 / 54 | All non-ancestor patches represented in main | Yes |
| `codex/provider-schema-parity` | `236ee5e09c37` | 0 / 12 | Main ancestor | Yes |
| `codex/provider-usage-semantics` | `ffcef01f7d29` | 0 / 11 | Main ancestor | Yes |
| `codex/reasoning-effort-cli` | `f9c777301b6b` | 1 / 50 | All non-ancestor patches represented in main | Yes |
| `codex/roadmap-reconciliation` | `c0346342a2f7` | 0 / 11 | Main ancestor | Yes |
| `codex/semantic-projection` | `d964b3e48f17` | 1 / 57 | All non-ancestor patches represented in main | Yes |
| `main` | `d58e3aada277` | 1 / 84 | All non-ancestor patches represented in main | No |

### Verified patch mappings

The following specific pairs were rechecked using `git patch-id --stable`; every pair matches. Reachability of each retained equivalent was separately checked against the pinned main/host commits. This supplements `git cherry` rather than treating commit ancestry as the only form of custody.

| Historical commit | Retained equivalent | Current custody |
| --- | --- | --- |
| `41cc2284beb9cf184c33b0907e3059f30e79b8c7` | `0606e4c927040dc82361918dada86070f5adf593` | Owned host frontier; not yet main |
| `3123ab904134df3d51066738c58ce787faeb5b9a` | `140f606714cb98628a6e78502989f2049667bbf5` | Owned host frontier; not yet main |
| `cf7780a676010d24d9bd67cd0e1d600025860139` | `7e7ee865707051231b0a37d96a569cb0e6f26ac3` | Owned host frontier; not yet main |
| `50d635c6f037a14694c0152159ff78662556249a` | `43c3a3c761d229d3b526fd426f4be63876eebe48` | Owned host frontier; not yet main |
| `40a3e0af5c95817e04b423d81d4b10d82b6e86ac` | `0965c78cb537c423f941da34cde0a6baec634b24` | Owned host frontier; not yet main |
| `5627d96f4ae58d0a41bd590cd385adee1ed8ea51` | `53b513d8bd216758e49d315726fd934c15ca5050` | Owned host frontier; not yet main |
| `ea8d808912a75f26eea107a9c0bcfd0b0e5a991c` | `7347a3bd194fa4d5392ccd854cd0dac9f865e587` | Owned host frontier; not yet main |
| `a71e8bd8541cd4fefe544ae6327ec98aa2ba69f3` | `b3f45f630c5304cac8841b48f823dd8a5e6c4e46` | Owned host frontier; not yet main |
| `875710f1c1731f459e770dff60ba57fa1b92ec5b` | `cd5f76c5156cbd83e44d42ca3469c3faed863a46` | Owned host frontier; not yet main |
| `5be619a940615fb3d6fad20bfdc83f41e04f36ee` | `136ac5b92bbea574e58132f96617b2fcf9935bab` | Owned host frontier; not yet main |
| `deff22970ab1dd75765a9cb683e8c85ba589d6ca` | `9af88010b637850b0a3fee3d6a20502fc2d84efb` | Owned host frontier; not yet main |
| `ae8d711e05c1376cb7d41dfd4d4e72aacd64f1c6` | `f1b32f40e3c447ab90fc5259c2a3f433f9afa625` | Owned host frontier; not yet main |
| `c3ef776501dd79ba5ea2245373f8f1040e263b74` | `90d516665eaf541a10387e7f95d6265ea8fa94f7` | Owned host frontier; not yet main |
| `1888eefe31fff65fcd3b3ac631766b6343fd9580` | `bd351cd1c61e6a68a39677e1cf7bf1d5197f314f` | Owned host frontier; not yet main |
| `577a214d020ba35ba3188761da6a114b978516b7` | `adb5cfecdc73888e9d9243a6b4131e50b9c9761b` | Owned host frontier; not yet main |
| `6ee6224d78fb0860ab889c08499cd4639e646568` | `dfd5bf89a811e8a3a388ce876bc4f21db6bc073a` | Main |
| `eedd9df27590145c085378433d6c83b9c489af1a` | `61b6eca1cf4e3b74045379ac52da96031b17740e` | Main |
| `67bf370e6d481f43eb0183ef02ad40b5f03a3d91` | `e8b065ec47b932716acd19885f104115ff9be448` | Main |
| `bee4acefdc2e6396cca7fc677c13dba210d6c3da` | `61b6eca1cf4e3b74045379ac52da96031b17740e` | Main |
| `d418b6a40e487cbe9b749464dc28e34ff02cf62e` | `8d8b8017f9e3ed0b846f4bfd8f3a3525afc74c21` | Main |
| `cd34431a2be1f6a5878c35666f6115d7253a7830` | `e075d932bdf13db713b1e338eaa443a093a6953e` | Main |
| `f9c777301b6bb9c306b942a2ecc2a66e107b3b15` | `575bd52bcdb8b15fa8d14866861832d7210a4427` | Main |
| `d964b3e48f17e28009420e3a675fdba00a12ad7f` | `28f88312cdec37e0d000d783b409d5da8182062d` | Main |
| `d58e3aada277d3210705541cce8bb13014b21399` | `144ab2aa25c360e68474bfbe43a058e3abdf9273` | Main |

## Remote ref retirement candidates

**Eleven non-main origin refs are content-safe retirement candidates by ancestry.** Each has a merged PR and its exact advertised head is already in pinned main. None is the head or base of an open PR at this cut. Retiring a remote ref does not dispose of its local checkout or ignored state. Root owns any action and must recheck the exact advertised hash immediately beforehand.

| Origin ref | Advertised head | Merged PR | Custody condition |
| --- | --- | --- | --- |
| `codex/capability-test-suites` | `408407aa8d99e036a0422d84b6f6babcbd9e891e` | [#6](https://github.com/zekenaulty/Agentica/pull/6) | Ref content merged; local worktree custody remains separate |
| `codex/lab-clean-room-chat` | `cc813f6a9c2b1a851a1698b52ab2e96a4a68716f` | [#11](https://github.com/zekenaulty/Agentica/pull/11) | Preserve runtime/user-state checkout |
| `codex/lab-durable-handoff` | `1e1a2df3c489d633b62b739b7bd61eba2cfa3c5d` | [#9](https://github.com/zekenaulty/Agentica/pull/9) | Preserve runtime/user-state checkout |
| `codex/lab-provider-settings` | `54831b4031b3a8fac058ab73fc2ab73f4328f8c8` | [#8](https://github.com/zekenaulty/Agentica/pull/8) | Ref content merged; local worktree custody remains separate |
| `codex/lab-web-host` | `7e233cdf735d85c3b34a7b13730a6b720dbd2afa` | [#7](https://github.com/zekenaulty/Agentica/pull/7) | Ref content merged; local worktree custody remains separate |
| `codex/mcp-streaming-first-slice` | `e075d932bdf13db713b1e338eaa443a093a6953e` | [#5](https://github.com/zekenaulty/Agentica/pull/5) | Preserve dirty primary checkout and ignored state |
| `codex/obligation-contracts` | `9186540f788ea69cc5f948f496ddd441f730245f` | [#38](https://github.com/zekenaulty/Agentica/pull/38) | Ref content merged; local worktree custody remains separate |
| `codex/provider-schema-parity` | `236ee5e09c379f45361380b52725c421f2ebc3bb` | [#35](https://github.com/zekenaulty/Agentica/pull/35) | Ref content merged; local worktree custody remains separate |
| `codex/provider-usage-semantics` | `ffcef01f7d2955ad6015589fdf9481481f20af6e` | [#36](https://github.com/zekenaulty/Agentica/pull/36) | Ref content merged; local worktree custody remains separate |
| `codex/roadmap-reconciliation` | `c0346342a2f78720a4d809622882f8c5d98e9152` | [#34](https://github.com/zekenaulty/Agentica/pull/34) | Defer while this report is being authored |
| `zekenaulty-cloud-audit` | `144ab2aa25c360e68474bfbe43a058e3abdf9273` | [#4](https://github.com/zekenaulty/Agentica/pull/4) | Ref content merged; local worktree custody remains separate |

Retain remote `main`. Retain these eight unmerged origin refs: `codex/fixture-contention-proof`, `codex/historical-evidence-faculty`, `codex/maze-action-recovery`, `codex/maze-decision-adoption`, `codex/maze-operation-stop`, `codex/maze-start-admission`, `codex/mcp-binding-readiness`, and `codex/obligation-learning-host`. The four legacy durable refs remain held until the host consolidation is admitted and their ancestry is rechecked against that new main.

### Local-only / remote-only names

Local-only: `codex/chessquest-replay-resume`, `codex/host-custody-consolidation`, `codex/maze-adoption-tests`, `codex/maze-compact-outcome`, `codex/maze-start-sdk`, `codex/maze-start-store`, `codex/maze-stop-tests`, `codex/maze-wake-replay`, `codex/mcp-sdk-acceptance`, `codex/provider-reasoning-controls`, `codex/provider-run-continuation`, `codex/reasoning-effort-cli`, `codex/semantic-projection`.

Remote-only: `zekenaulty-cloud-audit` at `144ab2aa25c360e68474bfbe43a058e3abdf9273`, already a main ancestor. It is included in the merged remote candidates above. Local `main` is a stale local ref, not an independent unharvested implementation.

## Worktree and ignored-state custody

All paths below belong to the same Agentica Git common directory. `Clean` means tracked/untracked Git state only. Ignored build outputs are not enumerated below; runtime-state names are metadata only. No ignored contents were copied, hashed or exported, and this review did not inspect or stop any process holding them.

| Workspace | Head / branch | Tracked/untracked state at final recheck | Ignored runtime names |
| --- | --- | --- | --- |
| `C:/Users/Zythis/source/repos/Agentica` | `e075d932bdf1` / `codex/mcp-streaming-first-slice` | 12 preserved files; exact baseline hashes | `.agentica/`, `.env`, `Agentica.Lab/.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/fixture-contention-proof/Agentica` | `bcf5a4e87853` / `codex/fixture-contention-proof` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/historical-evidence-faculty/Agentica` | `2d2637fed193` / `codex/historical-evidence-faculty` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/host-custody-consolidation/Agentica` | `0ba24d19c31d` / `codex/host-custody-consolidation` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/lab-clean-room-chat/Agentica` | `cc813f6a9c2b` / `codex/lab-clean-room-chat` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/maze-action-recovery/Agentica` | `27cb2ce97174` / `codex/maze-action-recovery` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-adoption-tests/Agentica` | `50d635c6f037` / `codex/maze-adoption-tests` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-compact-outcome/Agentica` | `a1f00ac142a4` / `codex/maze-compact-outcome` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-operation-stop/Agentica` | `d65fb5b7a5d4` / `codex/maze-decision-adoption` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-start-admission/Agentica` | `adb5cfecdc73` / `codex/maze-start-admission` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-start-sdk/Agentica` | `ea8d808912a7` / `codex/maze-start-sdk` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-start-store/Agentica` | `deff22970ab1` / `codex/maze-start-store` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-stop-tests/Agentica` | `1888eefe31ff` / `codex/maze-stop-tests` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/maze-wake-replay/Agentica` | `577a214d020b` / `codex/maze-wake-replay` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/.codex/worktrees/mcp-binding-readiness/Agentica` | `ceb2f5690c83` / `codex/mcp-binding-readiness` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/obligation-contracts/Agentica` | `9186540f788e` / `codex/obligation-contracts` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/obligation-learning-host/Agentica` | `80ac346402f8` / `codex/obligation-learning-host` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/provider-schema-parity/Agentica` | `236ee5e09c37` / `codex/provider-schema-parity` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/provider-usage-semantics/Agentica` | `ffcef01f7d29` / `codex/provider-usage-semantics` | Clean | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/roadmap-reconciliation/Agentica` | `c0346342a2f7` / `codex/roadmap-reconciliation` | Clean before this report; report now added | `.agentica/` |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-lab-web` | `1e1a2df3c489` / `codex/lab-durable-handoff` | Clean | `Agentica.Lab.Web/.agentica/` |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-mcp-acceptance` | `6ee6224d78fb` / `codex/mcp-sdk-acceptance` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-provider-continuation` | `cd34431a2be1` / `codex/provider-run-continuation` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-reasoning-cli` | `f9c777301b6b` / `codex/reasoning-effort-cli` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/source/repos/Agentica-projection` | `d964b3e48f17` / `codex/semantic-projection` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/source/repos/Agentica-provider-controls` | `eedd9df27590` / `codex/provider-reasoning-controls` | Clean | None listed at ignored matching level |
| `C:/Users/Zythis/source/repos/Agentica-worktrees/capability-test-suites` | `408407aa8d99` / `codex/capability-test-suites` | Clean | None listed at ignored matching level |

### Preserved primary dirty-file hashes

The source checkout remains at `e075d932bdf13db713b1e338eaa443a093a6953e` on `codex/mcp-streaming-first-slice`. Every entry below still has the exact original bytes; nothing was reset, moved or cleaned. All 12 paths now exist in pinned main. A differing whole-file hash for the newer README/status is expected and is not grounds to overwrite them.

| Relative primary path | Bytes | SHA-256 | Integrated content comparison |
| --- | --- | --- | --- |
| `README.md` | 32582 | `4226fc5d3c05261d0f13741b5b962651a9b1eba5e3a973c389f371e34347ec9c` | Newer main inventory/status additions inspected; preserve both checkout custody and current main |
| `docs/Agentica.DeferredWork.md` | 4153 | `5bf06432a97544e8eb5f45d0d23044bb989f86b0f4ee465b4c4d4625d8035c3d` | Same normalized lines as main |
| `docs/Agentica.ProductStatus.md` | 34242 | `5df54971e9aae774b5cefe76061a56cf3d081606ed6bda9b04363e1bca8a60c6` | Newer main inventory/status additions inspected; preserve both checkout custody and current main |
| `.agents/learning/MEMORY.md` | 1748 | `6c42c011071ddbeb31ebbb5447085e97353b706d2a3b149e1e394686a8c69255` | Same normalized lines as main |
| `.agents/learning/records/agentica-conservative-input-budget.json` | 1456 | `6444c216d880ac8a7fd30a6f94e256d55f5b7e22b8e45389913606576971d911` | Same normalized lines as main |
| `.agents/learning/records/agentica-host-owned-state.json` | 1384 | `70d7683c84571f4b2f1bdd00a770743f8113fd572ce3540ac7238e1ea50edde2` | Same normalized lines as main |
| `.agents/learning/records/agentica-observability-is-not-call-admission.json` | 1648 | `ea953af8e6df20fea88cba33341ca15c5c0aa934753b3cdecdc59d1c932e1f5d` | Same normalized lines as main |
| `.agents/learning/scope.json` | 49 | `bceda6a34f704015ab065626ccbb3e3ffcfcd97cc4f890ed2a7806573124ee71` | Same normalized lines as main |
| `.agents/skills/agentica-work-learning/SKILL.md` | 2317 | `fe9839d59f220b159dd63c8b153576e5ff58ce769e12d2ef1af8321d2f98dbc5` | Same normalized lines as main |
| `.agents/skills/agentica-work-learning/agents/openai.yaml` | 248 | `633089b8aa4d7aa50e88e862939ca840d5a1ac35a95ef3bd81df709e187b9708` | Same normalized lines as main |
| `AGENTS.md` | 823 | `1a33219c35ccbfab5269371d697a89361accb4a19099c0df06f2634ebbe0181b` | Same normalized lines as main |
| `docs/Agentica.TypedObligationActivation.Direction.md` | 7170 | `e80b90db90b85338575833ab72091b00f6c4e838300a86bd955703418707f6a9` | Same normalized lines as main |

## Consolidation order and limits

1. Keep the primary dirty set and every runtime-state workspace intact. No checkout retirement is implied by a merged branch.
2. Finish the exact-head gates for #37/#39/#40/#41 and the owned durable consolidation, preserving overlapping evidence and the original 26-commit stack. Re-evaluate representation against the resulting main after each admission.
3. Commit this follow-up through the root-owned documentation lane before retiring its active remote authoring ref. The other ten merged remote candidates can be considered independently after fresh advertised-head and open-PR checks.
4. Local `main` is unoccupied and content-safe to reconcile after root deliberately accounts for its equivalent `d58e3aa` upload. Do not force a checkout switch in the dirty primary workspace to achieve that. This review changed no ref.
5. Treat old local branch/worktree removal as a separate custody action. Preserve local-only reference history or recoverable snapshots when patch equivalence, rather than ancestry, is the only relationship. No deletion or archive occurred here.

Scope: all currently registered worktrees and local/origin branch refs in the Agentica common repository. Other repositories, including Bridge, Cloud, Nyx, Maze Battle and adjacent separately initialized Agentica projects, were not scanned or changed. No recursive ignored-content inventory, process/runtime liveness proof, reflog/unreachable-object re-audit, builds, provider calls, ref fetch/update/prune/reset, deletion, archive or service action was performed. Read-only Git commands used `GIT_OPTIONAL_LOCKS=0`; GitHub reads identified PR heads/states. Original audit/reflog evidence remains available at its earlier capture and is not reasserted as fresh by this narrower pass.

The 14:48:33Z recheck found unchanged pinned main, host head and all local/origin refs; the only concurrent state change was the owned host worktree's qualification edit returning to clean. GitHub/remote state can advance after this timestamp. Re-capture it before any merge or retirement action.

## Cohort admission and retirement update: 2026-11-03T16:22:46Z

This is a later snapshot, not a rewrite of the earlier preflight. The fetched main at this cut is **`2e5c39d742698df41b0a96a7959e417c81cee039`**, the [PR #42](https://github.com/zekenaulty/Agentica/pull/42) merge of exact host-consolidation head `123a11b05c8406e68842eb1cc5e8428e35eda9ad`. Its first parent `1f0609efe598558d642f9a8794b8e6ff1e5fb49c` admits [PR #39](https://github.com/zekenaulty/Agentica/pull/39) at `a03e20c18a70dd06fc7862ef7b37c83a91ded384`. The prior MCP-readiness and fixture-contention changes are also retained by this main's ancestry.

### Legacy content is now represented in main

All **23 original branch-ledger refs** remain present locally. Fresh ancestry and `git cherry` checks classify **12 as main ancestors and 11 as entirely patch-equivalent to main**, with no unmatched legacy patch. In particular the original `27cb2ce` durable frontier and all 26 of its commits are now main ancestors; its five patch-equivalent side branches no longer depend on an unmerged frontier for content custody. The specific retained equivalents listed earlier in this report are now main-reachable.

The 11 patch-equivalent refs are `codex/maze-adoption-tests`, `codex/maze-start-sdk`, `codex/maze-start-store`, `codex/maze-stop-tests`, `codex/maze-wake-replay`, `codex/mcp-sdk-acceptance`, `codex/provider-reasoning-controls`, `codex/provider-run-continuation`, `codex/reasoning-effort-cli`, `codex/semantic-projection`, and local `main`. This is content reconciliation, not removal of those original local refs or checkouts.

The primary checkout is still at `e075d932bdf13db713b1e338eaa443a093a6953e`; its same **12 dirty files again match every baseline byte length and SHA-256**. Its tracked/untracked filename set is unchanged. Local `main` remains `d58e3aada277d3210705541cce8bb13014b21399`, remains unoccupied by any registered worktree, and is now ahead 1 / behind 137 by ancestry. Its sole ahead commit is still patch-equivalent to main-reachable `144ab2aa25c360e68474bfbe43a058e3abdf9273`. No local-main update is claimed here; root owns any later ref reconciliation.

### Retired remote refs and retained workspaces

The root-owned [remote retirement ledger](remote-retirement.json), observed at **2026-11-03T16:21:29.4273945Z**, now records **18 `verified_absent` remote heads**: the first cohort of 11 plus seven subsequently retired heads. Each recorded `archive_ref` was independently resolved and matches its recorded original head. The namespace is `refs/archive/agentica-reconciliation/2026-11-03/remote/`. Root used exact-head leases for retirement; this documentation pass performed no remote or local ref mutation.

Fresh read-only `ls-remote --heads origin` returned only these three heads at this cut:

| Remote head | Exact observed hash | State at this snapshot |
| --- | --- | --- |
| `main` | `2e5c39d742698df41b0a96a7959e417c81cee039` | Current admitted cohort |
| `codex/historical-evidence-faculty` | `dcfffe23059536263251f008d09f377548fe4f15` | Open PR #40 candidate |
| `codex/roadmap-reconciliation` | `c0346342a2f78720a4d809622882f8c5d98e9152` | Retained while reconciliation documentation is being authored |

All **27 registered Agentica worktrees remain present**. The local root-owned `.agentica/reconciliation/worktree-archives.json` records four managed archive attempts rejected because the task/workspace is pinned. No worktree was removed, no retry was made by this pass, and no filesystem or alternate-tool bypass was attempted. Root retained local proof copies under `.agentica/reconciliation/retained-verification/`; directory checks confirm the recorded five nonempty files:

| Retained lane | Proof files | Managed archive result |
| --- | ---: | --- |
| `provider-schema-parity` | 2 | Rejected: pinned task/workspace; original retained |
| `provider-usage-semantics` | 1 | Rejected: pinned task/workspace; original retained |
| `obligation-contracts` | 1 | Rejected: pinned task/workspace; original retained |
| `mcp-binding-readiness` | 1 | Rejected: pinned task/workspace; original retained |

Those local verification copies preserve artifacts; their presence is not a new execution or qualification claim. Ignored runtime data and the original primary dirty set remain with their owning workspaces.

### Remaining candidate boundary

[PR #40](https://github.com/zekenaulty/Agentica/pull/40) remains **open** at `dcfffe23059536263251f008d09f377548fe4f15`. Relative to the admitted main, its N-03 candidate has exactly seven changed files: the MCP conformance test, two lock files, the faculty contract document, the learning-host project reference, and the two historical-evidence MCP source files. These candidate files are not promoted into admitted main by this custody update. The roadmap workspace currently shares that candidate source head while owning separate uncommitted reconciliation records.

Root may subsequently change candidate heads, retire remaining remotes or reconcile local `main`. None of those future actions is asserted here. This append checked hashes, ancestry, patch equivalence, registered-worktree presence, archive-ref identity, remote advertisement and PR #40 state only. It changed only this report; no build, provider call, Git mutation, archive, runtime action or outside-workspace write was performed.

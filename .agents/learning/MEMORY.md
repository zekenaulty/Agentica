# Agentica development memory

Scope: agentica. Local entry point: [.agents/skills/agentica-work-learning/SKILL.md](../skills/agentica-work-learning/SKILL.md).

The records/ directory holds small accepted domain lessons with triggers, evidence and recheck conditions. Search by the current task's keywords; inspect only the useful matches. A source review is not live/installed proof. Newer source or an explicit user correction can invalidate an old lesson.

On this machine the global work-learning skill provides retrieval and append-only capture. Its configuration at ~/.codex/work-learning/stores.json locates this memory and a shared inbox so isolated worktrees can read newly learned lessons before a commit. The shared inbox is outside peer checkouts. Domain-specific accepted records remain here and can travel with this repository.

Record schema version 1 fields: id, schema_version, scope, kind, status, title, trigger, guidance, evidence, recheck, tags, recorded_on; optional supersedes. Evidence objects name kind, reference and, when applicable, revision and note. Use the detailed contract from the global skill when available. Explicit user instructions establish preferences; reviewed source or meaningful tests support technical acceptance. Other agents' reports and hypotheses remain candidates until checked.

Append one record per useful correction, failure pattern or reusable technique. Do not overwrite an earlier lesson: an accepted replacement names its prior ID in supersedes. Do not copy secrets, participant prose, raw provider output or full logs into these files. Current task progress, budget custody and effect attempts stay in their owning ledgers. A memory record never substitutes for their authority or truth.

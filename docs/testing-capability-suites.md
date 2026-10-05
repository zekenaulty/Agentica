# Capability test suites

Use a focused capability project for a bounded development check and the existing aggregate for full regression and coverage qualification. A capability suite is worthwhile when it removes unrelated build dependencies or has a distinct fixture/runtime boundary. Do not create projects merely to distribute file counts.

## Entry points

| Scope | Project | Behavior |
| --- | --- | --- |
| Complete regression and coverage | `Agentica.Tests/Agentica.Tests.csproj`, also included by `Agentica.slnx` | Compiles all existing tests, including the provider files linked from their new physical home |
| Deterministic modern provider adapters | `Agentica.Clients.Tests/Agentica.Clients.Tests.csproj` | Compiles only the six provider/ reasoning-control files; references Clients and its transitive Runtime dependency, with no Lab, MCP or chess dependency |

The focused project is intentionally outside `Agentica.slnx`: it overlaps the aggregate. Running both explicitly is useful to qualify the focused entrypoint, but is not an increase in unique test coverage. Existing solution and aggregate commands retain the complete test population and the current coverage thresholds. Each moved test file has one physical source, and both projects compile that same source. Namespaces, assertions, test names, theory data and fake HTTP handlers are unchanged.

Run commands from the repository with the SDK required by `global.json`. Restore and build a project before using `--no-build` or `--no-restore`:

```powershell
# Focused, deterministic provider capability
dotnet restore Agentica.Clients.Tests/Agentica.Clients.Tests.csproj --locked-mode --configfile NuGet.config
dotnet test Agentica.Clients.Tests/Agentica.Clients.Tests.csproj --configuration Release --no-restore

# Existing complete regression entrypoint
dotnet restore Agentica.slnx --locked-mode --configfile NuGet.config
dotnet test Agentica.slnx --configuration Release --no-restore
```

The aggregate still contains live-provider tests with their existing opt-in flags. Deterministic aggregate qualification must run with `AGENTICA_RUN_LIVE_LLM_TESTS` and `AGENTICA_RUN_LIVE_PROVIDERS` disabled in the child test process. A capability split does not authorize provider spending or change those gates.

Coverage remains collected from the aggregate with `eng/coverage.runsettings` and evaluated by `eng/Assert-Coverage.ps1`. Use a fresh results directory. Do not collect separate overlapping reports into that directory or average project percentages. A focused-suite pass is not a substitute for the full regression or release gates.

## Lift-and-shift rules

Within an authorized test-orchestration task, choose a small coherent capability and establish its dependency closure before moving files. An isolated worktree and reviewable PR preserve other active work. Do not rewrite test bodies, introduce a new framework, loosen assertions, change production behavior or extract shared helpers merely to force a split. Shared fixtures, collections, assembly settings, internal visibility, data files, subprocess paths and concurrency constraints are part of the behavior to preserve.

Keep the complete entrypoint operational. For this incremental pattern, link the moved sources into the aggregate and keep overlapping capability projects out of the root solution. Update source links, locked dependencies, CI and project exclusions together. A later conversion to disjoint assemblies is a separate change requiring a complete runner/discovery union and correctly combined coverage; a `ProjectReference` alone does not cause `dotnet test` to execute another test assembly.

Qualify the move with before/after source hashes, discovered identities and outcomes. The focused project's test identities must match the selected baseline cases, while the aggregate must retain its complete identity set exactly once. Record inherited failures and explicit skips without hiding them in a smaller passing suite. Verify the focused build's dependency graph to substantiate isolation; claim wall-clock improvement only after comparable measurements.

For a shared helper or partial class that crosses the proposed boundary, retain the existing assembly until another worthwhile boundary is found. Test selection alone can be sufficient; the authorization to split projects does not require a rewrite.

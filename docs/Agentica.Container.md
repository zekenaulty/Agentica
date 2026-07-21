# Agentica Lab Container Contract

`Agentica.Lab` is an internal research harness, not a supported product CLI or public container. The
container exists to make a reviewed lab build repeatable and to exercise the same artifact boundary in
CI.

Build from the repository root:

```powershell
docker build --file Agentica.Lab/Dockerfile --tag agentica-lab:research .
docker run --rm agentica-lab:research quest list
```

The build contract is deliberately closed:

- SDK and runtime images use exact patch tags plus immutable multi-platform manifest digests.
- `global.json` pins the SDK used outside the container to the same SDK feature band.
- every project restore uses a checked-in `Packages.lock.json` and `--locked-mode`;
- publish uses `--no-restore` and `ContinuousIntegrationBuild=true`;
- the final image contains only the published Lab output; and
- `.dockerignore` excludes credentials, local evidence, tests, source-control data, and build outputs.

The image must never bake in `.env`, API keys, run logs, benchmark evidence, or workspace data. Runtime
credentials, when a researcher intentionally supplies them, remain an external deployment concern.

## Updating Pins

An image or SDK pin update is a reviewed dependency change, not an automatic floating update:

1. choose the exact SDK/runtime patch versions;
2. resolve and record their multi-platform manifest-list digests;
3. update `global.json`, the Dockerfile tags/digests, and package locks together;
4. run the vulnerability/deprecation audits, Release build, tests, format, package consumer check, and
   container contract tests; and
5. build the container in CI and run the `quest list` smoke before accepting the update.

Changing a tag without its digest, or a digest without its human-readable patch tag, violates this
contract.

## Current Evidence

On 2026-07-21, the reviewed Dockerfile built successfully on Docker Engine 27.4.0 and the resulting
118.73 MiB image passed `quest list`. Image inspection confirmed the expected entry point (`dotnet Agentica.Lab.dll`),
source-available research labels, and non-root UID/GID 1654. The no-publish GitHub workflow
[run 29877458089](https://github.com/zekenaulty/Agentica/actions/runs/29877458089) then passed the same
digest-pinned build and smoke on Linux, closing the bounded external CI gate.

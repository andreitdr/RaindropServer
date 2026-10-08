# CI & releases

Two GitHub Actions workflows live in [`.github/workflows/`](../.github/workflows). They cover
building, testing and publishing the server; putting a build *on* a host is
[`deploy.sh`](deployment.md), which is a separate, manual step.

## `ci.yml` — build and test

Runs on every push to `main`, and is also called as the first stage of `release.yml`
(`workflow_call`). A newer push cancels a run still in progress.

| Job | Runner | Does |
|---|---|---|
| `build` | `ubuntu-latest` | `dotnet restore` then `dotnet build RaindropServer.sln -c Release` on .NET 10 |
| `test` | `ubuntu-latest`, after `build` | `dotnet test -c Release`, uploading `test-results/results.trx` as an artifact (even on failure) |

The tests are the ones in [`RaindropServer.Tests`](../RaindropServer.Tests) — see
[Local development & Docker](local-development-and-docker.md) for what they cover and how to
run them locally. They need no database server: the suite uses SQLite.

## `release.yml` — package and publish

Triggered by a pushed version tag matching `v[0-9]+.[0-9]+.[0-9]+`.

1. **`ci`** — reruns `ci.yml` against the tagged commit, so nothing is published that did not build
   and pass its tests.
2. **`package`** — a matrix of `ubuntu-latest` (`linux-x64`), `macos-latest` (`osx-arm64`) and
   `windows-latest` (`win-x64`). Each publishes `ServerCli` **self-contained** (`--self-contained
   true`, `-p:Version=<tag without v>`), so a host needs no .NET installed, deletes the development
   `raindrop.db` that publishing leaves behind, and packs the folder — `.tar.gz` on Linux and macOS
   (tar keeps the executable bit), `.zip` on Windows.
3. **`release`** — downloads every package and runs `gh release create <tag> dist/* --verify-tag
   --generate-notes`. GitHub adds the source archives itself.

Each project is tagged in its own repository, and the workflow comments point at a `release.sh` in
the folder that holds all three checkouts. That script is not part of this repository — tagging by
hand (`git tag vX.Y.Z && git push origin vX.Y.Z`) triggers the same workflow.

## What CI does not do

- No deployment. `release.yml` publishes artefacts; [`deploy.sh`](deployment.md) is run by hand.
- No Docker image is pushed to a registry. The [image](local-development-and-docker.md#building-the-docker-image)
  is built on the target host during a deploy.
- No linting or analysers beyond what `dotnet build` reports as warnings.
- No PR trigger: the workflow is `push` to `main` only.

## Related

- [Local development & Docker](local-development-and-docker.md) — running and testing locally.
- [Deployment](deployment.md) — `deploy.sh`, what a deploy does and refuses, rollback, backups.
- The other projects' pipelines: [RaindropCrypto](../../RaindropCrypto/docs/ci-and-releases.md),
  [RaindropClient](../../RaindropClient/docs/ci-and-releases.md).

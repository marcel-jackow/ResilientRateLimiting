# Releasing

How the maintainer publishes a version of `ResilientRateLimiting` and `ResilientRateLimiting.AspNetCore` to nuget.org. Contributors do not need this page; [CONTRIBUTING.md](CONTRIBUTING.md) is for them.

## How a release works

The workflow `.github/workflows/ci.yml` publishes only when a GitHub Release is **published**. It then builds, tests and packs, and waits for the maintainer's approval in the `release` environment. Only after approval does it push the packages to nuget.org and attach them to the Release page.

nuget.org logs the workflow in with trusted publishing: a short-lived key for each run, so no nuget.org API key is stored in GitHub. The nuget.org policy trusts only this repository, the workflow file `ci.yml`, and the environment `release`.

These do **not** release anything:

- a `git push` of a tag alone;
- a Release saved as a draft;
- a merge to `main`.

## Steps

1. **Version.** If the version is new, change `VersionPrefix` in `Directory.Build.props` in a pull request and merge it. The run fails if the tag and `VersionPrefix` differ, so a typo in the tag cannot reach nuget.org. For a pre-release, the tag adds a suffix (for example `v0.2.0-rc.1`) and `VersionPrefix` stays `0.2.0`.
2. **Create the Release**, with a new tag `v<version>` and target `main`:
   - on the website: **Releases → Draft a new release** → type the tag → "Create new tag on publish" → target `main` → title and notes ("Generate release notes" fills them from the merged pull requests) → **Publish release**;
   - or from the command line: `gh release create v0.2.0 --target main --generate-notes`.
3. **Approve.** The run pauses before the `publish` job, and GitHub sends an email. Open the run under **Actions**, download the `packages` artifact if you want to look inside, then **Review deployments → Approve**.
4. **Check.** After a few minutes, both packages show on nuget.org (they are validated first; search can take up to an hour). The Release page has the four package files (`.nupkg` and `.snupkg` for each package).

## When something fails

nuget.org never deletes a version, and a version number can be used only once. What to do depends on where the run stopped:

- **Before the push** (tests, version check, package check, or "the release commit is not on main"): nothing reached nuget.org. Delete the Release and its tag on the website, fix the cause in a pull request, and create the Release again.
- **During or after the push** (for example one package pushed, the other not, or the Release upload failed): open the run and use **Re-run failed jobs**. It needs your approval again. A package that is already on nuget.org is skipped, and the files on the Release page are replaced, so the re-run is safe.
- **A wrong package reached nuget.org:** it cannot be deleted. Unlist it on nuget.org (it stays downloadable for projects that already use it, but search no longer shows it), fix the cause, and release a new version.

## SDK and target framework

`global.json` pins the .NET SDK. CI moves to a new major SDK only when a pull request changes that file.

The packages target `net10.0` only. A new .NET release does not change the target: a `net10.0` package also works in projects on newer .NET. Add a newer target only when the code needs an API from it. Remove `net10.0` only after .NET 10 is out of support, because removing a target breaks the projects that use it.

A pull request that changes the target must also update the stated requirement in `README.md`, `documentation/02-getting-started.md` and `documentation/03-api-reference.md`, and the expected `lib/net10.0/` folder in `build/Test-Packages.ps1` (the package check fails until it does).

# Contributing to ResilientRateLimiting

## Setup

You need the **.NET SDK that `global.json` names**, or a newer one of the same major version. An older SDK refuses to build this repository. To run the integration tests you also need **Docker**: they use [Testcontainers](https://testcontainers.com/) to start a real Redis.

Build with warnings as errors, the same way CI does:

```bash
dotnet build ResilientRateLimiting.slnx -warnaserror
```

## Running the tests

```bash
dotnet test
```

This runs three tiers:

- `ResilientRateLimiting.Tests` — unit tests for the core package. No Docker needed.
- `ResilientRateLimiting.AspNetCore.Tests` — unit tests for the ASP.NET Core package. No Docker needed.
- `ResilientRateLimiting.IntegrationTests` — tests against a real Redis, started by Testcontainers. Docker must be running.

Tests use plain xUnit `Assert`. Do not add assertion or mocking libraries.

## Conventions

- **`TimeProvider` is a constructor parameter, everywhere.** Any type that reads the clock takes a `TimeProvider` in its constructor; production code passes `TimeProvider.System`. If you add time-dependent code, give it a `TimeProvider` parameter too, and let tests pass a `FakeTimeProvider` instead of sleeping or waiting on a real clock.
- **Nothing on a request path may throw, including observability.** A metric, a log call, or a store failure callback must never turn into an exception that reaches the caller of `AcquireAsync`. If you add a call that watches or reports something, wrap it so a failure inside it cannot propagate.
- **Metric tests need a unique `PolicyName`.** The `Meter` this library uses is process-wide, and xUnit runs test classes in parallel. A test that reads metrics must give its limiter a `PolicyName` no other test uses, and filter the metric's tags by that name, or it will see data from a different test running at the same time.
- **Comments are plain language, one short line.** No formulas, no internal identifiers, no design rationale in a code comment; that reasoning belongs in `documentation/`. The one exception is XML docs on public members: those should be complete, because they are the reference a caller sees in their editor.

## Documentation and code snippets

Every C# or JSON code block in `README.md` and in `documentation/*.md` is a direct quote from a file under `samples/`, never hand-typed. This keeps the documentation from silently drifting away from code that compiles.

How it works:

- A **C# region** is marked in the sample with `// snippet: <name>` and `// end-snippet` around the lines to quote.
- A **whole JSON file** is quoted with `file:<path>` as its name instead of a region, for example `file:samples/ResilientRateLimiting.Samples.Web/appsettings.json`.
- In the Markdown page, the code block is preceded by `<!-- snippet: <name> -->` on its own line, directly above the ```` ```csharp ```` or ```` ```json ```` fence.

The test `Every_code_block_in_the_documentation_is_quoted_from_a_sample` (in `tests/ResilientRateLimiting.Tests`) reads every region from `samples/`, reads every code block in `README.md` and `documentation/*.md`, and fails if a block has no marker, names a snippet that does not exist, or no longer matches the region's current text. To add a new snippet: add the region (or the whole file) to a sample project first, keep the sample compiling, then quote it in the page with the matching marker. Run the test after any change to a sample or a documentation page:

```bash
dotnet test tests/ResilientRateLimiting.Tests --filter "FullyQualifiedName~DocumentationSnippet"
```

## Measurements

`documentation/measurements.md` holds numbers from real runs against a real Redis: memory per partition, store round-trip time, and so on. These are not run in CI, because the shared build machines are noisy and a benchmark run there would not be trustworthy. Run them by hand, on a quiet machine, with Docker running:

```bash
for i in 1 2 3; do dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- footprint-redis; done
for i in 1 2 3; do dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- footprint-memory; done
dotnet run -c Release --project benchmarks/ResilientRateLimiting.Measurements -- roundtrip
```

If a change could move one of these numbers (for example, a change to what `StoreHealth` stores per partition, or to the request path), rerun the affected mode and update `documentation/measurements.md` with the new numbers and the setup they were measured under.

## Building a release package

```bash
dotnet pack src/ResilientRateLimiting -c Release -o ./artifacts -p:ContinuousIntegrationBuild=true
dotnet pack src/ResilientRateLimiting.AspNetCore -c Release -o ./artifacts -p:ContinuousIntegrationBuild=true
```

`-p:ContinuousIntegrationBuild=true` makes the build deterministic and strips local file paths out of the debug symbols, so two people packing the same commit get byte-identical output, and a symbol file never leaks a path from the machine that built it.

## CI

GitHub Actions runs `.github/workflows/ci.yml`:

- **On every pull request and every push to `main`:** build with warnings as errors, run all three test tiers (the integration tier starts Redis in Docker on the build machine), pack both packages, and check them. The packages are kept as a download on the run's page, with a version like `0.1.0-ci.42`. They are never published.
- **The package check** (`build/Test-Packages.ps1`) fails the run if a package has a dependency other than the expected ones (`System.Threading.RateLimiting` and `Polly.Core` for the core package; the same-version `ResilientRateLimiting` for the ASP.NET Core package), is missing its symbols or README, or if there are not exactly two packages. `build/Test-BuildScripts.ps1` tests this check and the version script with fake input; run it after changing anything in `build/`:

  ```bash
  pwsh -NoProfile -File build/Test-BuildScripts.ps1
  ```

Releases are made by the maintainer; [RELEASING.md](RELEASING.md) describes how.

## Before you send a change

- `dotnet build ResilientRateLimiting.slnx -warnaserror` is clean.
- `dotnet test` is green (Docker running, so the integration tier runs too).
- If you changed anything in `build/`, `pwsh -NoProfile -File build/Test-BuildScripts.ps1` passes.
- New or changed code that reads the clock takes a `TimeProvider`.
- New or changed documentation quotes a sample, with a marker, and the snippet test passes.
- No decision identifiers, employer names, hostnames, tenant claim names, or real limit values anywhere in `src`, `tests`, `samples`, `benchmarks`, `documentation`, `README.md`, or this file.

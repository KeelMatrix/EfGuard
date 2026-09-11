# EfGuard development guide

## Navigation

- CLI and report contracts live in `src/KeelMatrix.EfGuard`.
- The isolated EF project loader lives in `src/KeelMatrix.EfGuard.Worker`.
- Semantic, configuration, process, and report tests live in `tests/KeelMatrix.EfGuard.Tests`.
- EF-version fixture projects live under `fixtures`.
- Rule documentation is under `docs/rules`.

## Commands

```text
dotnet restore KeelMatrix.EfGuard.sln
dotnet build KeelMatrix.EfGuard.sln -c Release --no-restore
dotnet test KeelMatrix.EfGuard.sln -c Release --no-build
dotnet format KeelMatrix.EfGuard.sln --verify-no-changes
dotnet pack src/KeelMatrix.EfGuard/KeelMatrix.EfGuard.csproj -c Release --no-restore
```

For the repository CI-equivalent validation path, use `pwsh ./build/validate.ps1` on Windows or `./build/validate.sh` on Linux/macOS. The scripts set `KEELMATRIX_NO_TELEMETRY=1`, run the release build/test/format gates, inspect the package contract, and install the freshly built package into an isolated consumer cache.

## Release finalization

Before creating a release tag, finalize the intended entry in `CHANGELOG.md`, commit and push that change, and run the same repository-controlled contract check on the exact commit that will be tagged:

```powershell
$version = "0.1.0"
$commit = (git rev-parse HEAD).Trim()
pwsh ./build/Validate-Changelog.ps1 -ExpectedVersion $version -ExpectedPackageVersion $version -ExpectedDependencyVersion $version -ExpectedCommit $commit -RequireChangelogInCommit
```

The check must pass before requesting release authorization or creating the tag. It verifies the finalized release date, changelog/package/tag/dependency/install-example versions, and the exact checked-out commit.

The package is a net8.0 .NET tool named `efguard`. The worker is an implementation detail and must stay out-of-process from the CLI. Release builds are deterministic and warnings-as-errors for shipping projects.

## Invariants

- EF project assemblies are loaded only by the worker and only after a bounded build/process step.
- Extraction failures and unknown migration operations fail closed.
- Baseline analysis uses an isolated temporary checkout and never writes the active worktree.
- The report schema, rule IDs, configuration version, options, and exit codes are stable v1 contracts.
- Telemetry is best-effort and never receives source, SQL, paths, names, provider, EF version, or findings.
- No database is connected to or modified by the tool.

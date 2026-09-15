# Contributing

Contributions are welcome as focused fixes, tests, and documentation improvements.

## Before You Begin

Enable the repository's local checks with `git config core.hooksPath .githooks`. Keep changes focused and do not include credentials, generated reports, or machine-specific paths.

## Validate Locally

Run the repository CI-equivalent path, which disables telemetry for validation:

```powershell
pwsh ./build/validate.ps1
```

On Linux/macOS:

```bash
./build/validate.sh
```

The canonical individual commands are:

```powershell
$env:MSBUILDDISABLENODEREUSE = "1"
dotnet restore KeelMatrix.EfGuard.sln -m:1 -nodeReuse:false
dotnet build KeelMatrix.EfGuard.sln -c Release --no-restore -m:1 -nodeReuse:false
dotnet test KeelMatrix.EfGuard.sln -c Release --no-build --no-restore -m:1 -nodeReuse:false
dotnet format KeelMatrix.EfGuard.sln --verify-no-changes --no-restore
dotnet pack src/KeelMatrix.EfGuard/KeelMatrix.EfGuard.csproj -c Release --no-build --no-restore -m:1 -nodeReuse:false
```

The pinned release install command in `src/KeelMatrix.EfGuard/README.md` is the repository's single canonical install example. Release-facing documents must link to it rather than repeat an unpinned or competing tool-install command. Local-feed and `--add-source` commands used by validation scripts are operational test commands, not release examples; `build/Validate-Changelog.ps1` enforces this distinction.

### MSB4166 node isolation

`MSB4166` can occur when a command-line build reuses a stale or concurrently owned MSBuild node, including one left by an open Visual Studio session. The reliable diagnostic and test path is:

```powershell
pwsh ./build/test.ps1 -Configuration Release
```

That entry point sets `MSBUILDDISABLENODEREUSE=1` and passes `-m:1 -nodeReuse:false` to each restore/build/test invocation, isolating fixture builds from the shared node pool.

### Reliable test path with Visual Studio open

Visual Studio and command-line builds can share the per-user MSBuild node-reuse pool. Use the committed test entry point when Visual Studio is open or when a machine has stale MSBuild nodes:

```powershell
pwsh ./build/test.ps1 -Configuration Release
```

On Linux/macOS, run the same PowerShell script after restoring the repository's PowerShell prerequisite. The script restores, builds the full solution, and then runs the tests with `MSBUILDDISABLENODEREUSE=1`, `-m:1`, and `-nodeReuse:false`. The environment setting is inherited by EfGuard's worker-launched build processes, so the test fixture build churn cannot rejoin Visual Studio's node pool.

### Visual Studio restore and build

After first opening the solution, wait for NuGet restore to finish before starting a Release build. Visual Studio can begin a parallel build while its background design-time restore is still running; the three intentional partial-assembly fixtures may then report `CS0234`/`CS0246` errors for Entity Framework Core references. Run Restore NuGet Packages, wait for completion, and build `Release|Any CPU` again. The fixtures are intentionally validated by the test suite with their missing dependencies, so these errors do not indicate an analyzer or product-code failure.

The web-host fixture projects may also cause Visual Studio to create `Properties/launchSettings.json`; these IDE-generated files are ignored by the repository.

Add meaningful positive and boundary regression coverage for every changed rule or provider behavior. Keep reports, fixtures, and documentation free of credentials and machine-specific paths. Update README/rule/schema documentation when behavior or a supported contract changes.

Changes to CLI options, JSON reports, `efguard.json`, exit codes, or rule IDs require compatibility review, deterministic fixture updates only when intentional, and a `CHANGELOG.md` entry. See [JSON-SCHEMA.md](docs/JSON-SCHEMA.md). For vulnerabilities, use the private route in [SECURITY.md](SECURITY.md), not a public issue.

## Submit a Pull Request

Describe the user-facing problem and the focused change, include the validation you ran, and update the applicable documentation. Follow the expectations in [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

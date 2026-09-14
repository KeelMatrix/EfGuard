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
dotnet restore KeelMatrix.EfGuard.sln
dotnet build KeelMatrix.EfGuard.sln -c Release --no-restore
dotnet test KeelMatrix.EfGuard.sln -c Release --no-build
dotnet format KeelMatrix.EfGuard.sln --verify-no-changes
dotnet pack src/KeelMatrix.EfGuard/KeelMatrix.EfGuard.csproj -c Release --no-build
```

### Visual Studio restore and build

After first opening the solution, wait for NuGet restore to finish before starting a Release build. Visual Studio can begin a parallel build while its background design-time restore is still running; the three intentional partial-assembly fixtures may then report `CS0234`/`CS0246` errors for Entity Framework Core references. Run Restore NuGet Packages, wait for completion, and build `Release|Any CPU` again. The fixtures are intentionally validated by the test suite with their missing dependencies, so these errors do not indicate an analyzer or product-code failure.

The web-host fixture projects may also cause Visual Studio to create `Properties/launchSettings.json`; these IDE-generated files are ignored by the repository.

Add meaningful positive and boundary regression coverage for every changed rule or provider behavior. Keep reports, fixtures, and documentation free of credentials and machine-specific paths. Update README/rule/schema documentation when behavior or a supported contract changes.

Changes to CLI options, JSON reports, `efguard.json`, exit codes, or rule IDs require compatibility review, deterministic fixture updates only when intentional, and a `CHANGELOG.md` entry. See [JSON-SCHEMA.md](docs/JSON-SCHEMA.md). For vulnerabilities, use the private route in [SECURITY.md](SECURITY.md), not a public issue.

## Submit a Pull Request

Describe the user-facing problem and the focused change, include the validation you ran, and update the applicable documentation. Follow the expectations in [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

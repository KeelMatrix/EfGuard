# Contributing

Contributions are welcome as focused fixes, tests, and documentation improvements.

## Before you begin

Enable the repository's local checks with `git config core.hooksPath .githooks`. Keep changes focused and do not include credentials, generated reports, or machine-specific paths.

## Validate locally

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

Add meaningful positive and boundary regression coverage for every changed rule or provider behavior. Keep reports, fixtures, and documentation free of credentials and machine-specific paths. Update README/rule/schema documentation when behavior or a supported contract changes.

Changes to CLI options, JSON reports, `efguard.json`, exit codes, or rule IDs require compatibility review, deterministic fixture updates only when intentional, and a `CHANGELOG.md` entry. See [JSON-SCHEMA.md](docs/JSON-SCHEMA.md). For vulnerabilities, use the private route in [SECURITY.md](SECURITY.md), not a public issue.

## Submit a pull request

Describe the user-facing problem and the focused change, include the validation you ran, and update the applicable documentation. Follow the expectations in [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

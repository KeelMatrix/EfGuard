# KeelMatrix.EfGuard

EfGuard is a local .NET tool for .NET teams using EF Core migrations in CI/CD. It compares EF application/schema generations, migration operations, and supported-provider SQL evidence to find rollout states that can break coexisting application versions, lose data, or block traffic before a migration merges.

## Install

Install the global tool with the pinned public release command:

```bash
dotnet tool install --global KeelMatrix.EfGuard --version 0.1.0
```

To update an existing global installation:

```bash
dotnet tool update --global KeelMatrix.EfGuard --version 0.1.0
```

To uninstall the global tool:

```bash
dotnet tool uninstall --global KeelMatrix.EfGuard
```

## Quick usage

From a straightforward single-project, single-context repository:

```bash
efguard check
```

For a rolling-deployment comparison with the previous application model, provide a local Git baseline:

```bash
efguard check --baseline origin/main
```

When discovery is ambiguous, select the project, startup project, and context explicitly:

```bash
efguard check --project src/Orders/Orders.csproj --startup-project src/Orders.Api/Orders.Api.csproj --context OrdersDbContext
```

Use `--format json` for CI automation. A normal workflow restores the selected project first, then runs EfGuard against that restored dependency graph:

```bash
dotnet restore src/Orders/Orders.csproj
efguard check --project src/Orders/Orders.csproj --baseline origin/main --format json
```

## Important limitations

- Analysis is local and offline: EfGuard does not connect to, modify, or apply changes to a database, and it does not require production database credentials. Restore the project yourself before running the tool; EfGuard does not perform an implicit package restore.
- Exit code `0` means trustworthy analysis completed with no configured blocking or unverified diagnostic. Exit code `1` means trustworthy analysis found a blocking or unverified finding. Exit code `2` means analysis could not complete trustworthily because of configuration, extraction, build, provider, baseline, or internal error.
- EF Core 8, 9, and 10 projects are supported when their own restored dependency graph can be extracted reliably. SQL Server/Azure SQL and PostgreSQL through Npgsql are the initial provider targets; other providers are not given provider-specific safety claims.
- Provider-locking findings are promoted by real SQL Server and PostgreSQL engine evidence run in CI service containers. Actual lock duration, workload impact, deployment ordering, and zero-downtime behavior cannot be proven statically, so EfGuard does not guarantee zero downtime.
- Unknown operations, raw SQL, missing compatibility history, and unsupported provider behavior remain unverified rather than being treated as safe. The tool does not automatically rewrite migrations, run hosted preflight checks, or provide a supported .NET library API.

## Documentation

- [Repository and full usage guide](https://github.com/KeelMatrix/EfGuard#readme)
- [JSON report and compatibility policy](https://github.com/KeelMatrix/EfGuard/blob/main/docs/JSON-SCHEMA.md)
- [Rule documentation](https://github.com/KeelMatrix/EfGuard/tree/main/docs/rules)
- [Security policy](https://github.com/KeelMatrix/EfGuard/blob/main/SECURITY.md)
- [Privacy and telemetry contract](https://github.com/KeelMatrix/EfGuard/blob/main/PRIVACY.md)
- [Contributing guide](https://github.com/KeelMatrix/EfGuard/blob/main/CONTRIBUTING.md)

EfGuard is MIT licensed. See the [license](https://github.com/KeelMatrix/EfGuard/blob/main/LICENSE).

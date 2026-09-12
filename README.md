# KeelMatrix.EfGuard

Your migration compiles. EF Core accepts it. Your rolling deployment can still fail. EfGuard compares application/schema generations and provider behavior to find dangerous rollout states before they merge.

EfGuard is a local .NET tool that analyzes EF Core migrations without connecting to or modifying a database. It reports compatibility, data-loss, and provider blocking risks for SQL Server/Azure SQL and PostgreSQL.

## Install

```bash
dotnet tool install --global KeelMatrix.EfGuard --version 0.1.0
efguard --help
```

Update or remove it with:

```bash
dotnet tool update --global KeelMatrix.EfGuard
dotnet tool uninstall --global KeelMatrix.EfGuard
```

## First scan

From a straightforward single-project, single-context repository:

```bash
efguard check
```

For a rolling-deployment comparison with the previous application model:

```bash
efguard check --baseline origin/main
```

Use explicit paths when discovery is not unambiguous:

```bash
efguard check --project src/Orders/Orders.csproj --startup-project src/Orders.Api/Orders.Api.csproj --context OrdersDbContext
```

With `--startup-project`, EfGuard follows the normal EF Core design-time startup path in a bounded child process: it resolves the startup host through `BuildWebHost`, `CreateWebHostBuilder`, `CreateHostBuilder`, or the startup entry point, then resolves the selected `DbContext` (or `IDbContextFactory<TContext>`) from the startup application's scoped services. An `IDesignTimeDbContextFactory<TContext>` and then a parameterless constructor are fallbacks when startup services do not provide the context. Keep all design-time startup code and factories free of production connections and side effects. The worker response file is limited to 4 MiB (4,194,304 bytes); an oversized response is an untrustworthy extraction failure and returns exit code `2`.

In repositories with more than one `DbContext`, `--context` also scopes migration discovery. EfGuard resolves the migrations of the selected context from EF Core's own migrations metadata (the `IMigrationsAssembly` service and the context's configured migrations assembly) instead of every loaded `Migration` subclass, so migrations that belong to another context are never analyzed for the selected context.

## Network and offline behavior

Core analysis is local and offline. EfGuard never restores packages and never contacts a package feed, so the dependency graph must come from a restore you already ran:

```bash
dotnet restore src/Orders/Orders.csproj
efguard check --project src/Orders/Orders.csproj
```

Extraction builds the selected project with `--no-restore` against that restored graph. When the graph is missing, EfGuard fails closed with exit code `2` and tells you to run `dotnet restore`; it never silently falls back to a network restore. `--baseline` reuses the same restored graph from your working tree inside an isolated temporary checkout, so the active worktree is never modified. The only normal network behavior is best-effort telemetry.

## Exit codes and output

- `0`: trustworthy analysis completed with no configured blocking or unverified diagnostic.
- `1`: trustworthy analysis completed with a blocking or unverified diagnostic.
- `2`: analysis could not complete trustworthily because of configuration, extraction, build, provider, baseline, or internal error.

Use `--format json` for automation. JSON has `schemaVersion: 1`, `provider`, `providerSql`, `compatibility`, `baseline`, `summary`, `diagnostics`, and `errors`. Each diagnostic contains `ruleId`, `title`, `riskDimensions`, `severity`, `confidence`, optional provider/migration/location, `affectedState`, `explanation`, `remediation`, and `uncertainty`.

`providerSql` records provider-generated SQL for the migration operations when the target provider exposes that service. Each statement carries its migration and operation ordinal, and provider-specific findings require exactly one matching operation-level statement with the expected SQL shape. `engineVerified` is true only when the repository's real database-engine integration gates have verified the provider behavior behind every provider finding in the report. EfGuard ships that evidence as a versioned provider engine evidence manifest, and it only promotes a provider-locking finding to `HIGH`/`high` confidence when the manifest covers the finding's rule and provider. Missing or unverified engine evidence leaves the finding `UNVERIFIED` instead of asserting a high-confidence provider claim.

## Configuration and suppressions

Create `efguard.json` at the repository root:

```json
{
  "version": 1,
  "deployment": {
    "strategy": "rolling",
    "minimumCompatibleVersions": 1
  },
  "rules": {
    "EFG302": "error"
  },
  "suppressions": [
    {
      "rule": "EFG101",
      "migration": "20260820153000_RemoveLegacyCode",
      "reason": "The old application version is retired before deployment",
      "expires": "2026-09-30"
    }
  ]
}
```

Severity values are `error`/`block`, `warning`/`high`, `info`/`advisory`, `unverified`, and `off`. Suppressions require a reason. Expired suppressions produce `EFG998` and do not hide the original finding. Configuration never accepts connection strings or credentials.

## Rules

| ID | Diagnostic | Default | Risk |
| --- | --- | --- | --- |
| [EFG101](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG101.md) | Rolling-deployment incompatibility | BLOCK | compatibility, data loss |
| [EFG102](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG102.md) | Required column may reject existing rows | BLOCK | compatibility, data loss |
| [EFG201](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG201.md) | Destructive column/table change | HIGH | data loss, rollback |
| [EFG202](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG202.md) | Unsafe column alteration | BLOCK | compatibility, data loss |
| [EFG204](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG204.md) | Unique index/constraint validation risk | HIGH | compatibility, blocking |
| [EFG301](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG301.md) | Potentially blocking SQL Server index creation | HIGH | blocking, provider |
| [EFG302](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG302.md) | Write-blocking PostgreSQL index creation | HIGH | blocking, provider |
| [EFG303](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG303.md) | Foreign-key validation and locking risk | HIGH | compatibility, blocking |
| [EFG304](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG304.md) | Unbounded data backfill | HIGH | blocking, data loss |
| [EFG305](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG305.md) | Transaction semantics require review | HIGH | blocking, provider |
| [EFG399](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG399.md) | Unverified operation or compatibility history | UNVERIFIED | compatibility, provider |
| [EFG900](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG900.md) | Unsupported database provider | UNVERIFIED | provider |
| [EFG998](https://github.com/KeelMatrix/EfGuard/blob/main/docs/rules/EFG998.md) | Expired suppression | BLOCK | configuration |

Unknown operations, raw SQL, and custom operations are never silently treated as safe. Transaction suppression is an independent risk dimension: arbitrary SQL retains EFG399 and may also report EFG305, while a classified unbounded backfill may report EFG304 and EFG305 together. SQL is classified locally and is not included in telemetry. Provider lock behavior depends on engine version, capabilities, and workload; EfGuard cannot guarantee zero downtime.

Provider behavior claims (EFG301, EFG302, and EFG303) are promoted to `HIGH`/`high` confidence only when the repository's real SQL Server and PostgreSQL integration gates have verified that behavior and the shipped provider engine evidence covers the provider. Those gates execute the provider-generated statements against real engines and record the verified claims; unverified claims stay `UNVERIFIED` and normally produce exit code `1`.

## Rollout guidance

Prefer expand, transition, cutover, and contract stages. Add compatible schema first, deploy code that can read both generations, backfill in bounded/resumable batches, switch reads/writes, and remove old schema only after old instances are retired. Provider-specific online or concurrent options still require operational validation.

The baseline matrix is evaluated from extracted EF models and is included in JSON as `compatibility`: previous application + previous schema, previous application + target schema, current application + previous schema, and current application + target schema. With `rolling`, both overlap states are blocking when the evidence shows incompatibility. With `expand-contract`, previous-application + target-schema is advisory because the strategy declares a staged contract order; current-application + previous-schema is not required. With `blue-green`, neither overlap state is required because the strategy declares isolated application/schema cutover. These strategy verdicts still do not inspect live traffic, queries, or deployment ordering.

For the default `rolling` policy, a scan without `--baseline` emits EFG399 because compatibility with the previous application/schema generation has no evidence; it cannot exit cleanly unless EFG399 is explicitly overridden or disabled. When `minimumCompatibleVersions` is greater than one, one `--baseline` reference is also insufficient to prove the requested history. With the default one-generation policy, a compatible supplied baseline remains clean. Without a baseline, EfGuard still analyzes only the latest discovered migration, so operation-level diagnostics are not masked by the history finding. With a baseline, it analyzes migrations present in the current extraction but absent from the baseline extraction, treating those as the pending change set.

## Supported matrix

The CLI targets `net8.0`. Extraction supports EF Core 8, 9, and 10 projects when their own restored dependency graph can be built. Customer projects targeting `net8.0`, `net9.0`, or `net10.0` use the matching isolated worker. Supported providers are Microsoft SQL Server/Azure SQL and Npgsql PostgreSQL. Unsupported providers return an unverified provider result and exit `2`; EfGuard does not guess provider locking behavior.

The compatibility fixture matrix exercises each supported EF/provider family:

| EF Core | SQL Server/Azure SQL | PostgreSQL/Npgsql |
| --- | --- | --- |
| 8 | covered | covered |
| 9 | covered | covered |
| 10 | covered | covered |

## Troubleshooting

- **Project or `DbContext` discovery is ambiguous:** use `--project path/to/App.csproj`, `--startup-project path/to/App.Api.csproj`, and `--context AppDbContext`. When automatic discovery fails, the error identifies the missing or ambiguous selection and shows the corrective option shape.
- **The startup services, design-time factory, or context constructor fails:** run the same design-time path locally, keep it free of production connections and side effects, and ensure the selected startup project is restored and buildable. EfGuard resolves startup services first, then falls back to the design-time factory and parameterless constructor inside a bounded child process; a failure returns exit code `2`.
- **The baseline Git reference cannot be read:** verify the ref exists locally, the selected project is present at that ref, and the repository has a usable Git checkout. Baseline extraction is isolated and never rewrites the active worktree; failure returns exit code `2`.
- **The dependency graph is not restored:** run `dotnet restore` for the selected project and its referenced projects first. EfGuard never restores packages and never contacts a package feed; a missing restored graph is reported as a clear extraction failure with exit code `2`. If `--baseline` reports the same failure, the project's restored graph is missing from your working tree or the baseline dependency set is not available offline.
- **Extraction times out or exceeds the output limit:** reduce the selected project/startup scope and design-time logging. Worker execution is bounded and response output is limited to 4 MiB; either condition is an untrustworthy extraction failure and returns exit code `2`.
- **The provider is unsupported:** SQL Server/Azure SQL and PostgreSQL through Npgsql are supported in v1. Other providers produce EFG900 and exit code `2`; no provider-locking claim is inferred.
- **The report contains `UNVERIFIED`:** inspect EFG399 or the provider diagnostic's uncertainty text. Unknown operations and provider findings without generated-SQL evidence fail closed and normally produce exit code `1`; review the migration and provider evidence before overriding policy.
- **Configuration is malformed:** `efguard.json` must use version `1`, documented property names, known rule IDs, non-empty suppression reasons, and `yyyy-MM-dd` expiry dates. Unknown properties and rule IDs are rejected with an actionable error and exit code `2`.
- **The exit code is unexpected:** `0` means a trustworthy clean result, `1` means a trustworthy blocking or unverified result, and `2` means analysis could not complete trustworthily. In CI, treat all non-zero codes as failures unless the job intentionally evaluates the JSON report.

## Contracts and documentation

- [JSON report and compatibility policy](https://github.com/KeelMatrix/EfGuard/blob/main/docs/JSON-SCHEMA.md)
- [Security policy](https://github.com/KeelMatrix/EfGuard/blob/main/SECURITY.md)
- [Privacy and shared telemetry contract](https://github.com/KeelMatrix/EfGuard/blob/main/PRIVACY.md)
- [Rule documentation](https://github.com/KeelMatrix/EfGuard/tree/main/docs/rules)
- [Contributing](https://github.com/KeelMatrix/EfGuard/blob/main/CONTRIBUTING.md)
- [Code of Conduct](https://github.com/KeelMatrix/EfGuard/blob/main/CODE_OF_CONDUCT.md)

## CI

Install from the configured package source, then invoke the CLI directly:

```bash
dotnet tool install --global KeelMatrix.EfGuard --version 0.1.0 --add-source https://api.nuget.org/v3/index.json
efguard check --baseline origin/main --format json > efguard-report.json
status=$?
test $status -eq 0
```

On Windows PowerShell, inspect `$LASTEXITCODE` instead of `test`. A gating job should fail for either non-zero exit code.

## Telemetry and privacy

After a trustworthy scan, EfGuard uses `KeelMatrix.Telemetry` for one anonymous activation and at most one weekly heartbeat. Telemetry does not receive source, generated SQL, schema names, paths, project names, provider/EF versions, findings, or credentials. It is best effort and cannot change analysis. Disable it for local development and company CI with `KEELMATRIX_NO_TELEMETRY=1`.

For repository validation with telemetry explicitly disabled, run `pwsh ./build/validate.ps1` on Windows or `./build/validate.sh` on Linux/macOS. This controlled path runs restore, Release build, tests, formatting, package-contract inspection, and an isolated installed-package consumer smoke test.

## License

MIT. See [LICENSE](https://github.com/KeelMatrix/EfGuard/blob/main/LICENSE).

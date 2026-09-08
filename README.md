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

The tool executes the selected project's normal design-time context construction in a bounded child process. Keep design-time factories free of production connections and side effects.

## Exit codes and output

- `0`: trustworthy analysis completed with no configured blocking or unverified diagnostic.
- `1`: trustworthy analysis completed with a blocking or unverified diagnostic.
- `2`: analysis could not complete trustworthily because of configuration, extraction, build, provider, baseline, or internal error.

Use `--format json` for automation. JSON has `schemaVersion: 1`, `provider`, `baseline`, `summary`, `diagnostics`, and `errors`. Each diagnostic contains `ruleId`, `title`, `riskDimensions`, `severity`, `confidence`, optional provider/migration/location, `affectedState`, `explanation`, `remediation`, and `uncertainty`.

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
| EFG101 | Rolling-deployment incompatibility | BLOCK | compatibility, data loss |
| EFG102 | Required column may reject existing rows | BLOCK | compatibility, data loss |
| EFG201 | Destructive column/table change | HIGH | data loss, rollback |
| EFG202 | Unsafe column alteration | BLOCK | compatibility, data loss |
| EFG204 | Unique index validation risk | HIGH | compatibility, blocking |
| EFG301 | Potentially blocking SQL Server index creation | HIGH | blocking, provider |
| EFG302 | Write-blocking PostgreSQL index creation | HIGH | blocking, provider |
| EFG303 | Foreign-key validation and locking risk | HIGH | compatibility, blocking |
| EFG304 | Unbounded data backfill | HIGH | blocking, data loss |
| EFG305 | Transaction semantics require review | HIGH | blocking, provider |
| EFG399 | Unverified migration operation | UNVERIFIED | compatibility, provider |
| EFG900 | Unsupported database provider | UNVERIFIED | provider |
| EFG998 | Expired suppression | BLOCK | configuration |

Unknown operations, raw SQL, and custom operations are never silently treated as safe. SQL is classified locally and is not included in telemetry. Provider lock behavior depends on engine version, capabilities, and workload; EfGuard cannot guarantee zero downtime.

## Rollout guidance

Prefer expand, transition, cutover, and contract stages. Add compatible schema first, deploy code that can read both generations, backfill in bounded/resumable batches, switch reads/writes, and remove old schema only after old instances are retired. Provider-specific online or concurrent options still require operational validation.

The baseline matrix is evaluated from extracted EF models: previous application + previous schema, previous application + target schema, current application + previous schema, and current application + target schema. In v1, the explicit previous-application + target-schema compatibility state is blocking when evidence is available.

## Supported matrix

The CLI targets `net8.0`. Extraction supports EF Core 8, 9, and 10 projects when their own restored dependency graph can be built. Supported providers are Microsoft SQL Server/Azure SQL and Npgsql PostgreSQL. Unsupported providers return an unverified provider result and exit `2`; EfGuard does not guess provider locking behavior.

## CI

Install from the configured package source, then invoke the CLI directly:

```bash
dotnet tool install --global KeelMatrix.EfGuard --version 0.1.0 --add-source https://api.nuget.org/v3/index.json
efguard check --baseline origin/main --format json > efguard-report.json
test $? -ne 2
```

On Windows PowerShell, inspect `$LASTEXITCODE` instead of `test`. Do not make a CI job treat exit `2` as clean.

## Telemetry and privacy

After a trustworthy scan, EfGuard uses `KeelMatrix.Telemetry` for one anonymous activation and at most one weekly heartbeat. Telemetry does not receive source, generated SQL, schema names, paths, project names, provider/EF versions, findings, or credentials. It is best effort and cannot change analysis. Disable it for local development and company CI with `KEELMATRIX_NO_TELEMETRY=1`.

## License

MIT. See [LICENSE](LICENSE).

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

The first release remains unpublished.

### Fixed

- Extraction tolerates peripheral assemblies whose metadata cannot be enumerated. EF Core 8, 9, and 10 projects that reference the standard `Microsoft.EntityFrameworkCore.Design` package, including when only a separate `--startup-project` references it, no longer fail the whole scan with exit code `2`: the worker enumerates types per copied assembly, skips design-time, build, and Roslyn assemblies that are not loadable in its own process, and records what it skipped. The fail-closed guarantee still applies to the assemblies EfGuard must read, and an unreadable assembly that declares the selected `DbContext` or its migrations now fails closed with the assembly name, the underlying exception, and a next step instead of the generic read-model error.
- `--context` now scopes migration discovery to the migrations EF Core attributes to the selected `DbContext`: extraction reads EF Core's own migrations metadata and never falls back to every `Migration` subclass in the migrations assembly, so migrations that belong to another context are no longer analyzed for the selected context. A context without attributed migrations reports zero operations, and migration classes that carry no matching `[DbContext]` attribute fail closed with an actionable diagnostic instead of silently producing a clean scan.
- Extraction builds the selected project with `--no-restore` against the dependency graph the caller already restored, never performs an implicit network restore, and fails with an actionable diagnostic when that graph is missing. Baseline analysis reuses the same restored graph from an isolated temporary checkout.
- `EFG202` now detects unbounded-to-bounded text narrowing, including SQL Server `nvarchar(max)` to `nvarchar(32)` and PostgreSQL `text` to `character varying(32)`.
- Provider-behavior findings (EFG301, EFG302, EFG303) are reported with `HIGH`/`high` confidence only when real SQL Server and PostgreSQL engine integration evidence covers the claim. Without that evidence they are reported as `UNVERIFIED`, and `providerSql.engineVerified` stays `false`.

## [0.1.0] (Unreleased)

### Added

- `KeelMatrix.EfGuard` .NET tool installation with the `efguard` command.
- `efguard check` for automatic discovery and explicit `--project`, `--startup-project`, and `--context` selection, plus `efguard check --baseline <git-ref>` for baseline-aware compatibility analysis.
- Out-of-process EF model and migration extraction for EF Core 8, 9, and 10 projects using SQL Server/Azure SQL and Npgsql/PostgreSQL provider graphs.
- Provider-neutral risk analysis for destructive changes, rolling-deployment incompatibilities, narrowing and required-column changes, indexes and constraints, backfills, transaction behavior, and unverified custom SQL or operations.
- Console reports render the reason, affected state, risk dimensions, provider evidence, remediation guidance, and uncertainty; versioned JSON reports retain the same stable diagnostic contract and exit codes `0` (no configured blocking finding), `1` (configured blocking finding), and `2` (analysis could not complete trustworthily).
- Versioned `efguard.json` configuration for deployment strategy, rule severity overrides, suppressions, required suppression reasons, and optional suppression expiry.

### Privacy

- Analysis runs locally without database credentials or schema changes. Best-effort telemetry is limited to the shared activation/heartbeat contract, is disabled for KeelMatrix development and CI with `KEELMATRIX_NO_TELEMETRY=1`, and does not transmit source, SQL, schema details, paths, provider names, or findings.

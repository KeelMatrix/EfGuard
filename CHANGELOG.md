# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

The initial `0.1.0` release is not yet published.

### Added

- `KeelMatrix.EfGuard` .NET tool installation with the `efguard` command.
- `efguard check` supports automatic discovery, explicit `--project`, `--startup-project`, and `--context` selection, and baseline-aware compatibility analysis with `--baseline <git-ref>`.
- Out-of-process extraction supports EF Core 8, 9, and 10 projects using SQL Server/Azure SQL and Npgsql/PostgreSQL provider graphs. The selected project is built with `--no-restore` against the caller's dependency graph, without an implicit network restore. `--context` scopes migration discovery to migrations attributed to the selected context; unreadable assemblies required for that context or its migrations fail closed with actionable detail, while unrelated unreadable assemblies are tolerated.
- Deterministic provider-neutral analysis covers destructive changes, rolling-deployment compatibility, narrowing and required-column changes, indexes and constraints, backfills, transaction behavior, and unverified custom SQL or operations. Provider-behavior findings carry `HIGH`/`high` confidence only with real SQL Server or PostgreSQL engine evidence; otherwise they are `UNVERIFIED`. Unbounded-to-bounded text narrowing is detected.
- Console and versioned JSON reports provide reasons, affected states, risk dimensions, provider evidence, remediation guidance, uncertainty, and stable exit codes: `0` for no configured blocking or unverified diagnostic, `1` for a blocking or unverified diagnostic, and `2` when analysis cannot complete trustworthily.
- Versioned `efguard.json` configuration for deployment strategy, rule severity overrides, suppressions, required suppression reasons, and optional suppression expiry.

- Analysis runs locally without database credentials or schema changes. Best-effort telemetry uses only the shared activation/heartbeat contract, is disabled for KeelMatrix development and CI with `KEELMATRIX_NO_TELEMETRY=1`, and does not transmit source, SQL, schema details, paths, provider names, or findings.

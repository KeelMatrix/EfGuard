# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

The first release remains unpublished.

## [0.1.0] (Unreleased)

### Added

- `KeelMatrix.EfGuard` .NET tool installation with the `efguard` command.
- `efguard check` for automatic discovery and explicit `--project`, `--startup-project`, and `--context` selection, plus `efguard check --baseline <git-ref>` for baseline-aware compatibility analysis.
- Out-of-process EF model and migration extraction for EF Core 8, 9, and 10 projects using SQL Server/Azure SQL and Npgsql/PostgreSQL provider graphs.
- Provider-neutral risk analysis for destructive changes, rolling-deployment incompatibilities, narrowing and required-column changes, indexes and constraints, backfills, transaction behavior, and unverified custom SQL or operations.
- Console reports and versioned JSON reports with stable rule IDs, risk dimensions, provider evidence, remediation guidance, and exit codes `0` (no configured blocking finding), `1` (configured blocking finding), and `2` (analysis could not complete trustworthily).
- Versioned `efguard.json` configuration for deployment strategy, rule severity overrides, suppressions, required suppression reasons, and optional suppression expiry.

### Privacy

- Analysis runs locally without database credentials or schema changes. Best-effort telemetry is limited to the shared activation/heartbeat contract, is disabled for KeelMatrix development and CI with `KEELMATRIX_NO_TELEMETRY=1`, and does not transmit source, SQL, schema details, paths, provider names, or findings.

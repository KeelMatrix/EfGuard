# JSON report schema v1

`efguard check --format json` emits one object with these top-level fields:

- `schemaVersion`: integer `1`.
- `toolVersion`: tool version.
- `provider`: provider identity when extraction succeeded.
- `providerSql`: `available`, `engineVerified`, `source`, and generated migration SQL `statements` when the provider exposes migration SQL generation. SQL is local report evidence and is never sent through telemetry.
- `compatibility`: the configured `strategy`, `minimumCompatibleVersions`, all four model-state booleans, and the states evaluated for blocking verdicts.
- `baseline`: `requested`, `reference`, and `available`.
- `summary`: operation and severity counts plus `exitCode`.
- `diagnostics`: stable rule findings. Suppressed findings remain present with `suppressed: true`.
- `errors`: non-sensitive execution errors.

Diagnostic fields are `ruleId`, `title`, `riskDimensions`, `severity`, `confidence`, `provider`, `migration`, `location`, `affectedState`, `explanation`, `remediation`, `uncertainty`, and `suppressed`. `severity` values are `advisory`, `high`, `block`, and `unverified`; `confidence` values are `high`, `medium`, and `unknown`.

## Compatibility policy

The JSON report and `efguard.json` configuration are versioned v1 contracts. Their version fields are intentionally separate from the tool version: `schemaVersion` identifies the report shape and `version` identifies the configuration shape. Rule IDs, their documented meanings, CLI option names, and exit codes `0`, `1`, and `2` are stable v1 contracts.

Additive report fields, additive configuration fields that have safe defaults, new diagnostics, and new CLI options are compatible changes. A consumer must ignore unknown JSON fields. Removing or renaming a report/configuration field, changing its type or meaning, changing a rule ID or documented severity semantics, removing or renaming a CLI option, or changing exit-code meaning is breaking and requires a new major contract version and migration guidance.

Any incompatible report or configuration change requires a schema/version bump, an entry in `CHANGELOG.md`, updated examples and fixtures, and a documented migration path. Deprecated fields, options, or rule aliases remain accepted for at least one supported release when practical, are documented as deprecated, and are not silently repurposed. Deprecation and removal are recorded in the changelog. Current v1 validation rejects unknown configuration properties and rule IDs so likely typos fail clearly; this is intentional and is not a forward-compatibility escape hatch.

Compatibility fixtures under `tests/Fixtures` are committed v1 evidence. Tests compare deterministic serialization to those fixtures and never rewrite them automatically; regeneration, when needed for an intentional contract update, must be an explicit reviewed change.

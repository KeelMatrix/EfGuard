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

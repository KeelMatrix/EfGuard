# JSON report schema v1

`efguard check --format json` emits one object with these top-level fields:

- `schemaVersion`: integer `1`.
- `toolVersion`: tool version.
- `provider`: provider identity when extraction succeeded.
- `baseline`: `requested`, `reference`, and `available`.
- `summary`: operation and severity counts plus `exitCode`.
- `diagnostics`: stable rule findings. Suppressed findings remain present with `suppressed: true`.
- `errors`: non-sensitive execution errors.

Diagnostic fields are `ruleId`, `title`, `riskDimensions`, `severity`, `confidence`, `provider`, `migration`, `location`, `affectedState`, `explanation`, `remediation`, `uncertainty`, and `suppressed`. `severity` values are `advisory`, `high`, `block`, and `unverified`; `confidence` values are `high`, `medium`, and `unknown`.

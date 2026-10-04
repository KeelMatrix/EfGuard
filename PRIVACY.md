# Privacy

EfGuard performs analysis locally. After a trustworthy scan completes with exit code `0` or `1`, EfGuard requests activation and heartbeat telemetry through `KeelMatrix.Telemetry`. An incomplete scan (exit code `2`) does not request telemetry. The shared client owns event fields, opt-out handling, cadence, state, queueing, and delivery; see the [KeelMatrix.Telemetry README](https://github.com/KeelMatrix/Telemetry#readme) and [Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md) for that contract.

## EfGuard-Specific Collection and Exclusions

EfGuard does not pass migration source, generated SQL, schema names, analyzed project or repository names, file paths, database contents, connection strings, provider or EF version, rule IDs, findings, context names, or credentials to the shared client. Set `KEELMATRIX_NO_TELEMETRY=1` to disable process telemetry. See the shared privacy policy for available opt-out controls and their behavior. Local development and CI use this variable.

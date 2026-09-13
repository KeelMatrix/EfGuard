# Privacy

EfGuard performs analysis locally. After a trustworthy completed scan, the shared `KeelMatrix.Telemetry` dependency may record one anonymous activation and at most one weekly heartbeat. Delivery is best effort and telemetry failure cannot change analysis. The shared telemetry implementation and its source-of-truth contract are maintained in the [KeelMatrix.Telemetry README](https://github.com/KeelMatrix/Telemetry#readme) and [Telemetry privacy policy](https://github.com/KeelMatrix/Telemetry/blob/main/PRIVACY.md); this file does not duplicate those changing implementation details.

## EfGuard-Specific Collection and Exclusions

EfGuard does not send migration source, generated SQL, schema names, project or repository names, file paths, database contents, connection strings, provider or EF version, rule IDs, findings, context names, or credentials. Set `KEELMATRIX_NO_TELEMETRY=1` to disable process telemetry; repository-local opt-out supported by the shared dependency is also honored. Local development and CI use this variable.

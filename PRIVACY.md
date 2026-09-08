# Privacy

EfGuard performs analysis locally. It does not upload migration source, generated SQL, schema names, project or repository names, file paths, database contents, connection strings, provider or EF version, rule IDs, or findings.

After a trustworthy completed scan, the optional `KeelMatrix.Telemetry` dependency may record an anonymous activation and a maximum of one weekly heartbeat. Delivery is best effort. Set `KEELMATRIX_NO_TELEMETRY=1` to disable process telemetry; repository-local opt-out supported by the dependency is also honored. Company development and CI runs should set that variable.

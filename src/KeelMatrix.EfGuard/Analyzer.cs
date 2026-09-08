namespace KeelMatrix.EfGuard;

internal static class Analyzer
{
    internal static Report Analyze(ExtractionResult current, ExtractionResult? baseline, GuardConfig config, string? baselineReference)
    {
        Report report = new()
        {
            Provider = current.Provider,
            ProviderSql = current.ProviderSql,
            Baseline = new BaselineReport { Requested = baselineReference is not null, Reference = baselineReference, Available = baseline is not null },
            Compatibility = BuildCompatibilityMatrix(current, baseline, config)
        };

        if (!current.Success)
        {
            report.Errors.Add(current.Error ?? "EF extraction did not complete.");
            report.Summary.ExitCode = 2;
            return report;
        }

        if (!current.ProviderSupported)
        {
            report.Errors.Add("The project's database provider is not supported by this version of EfGuard.");
            report.Diagnostics.Add(Create(
                "EFG900", "Unsupported database provider", FindingSeverity.Unverified, FindingConfidence.Unknown,
                ["provider"], current.Provider, null, null,
                "The EF provider was identified, but EfGuard has no provider-specific rule set for it.",
                "Use Microsoft SQL Server/Azure SQL or Npgsql PostgreSQL for v1 analysis.",
                "Provider-specific locking and compatibility behavior is unknown."));
            report.Summary.ExitCode = 2;
            return report;
        }

        if (baseline is not null)
        {
            AddFindings(report, current.Provider, config, AnalyzeCompatibility(current.Model, baseline.Model, config.Strategy));
        }

        if (config.Strategy.Equals("rolling", StringComparison.OrdinalIgnoreCase) && config.MinimumCompatibleVersions > 1)
        {
            string historyExplanation = baseline is null
                ? $"The rolling policy requires compatibility with {config.MinimumCompatibleVersions} previous application versions, but this scan has no baseline history evidence."
                : $"The rolling policy requires compatibility with {config.MinimumCompatibleVersions} previous application versions, but this scan provides only one baseline model.";
            AddFindings(report, current.Provider, config, [Create(
                "EFG399", "Insufficient compatibility history", FindingSeverity.Unverified, FindingConfidence.Unknown,
                ["compatibility"], current.Provider, null, null,
                historyExplanation,
                "Provide baseline history that represents the required compatibility generations or lower the policy to the evidence available to this scan.",
                "EfGuard does not reconstruct application generations that are absent from the selected evidence.")]);
        }

        List<NormalizedOperation> operations = SelectRelevantOperations(current.Operations, baseline, baselineReference);
        foreach (NormalizedOperation operation in operations)
        {
            List<Diagnostic> findings = AnalyzeOperation(
                operation,
                current.Provider!,
                baseline?.Model,
                config.Strategy.Equals("rolling", StringComparison.OrdinalIgnoreCase),
                current.ProviderSql);
            if (findings.Count == 0)
            {
                report.Summary.Compatible++;
                continue;
            }

            foreach (Diagnostic finding in findings)
            {
                AddFindings(report, current.Provider, config, [finding]);
            }
        }

        report.Summary.OperationsInspected = operations.Count;
        report.Summary.Advisory = report.Diagnostics.Count(d => !d.Suppressed && d.Severity == FindingSeverity.Advisory);
        report.Summary.High = report.Diagnostics.Count(d => !d.Suppressed && d.Severity == FindingSeverity.High);
        report.Summary.Blocking = report.Diagnostics.Count(d => !d.Suppressed && d.Severity == FindingSeverity.Block);
        report.Summary.Unverified = report.Diagnostics.Count(d => !d.Suppressed && d.Severity == FindingSeverity.Unverified);
        report.Summary.ExitCode = report.Errors.Count > 0 || baselineReference is not null && baseline is null
            ? 2
            : report.Summary.Blocking > 0 || report.Summary.Unverified > 0 ? 1 : 0;
        return report;
    }

    private static void AddFindings(Report report, string? provider, GuardConfig config, IEnumerable<Diagnostic> findings)
    {
        foreach (Diagnostic finding in findings)
        {
            if (config.RuleSeverities.TryGetValue(finding.RuleId, out FindingSeverity configured))
                finding.Severity = configured;

            if (config.DisabledRules.Contains(finding.RuleId))
                finding.Suppressed = true;

            Suppression? suppression = config.Suppressions.FirstOrDefault(s =>
                s.Rule.Equals(finding.RuleId, StringComparison.OrdinalIgnoreCase)
                && (s.Migration is null || s.Migration.Equals(finding.Migration, StringComparison.OrdinalIgnoreCase)));
            if (suppression is not null && suppression.Expires is not null && suppression.Expires.Value < DateOnly.FromDateTime(DateTime.UtcNow))
            {
                report.Diagnostics.Add(Create(
                    "EFG998", "Expired suppression", FindingSeverity.Block, FindingConfidence.High,
                    ["configuration"], provider, finding.Migration, finding.Location,
                    $"Suppression for {finding.RuleId} expired on {suppression.Expires:yyyy-MM-dd}.",
                    "Renew the suppression only with a current, documented rollout reason, or fix the finding.",
                    "The original diagnostic remains active."));
            }
            else if (suppression is not null)
            {
                finding.Suppressed = true;
            }

            report.Diagnostics.Add(finding);
        }
    }

    private static CompatibilityMatrix BuildCompatibilityMatrix(ExtractionResult current, ExtractionResult? baseline, GuardConfig config)
    {
        CompatibilityMatrix matrix = new() { Strategy = config.Strategy, MinimumCompatibleVersions = config.MinimumCompatibleVersions };
        if (baseline is null)
            return matrix;

        matrix.PreviousApplicationTargetSchema = ModelsCompatible(baseline.Model, current.Model);
        matrix.CurrentApplicationPreviousSchema = ModelsCompatible(current.Model, baseline.Model);
        matrix.EvaluatedStates.Add("Previous application + previous schema");
        matrix.EvaluatedStates.Add("Current application + target schema");
        if (config.Strategy.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            matrix.EvaluatedStates.Add("Previous application + target schema");
            matrix.EvaluatedStates.Add("Current application + previous schema");
        }
        else if (config.Strategy.Equals("expand-contract", StringComparison.OrdinalIgnoreCase))
        {
            matrix.EvaluatedStates.Add("Previous application + target schema");
        }
        return matrix;
    }

    private static List<Diagnostic> AnalyzeCompatibility(ModelSnapshot current, ModelSnapshot baseline, string strategy)
    {
        List<Diagnostic> findings = [];
        bool previousTargetCompatible = ModelsCompatible(baseline, current);
        bool currentPreviousCompatible = ModelsCompatible(current, baseline);
        bool rolling = strategy.Equals("rolling", StringComparison.OrdinalIgnoreCase);
        bool expandContract = strategy.Equals("expand-contract", StringComparison.OrdinalIgnoreCase);

        if (!previousTargetCompatible && (rolling || expandContract))
        {
            findings.Add(Create(
                "EFG101", "Rolling-deployment incompatibility", rolling ? FindingSeverity.Block : FindingSeverity.Advisory,
                rolling ? FindingConfidence.High : FindingConfidence.Medium,
                ["compatibility"], null, null, null,
                "The previous application model is not compatible with the target schema during the configured deployment sequence.",
                expandContract
                    ? "Treat this as a contract step only after the previous application generation is retired; keep expansion and transition changes separate."
                    : "Use an expand/transition/contract rollout and deploy a compatible application before applying the contract change.",
                "Model comparison does not inspect live queries, data, traffic, or deployment ordering.",
                "Previous application + target schema"));
        }

        if (!currentPreviousCompatible && rolling)
        {
            findings.Add(Create(
                "EFG101", "Rolling-deployment incompatibility", FindingSeverity.Block, FindingConfidence.High,
                ["compatibility"], null, null, null,
                "The current application model requires schema elements that are absent from the previous schema.",
                "Apply an additive expansion first, then deploy the application generation that requires the new schema.",
                "Model comparison does not inspect live queries, data, traffic, or deployment ordering.",
                "Current application + previous schema"));
        }
        return findings;
    }

    private static List<NormalizedOperation> SelectRelevantOperations(List<NormalizedOperation> operations, ExtractionResult? baseline, string? baselineReference)
    {
        if (operations.Count == 0)
            return [];

        if (baselineReference is not null && baseline is not null)
        {
            HashSet<string> baselineMigrations = baseline.Operations
                .Where(operation => operation.Migration is not null)
                .Select(operation => operation.Migration!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return operations.Where(operation => operation.Migration is null || !baselineMigrations.Contains(operation.Migration)).ToList();
        }

        string? latestMigration = operations
            .Select(operation => operation.Migration)
            .Where(migration => migration is not null)
            .OrderByDescending(migration => migration, StringComparer.Ordinal)
            .FirstOrDefault();
        return latestMigration is null
            ? operations
            : operations.Where(operation => operation.Migration?.Equals(latestMigration, StringComparison.OrdinalIgnoreCase) == true).ToList();
    }

    private static List<Diagnostic> AnalyzeOperation(NormalizedOperation op, string provider, ModelSnapshot? baseline, bool enforceOverlap, ProviderSqlEvidence providerSql)
    {
        List<Diagnostic> findings = [];
        bool oldColumnExists = baseline is not null && ContainsColumn(baseline, op.Schema, op.Table, op.Column);
        bool oldTableExists = baseline is not null && ContainsTable(baseline, op.Schema, op.Table);

        switch (op.Kind)
        {
            case "drop-column":
                if (enforceOverlap && oldColumnExists)
                    findings.Add(Create("EFG101", "Rolling-deployment incompatibility", FindingSeverity.Block, FindingConfidence.High,
                        ["compatibility", "data-loss"], provider, op.Migration, Location(op),
                        $"{op.Table}.{op.Column} exists in the baseline EF model but is removed by the target migration.",
                        "Release an application version that no longer reads or writes the column, wait for older instances to retire, then remove it in a later contract migration.",
                        "Without baseline evidence, old application compatibility cannot be assessed."));
                findings.Add(Create("EFG201", "Destructive column change", FindingSeverity.High, FindingConfidence.High,
                    ["data-loss", "rollback"], provider, op.Migration, Location(op),
                    $"The migration permanently removes column {op.Table}.{op.Column}.",
                    "Use an expand/transition/contract rollout and preserve a recoverable copy before the contract step.",
                    "The analysis does not inspect database contents or backups."));
                break;
            case "drop-table":
                if (enforceOverlap && oldTableExists)
                    findings.Add(Create("EFG101", "Rolling-deployment incompatibility", FindingSeverity.Block, FindingConfidence.High,
                        ["compatibility", "data-loss"], provider, op.Migration, Location(op),
                        $"Table {op.Table} exists in the baseline EF model but is removed by the target migration.",
                        "Retire application versions that use the table before a later contract migration drops it.",
                        "Without baseline evidence, old application compatibility cannot be assessed."));
                findings.Add(Create("EFG201", "Destructive table change", FindingSeverity.High, FindingConfidence.High,
                    ["data-loss", "rollback"], provider, op.Migration, Location(op),
                    $"The migration permanently removes table {op.Table}.",
                    "Use an expand/transition/contract rollout and preserve a recoverable copy before the contract step.",
                    "The analysis does not inspect database contents or backups."));
                break;
            case "rename-column" when enforceOverlap && oldColumnExists:
                findings.Add(Create("EFG101", "Rolling-deployment incompatibility", FindingSeverity.Block, FindingConfidence.High,
                    ["compatibility"], provider, op.Migration, Location(op),
                    $"{op.Table}.{op.Column} exists in the baseline EF model but is renamed to {op.NewColumn}; older application instances still expect the old name.",
                    "Add the new column, dual-read/write during transition, then remove the old column in a later contract migration.",
                    "A rename is modeled as an incompatible name change for overlapping application generations."));
                break;
            case "rename-table" when enforceOverlap && oldTableExists:
                findings.Add(Create("EFG101", "Rolling-deployment incompatibility", FindingSeverity.Block, FindingConfidence.High,
                    ["compatibility"], provider, op.Migration, Location(op),
                    $"Table {op.Table} exists in the baseline EF model but is renamed to {op.NewTable}; older application instances still expect the old name.",
                    "Use a compatibility view or parallel table during transition, then remove the old name in the contract step.",
                    "A rename is modeled as an incompatible name change for overlapping application generations."));
                break;
            case "add-column" when !op.IsNullable && !op.SuppressTransaction && op.SqlShape is null:
                findings.Add(Create("EFG102", "Required column may reject existing rows", FindingSeverity.Block, FindingConfidence.High,
                    ["compatibility", "data-loss"], provider, op.Migration, Location(op),
                    $"{op.Table}.{op.Column} is added as required without a detectable default or backfill.",
                    "Add it nullable or with a safe server default, backfill in bounded batches, deploy code that requires it, and enforce non-nullability later.",
                    "The tool cannot prove existing row counts or provider-specific default evaluation."));
                break;
            case "alter-column" when IsUnsafeAlter(op):
                findings.Add(Create("EFG202", "Unsafe column alteration", FindingSeverity.Block, FindingConfidence.High,
                    ["compatibility", "data-loss"], provider, op.Migration, Location(op),
                    $"Column {op.Table}.{op.Column} changes from {FormatType(op.OldClrType, op.OldMaxLength, op.OldPrecision, op.OldScale, op.OldCollation)} to {FormatType(op.ClrType, op.MaxLength, op.Precision, op.Scale, op.Collation)} in a way that can reject existing values or old application writes.",
                    "Add a compatible representation, migrate values explicitly, switch application reads/writes, and contract the old representation later.",
                    "The tool does not inspect live data, collation contents, or application query behavior."));
                break;
            case "create-index":
                if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) && !op.IsConcurrent)
                    findings.Add(CreateProviderFinding("EFG302", "Write-blocking index creation", provider, op, providerSql,
                        $"The PostgreSQL index operation for {op.Table} is not configured for concurrent creation.",
                        "Use the provider-supported concurrent index option when appropriate and use the transaction semantics required by that option.",
                        ["blocking", "provider"]));
                if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) && !op.IsOnline)
                    findings.Add(CreateProviderFinding("EFG301", "Potentially blocking index creation", provider, op, providerSql,
                        $"The SQL Server index operation for {op.Table} is not marked for online creation.",
                        "Evaluate SQL Server/Azure SQL online index support and configure online creation where supported; otherwise schedule the operation for an appropriate maintenance window.",
                        ["blocking", "provider"]));
                if (op.IsUnique)
                    findings.Add(Create("EFG204", "Unique index validation risk", FindingSeverity.High, FindingConfidence.High,
                    ["compatibility", "blocking"], provider, op.Migration, Location(op),
                    $"The migration creates a unique index on {op.Table}; existing duplicate values can make the migration fail.",
                    "Check and remediate duplicate values before enforcing uniqueness, then create the constraint during a controlled transition.",
                    "The tool does not connect to the database or inspect existing values."));
                break;
            case "unique-constraint":
                findings.Add(Create("EFG204", "Unique constraint validation risk", FindingSeverity.High, FindingConfidence.High,
                    ["compatibility", "blocking"], provider, op.Migration, Location(op),
                    $"The migration creates a unique constraint on {op.Table}; existing duplicate values can make the migration fail.",
                    "Check and remediate duplicate values before enforcing uniqueness, then create the constraint during a controlled transition.",
                    "The tool does not connect to the database or inspect existing values."));
                break;
            case "add-foreign-key":
                findings.Add(Create("EFG303", "Foreign-key validation and locking risk", FindingSeverity.High, FindingConfidence.High,
                    ["compatibility", "blocking"], provider, op.Migration, Location(op),
                    $"The migration adds a foreign key from {op.Table} to {op.PrincipalTable}; existing orphaned rows or validation locks can fail or delay deployment.",
                    "Validate and repair data before adding the constraint, and use provider-supported online/not-valid validation patterns where available.",
                    "The tool cannot verify existing rows or production lock duration."));
                break;
            case "sql-backfill":
                findings.Add(Create("EFG304", "Unbounded data backfill", FindingSeverity.High, FindingConfidence.High,
                    ["blocking", "data-loss"], provider, op.Migration, Location(op),
                    "A migration contains an UPDATE-shaped operation without a detectable WHERE clause.",
                    "Move the backfill to bounded, resumable application or operational work with progress and throttling.",
                    "SQL is classified locally; the tool does not execute or transmit it."));
                break;
            case "sql-suppressed-transaction":
                findings.Add(Create("EFG305", "Transaction semantics require review", FindingSeverity.High, FindingConfidence.High,
                    ["blocking", "provider"], provider, op.Migration, Location(op),
                    "A migration operation suppresses its transaction.",
                    "Confirm why the operation requires transaction suppression and isolate it from changes that must be atomic.",
                    "The tool cannot prove whether the provider and deployment process make the split safe."));
                break;
            case "raw-sql":
            case "custom-operation":
                findings.Add(Create("EFG399", "Unverified migration operation", FindingSeverity.Unverified, FindingConfidence.Unknown,
                    ["compatibility", "provider"], provider, op.Migration, Location(op),
                    "The migration contains SQL or a custom operation that EfGuard cannot analyze with high confidence.",
                    "Review the generated SQL and rollout order manually; keep the operation outside the contract step unless its compatibility is proven.",
                    "Unknown operations are never treated as safe."));
                break;
        }

        return findings;
    }

    private static Diagnostic CreateProviderFinding(string ruleId, string title, string provider, NormalizedOperation operation, ProviderSqlEvidence evidence, string explanation, string remediation, List<string> risks)
    {
        bool hasEvidence = evidence.Available && evidence.Statements.Any(statement => operation.Migration is null || statement.Migration is null || statement.Migration.Equals(operation.Migration, StringComparison.OrdinalIgnoreCase));
        return Create(ruleId, title, hasEvidence ? FindingSeverity.High : FindingSeverity.Unverified, hasEvidence ? FindingConfidence.High : FindingConfidence.Unknown,
            risks, provider, operation.Migration, Location(operation), explanation, remediation,
            hasEvidence
                ? "Provider-generated migration SQL is retained as evidence, but runtime lock duration still depends on engine version, capabilities, workload, and deployment conditions."
                : "Provider-generated SQL for the migration operations under review was not available, so this provider-specific claim is explicitly unverified.");
    }

    private static Diagnostic Create(string ruleId, string title, FindingSeverity severity, FindingConfidence confidence,
        List<string> risks, string? provider, string? migration, string? location, string explanation, string remediation, string uncertainty, string? affectedState = null)
        => new()
        {
            RuleId = ruleId,
            Title = title,
            Severity = severity,
            Confidence = confidence,
            RiskDimensions = risks,
            Provider = provider,
            Migration = migration,
            Location = location,
            AffectedState = affectedState ?? (ruleId == "EFG101" ? "The baseline model and target migration are incompatible during overlap." : null),
            Explanation = explanation,
            Remediation = remediation,
            Uncertainty = uncertainty
        };

    private static bool ModelsCompatible(ModelSnapshot application, ModelSnapshot schema)
    {
        foreach (ModelTable applicationTable in application.Tables)
        {
            ModelTable? schemaTable = schema.Tables.FirstOrDefault(table => SameTable(table.Name, applicationTable.Name) && Same(table.Schema, applicationTable.Schema));
            if (schemaTable is null)
                return false;
            if (applicationTable.Columns.Any(column => !schemaTable.Columns.Any(candidate => Same(candidate.Name, column.Name))))
                return false;
        }
        return true;
    }

    private static bool ContainsTable(ModelSnapshot snapshot, string? schema, string? table)
        => table is not null && snapshot.Tables.Any(t => SameTable(t.Name, table!) && Same(t.Schema, schema));

    private static bool ContainsColumn(ModelSnapshot snapshot, string? schema, string? table, string? column)
        => column is not null && snapshot.Tables.Any(t => SameTable(t.Name, table!) && Same(t.Schema, schema) && t.Columns.Any(c => Same(c.Name, column)));

    private static bool SameTable(string left, string right)
    {
        string leftName = left.Contains('.', StringComparison.Ordinal) ? left[(left.LastIndexOf('.') + 1)..] : left;
        string rightName = right.Contains('.', StringComparison.Ordinal) ? right[(right.LastIndexOf('.') + 1)..] : right;
        return Same(leftName, rightName);
    }

    private static bool Same(string? left, string? right)
        => string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);

    private static string? Location(NormalizedOperation op)
        => op.Table is null ? null : op.Column is null ? op.Table : $"{op.Table}.{op.Column}";

    private static bool IsUnsafeAlter(NormalizedOperation op)
    {
        bool typeChanged = op.OldClrType is not null && op.ClrType is not null && !op.OldClrType.Equals(op.ClrType, StringComparison.OrdinalIgnoreCase);
        bool nullabilityNarrowed = op.OldIsNullable == true && !op.IsNullable;
        bool lengthNarrowed = op.OldMaxLength is not null && op.MaxLength is not null && op.MaxLength < op.OldMaxLength;
        bool precisionNarrowed = op.OldPrecision is not null && op.Precision is not null && op.Precision < op.OldPrecision;
        bool scaleNarrowed = op.OldScale is not null && op.Scale is not null && op.Scale < op.OldScale;
        bool collationChanged = op.OldCollation is not null && op.Collation is not null && !op.OldCollation.Equals(op.Collation, StringComparison.OrdinalIgnoreCase);
        return typeChanged || nullabilityNarrowed || lengthNarrowed || precisionNarrowed || scaleNarrowed || collationChanged;
    }

    private static string FormatType(string? type, int? length, byte? precision, byte? scale, string? collation)
        => type is null ? "the previous type" : length is not null ? $"{type}({length})" : precision is not null ? $"{type}({precision},{scale})" : collation is null ? type : $"{type} with collation {collation}";
}

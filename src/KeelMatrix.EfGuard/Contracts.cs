using System.Text.Json.Serialization;

namespace KeelMatrix.EfGuard;

internal sealed class ExtractionRequest
{
    public string ProjectPath { get; set; } = "";
    public string StartupProjectPath { get; set; } = "";
    public string? ContextName { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string? ResponsePath { get; set; }
    public int TimeoutSeconds { get; set; } = 90;
}

internal sealed class ExtractionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Provider { get; set; }
    public bool ProviderSupported { get; set; }
    public bool ProviderSqlGenerated { get; set; }
    public string? Context { get; set; }
    public ModelSnapshot Model { get; set; } = new();
    public List<NormalizedOperation> Operations { get; set; } = [];
}

internal sealed class ModelSnapshot
{
    public List<ModelTable> Tables { get; set; } = [];
}

internal sealed class ModelTable
{
    public string Name { get; set; } = "";
    public string? Schema { get; set; }
    public List<ModelColumn> Columns { get; set; } = [];
}

internal sealed class ModelColumn
{
    public string Name { get; set; } = "";
    public string? ClrType { get; set; }
    public bool IsNullable { get; set; } = true;
    public int? MaxLength { get; set; }
    public byte? Precision { get; set; }
    public byte? Scale { get; set; }
}

internal sealed class NormalizedOperation
{
    public string Kind { get; set; } = "";
    public string? Migration { get; set; }
    public string? Schema { get; set; }
    public string? Table { get; set; }
    public string? NewTable { get; set; }
    public string? Column { get; set; }
    public string? NewColumn { get; set; }
    public string? PrincipalTable { get; set; }
    public string? ClrType { get; set; }
    public string? OldClrType { get; set; }
    public bool IsNullable { get; set; }
    public bool? OldIsNullable { get; set; }
    public int? MaxLength { get; set; }
    public int? OldMaxLength { get; set; }
    public byte? Precision { get; set; }
    public byte? OldPrecision { get; set; }
    public byte? Scale { get; set; }
    public byte? OldScale { get; set; }
    public bool IsUnique { get; set; }
    public bool IsConcurrent { get; set; }
    public bool IsOnline { get; set; }
    public bool SuppressTransaction { get; set; }
    public string? SqlShape { get; set; }
}

internal enum FindingSeverity
{
    Advisory,
    High,
    Block,
    Unverified
}

internal enum FindingConfidence
{
    High,
    Medium,
    Unknown
}

internal sealed class Diagnostic
{
    public string RuleId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> RiskDimensions { get; set; } = [];
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FindingSeverity Severity { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FindingConfidence Confidence { get; set; }
    public string? Provider { get; set; }
    public string? Migration { get; set; }
    public string? Location { get; set; }
    public string? AffectedState { get; set; }
    public string Explanation { get; set; } = "";
    public string Remediation { get; set; } = "";
    public string Uncertainty { get; set; } = "";
    public bool Suppressed { get; set; }
}

internal sealed class Report
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "0.1.0";
    public string? Provider { get; set; }
    public BaselineReport Baseline { get; set; } = new();
    public ReportSummary Summary { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

internal sealed class BaselineReport
{
    public bool Requested { get; set; }
    public string? Reference { get; set; }
    public bool Available { get; set; }
}

internal sealed class ReportSummary
{
    public int OperationsInspected { get; set; }
    public int Compatible { get; set; }
    public int Advisory { get; set; }
    public int High { get; set; }
    public int Blocking { get; set; }
    public int Unverified { get; set; }
    public int ExitCode { get; set; }
}

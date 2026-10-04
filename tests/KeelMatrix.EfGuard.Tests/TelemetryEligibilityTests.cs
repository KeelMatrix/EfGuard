namespace KeelMatrix.EfGuard.Tests;

public sealed class TelemetryEligibilityTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void Telemetry_is_requested_only_for_completed_scans(int exitCode, bool expected)
    {
        int calls = 0;

        Program.TrackTelemetryAfterCompletedScan(exitCode, () => calls++);

        Assert.Equal(expected ? 1 : 0, calls);
    }
}

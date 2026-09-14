using System.Diagnostics;

namespace KeelMatrix.EfGuard.Tests;

public sealed class ProjectBuilderTests
{
    [Fact]
    public void BuildStartInfoCarriesExplicitMsBuildNodeIsolation()
    {
        ProcessStartInfo startInfo = KeelMatrix.EfGuard.Worker.ProjectBuilder.CreateStartInfo(
            Path.Combine(Path.GetTempPath(), "EfGuardFixture.csproj"),
            Path.Combine(Path.GetTempPath(), "efguard-build-output"));

        Assert.Equal("1", startInfo.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal(
            [
                "build",
                Path.Combine(Path.GetTempPath(), "EfGuardFixture.csproj"),
                "--configuration",
                "Release",
                "--nologo",
                "--no-restore",
                "-m:1",
                "-nodeReuse:false",
                "/p:OutputPath=" + Path.Combine(Path.GetTempPath(), "efguard-build-output") + Path.DirectorySeparatorChar,
                "/p:CopyLocalLockFileAssemblies=true"
            ],
            startInfo.ArgumentList);
    }
}

using MiguelDownloader.Engine.Dependencies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// Exercises the real bootstrap path: fetch the published release, verify its checksum, install.
/// <para>
/// Excluded from the default run because it downloads tens of megabytes. Run with
/// <c>dotnet test --filter Category=ToolInstall</c>.
/// </para>
/// </summary>
[Trait("Category", "ToolInstall")]
public sealed class ToolInstallerTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // The GitHub API rejects requests without a user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiguelDownloader/1.0");
        return client;
    }

    [Fact]
    public async Task ReportsTheLatestPublishedYtDlpVersion()
    {
        using var client = CreateClient();
        var installer = new ToolInstaller(client, NullLogger<ToolInstaller>.Instance);

        var version = await installer.GetLatestYtDlpVersionAsync();
        _output.WriteLine($"latest yt-dlp: {version}");

        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public async Task InstallsFfmpegAndVerifiesItsChecksum()
    {
        using var client = CreateClient();
        var installer = new ToolInstaller(client, NullLogger<ToolInstaller>.Instance);

        var lastPhase = ToolInstallPhase.CheckingVersion;
        var progress = new Progress<ToolInstallProgress>(p =>
        {
            if (p.Phase == lastPhase) return;
            lastPhase = p.Phase;
            _output.WriteLine($"phase: {p.Phase}");
        });

        var (ffmpeg, ffprobe) = await installer.InstallFfmpegAsync(progress);

        _output.WriteLine($"ffmpeg : {ffmpeg}");
        _output.WriteLine($"ffprobe: {ffprobe}");

        Assert.True(File.Exists(ffmpeg), "ffmpeg.exe was not installed");
        Assert.True(File.Exists(ffprobe), "ffprobe.exe was not installed");
        Assert.True(new FileInfo(ffmpeg).Length > 100_000);
    }

    [Fact]
    public async Task InstallsYtDlpAndVerifiesItsChecksum()
    {
        using var client = CreateClient();
        var installer = new ToolInstaller(client, NullLogger<ToolInstaller>.Instance);

        var path = await installer.InstallYtDlpAsync();

        _output.WriteLine($"yt-dlp: {path}");
        Assert.True(File.Exists(path), "yt-dlp.exe was not installed");
        Assert.True(new FileInfo(path).Length > 1_000_000);
    }

    [Theory]
    [InlineData("2026.08.19", "2026.07.04", true)]
    [InlineData("2026.07.04", "2026.08.19", false)]
    [InlineData("2026.08.19", "2026.08.19", false)]
    [InlineData("2026.08.19", null, true)]
    [InlineData(null, "2026.08.19", false)]
    public void ComparesDatedVersions(string? candidate, string? current, bool expected)
    {
        Assert.Equal(expected, ToolInstaller.IsNewer(candidate, current));
    }
}

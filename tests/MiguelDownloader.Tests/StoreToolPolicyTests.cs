using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MiguelDownloader.Tests;

public sealed class StoreToolPolicyTests
{
    [Fact]
    public async Task PackagePolicyIgnoresConfiguredAndPathTools()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"miguel-store-tool-{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(marker, []);

        try
        {
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var locator = new ToolLocator(
                runner,
                NullLogger<ToolLocator>.Instance,
                allowManagedTools: false,
                allowExternalTools: false,
                useLgplTranscodeEncoders: true);

            var tools = await locator.ResolveAsync(new AdvancedSettings
            {
                YtDlpPath = marker,
                FfmpegPath = marker,
                FfprobePath = marker,
                JsRuntimePath = marker,
            });

            foreach (var tool in new[] { tools.YtDlp, tools.Ffmpeg, tools.Ffprobe, tools.JsRuntime })
            {
                Assert.DoesNotContain(tool.Source, new[]
                {
                    ToolSource.Configured,
                    ToolSource.Managed,
                    ToolSource.SystemPath,
                });
                Assert.NotEqual(marker, tool.Path);
            }
            Assert.False(tools.AllowUserToolOverrides);
            Assert.True(tools.UseLgplTranscodeEncoders);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void PackagePolicyUsesBundledPathsAndDropsArbitraryArguments()
    {
        var tools = new ToolPaths
        {
            YtDlp = Resolved(ExternalTool.YtDlp, @"C:\Package\tools\yt-dlp.exe"),
            Ffmpeg = Resolved(ExternalTool.Ffmpeg, @"C:\Package\tools\ffmpeg.exe"),
            Ffprobe = Resolved(ExternalTool.Ffprobe, @"C:\Package\tools\ffprobe.exe"),
            JsRuntime = Resolved(ExternalTool.JsRuntime, @"C:\Package\tools\deno.exe"),
            AllowUserToolOverrides = false,
            UseLgplTranscodeEncoders = true,
        };
        var configured = new AdvancedSettings
        {
            YtDlpPath = @"D:\User\yt-dlp.exe",
            FfmpegPath = @"D:\User\ffmpeg.exe",
            FfprobePath = @"D:\User\ffprobe.exe",
            JsRuntimePath = @"D:\User\node.exe",
            ExtraYtDlpArguments = "--exec calc.exe",
            CookiesFromBrowser = "firefox",
            VerboseLogging = true,
        };

        var effective = tools.ApplyExecutionPolicy(configured);

        Assert.Empty(effective.YtDlpPath);
        Assert.Equal(tools.Ffmpeg.Path, effective.FfmpegPath);
        Assert.Equal(tools.Ffprobe.Path, effective.FfprobePath);
        Assert.Equal(tools.JsRuntime.Path, effective.JsRuntimePath);
        Assert.Empty(effective.ExtraYtDlpArguments);
        Assert.Equal("firefox", effective.CookiesFromBrowser);
        Assert.True(effective.VerboseLogging);
    }

    private static ResolvedTool Resolved(ExternalTool tool, string path) => new()
    {
        Tool = tool,
        Source = ToolSource.Bundled,
        Path = path,
    };
}

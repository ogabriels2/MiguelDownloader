using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;
using Xunit;

namespace MiguelDownloader.Tests;

public class ProgressParserTests
{
    /// <summary>Builds a line exactly as the configured progress template produces it.</summary>
    private static string DownloadLine(
        string status = "downloading", string downloaded = "1048576", string total = "10485760",
        string estimate = "NA", string speed = "524288.0", string eta = "18",
        string fragIndex = "NA", string fragCount = "NA", string formatId = "137")
        => ProgressParser.DownloadSentinel +
           string.Join('|', status, downloaded, total, estimate, speed, eta, fragIndex, fragCount, formatId);

    [Fact]
    public void ParsesATypicalDownloadLine()
    {
        Assert.True(ProgressParser.TryParse(DownloadLine(), out var update));

        Assert.Equal("downloading", update.Status);
        Assert.Equal(1_048_576, update.DownloadedBytes);
        Assert.Equal(10_485_760, update.TotalBytes);
        Assert.Equal(524_288.0, update.SpeedBytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(18), update.Eta);
        Assert.Equal("137", update.FormatId);
        Assert.False(update.TotalIsEstimate);
    }

    [Fact]
    public void TreatsNaAsUnknownRatherThanZero()
    {
        // This is the field yt-dlp writes when it has no value; reading it as 0 would show a
        // completed download or a zero-second ETA.
        var line = DownloadLine(total: "NA", speed: "NA", eta: "NA");
        Assert.True(ProgressParser.TryParse(line, out var update));

        Assert.Null(update.TotalBytes);
        Assert.Null(update.SpeedBytesPerSecond);
        Assert.Null(update.Eta);
    }

    [Fact]
    public void FallsBackToTheEstimatedTotalAndFlagsIt()
    {
        var line = DownloadLine(total: "NA", estimate: "9999999");
        Assert.True(ProgressParser.TryParse(line, out var update));

        Assert.Equal(9_999_999, update.TotalBytes);
        Assert.True(update.TotalIsEstimate);
    }

    [Fact]
    public void ParsesFragmentCounters()
    {
        var line = DownloadLine(total: "NA", fragIndex: "12", fragCount: "40");
        Assert.True(ProgressParser.TryParse(line, out var update));

        Assert.Equal(12, update.FragmentIndex);
        Assert.Equal(40, update.FragmentCount);
    }

    [Fact]
    public void RecognisesTheFinishedStatus()
    {
        Assert.True(ProgressParser.TryParse(DownloadLine(status: "finished"), out var update));
        Assert.True(update.IsFinished);
    }

    [Fact]
    public void ParsesPostProcessingLines()
    {
        var line = ProgressParser.PostProcessSentinel + "started|Merger";
        Assert.True(ProgressParser.TryParse(line, out var update));

        Assert.True(update.IsPostProcessing);
        Assert.Equal("Merger", update.Postprocessor);
    }

    [Theory]
    [InlineData("Merger", DownloadStage.Muxing)]
    [InlineData("VideoRemuxer", DownloadStage.Remuxing)]
    [InlineData("VideoConvertor", DownloadStage.Converting)]
    [InlineData("ExtractAudio", DownloadStage.Converting)]
    [InlineData("Metadata", DownloadStage.WritingMetadata)]
    [InlineData("EmbedThumbnail", DownloadStage.EmbeddingCoverArt)]
    [InlineData("MoveFiles", DownloadStage.Finalizing)]
    public void MapsPostProcessorsToUserFacingStages(string postprocessor, DownloadStage expected)
    {
        Assert.Equal(expected, ProgressParser.StageForPostProcessor(postprocessor));
    }

    [Theory]
    [InlineData("[download]  53.2% of 10.00MiB at 1.00MiB/s ETA 00:05")]
    [InlineData("[youtube] abc: Downloading webpage")]
    [InlineData("")]
    [InlineData("WARNING: something happened")]
    public void IgnoresOrdinaryToolOutput(string line)
    {
        Assert.False(ProgressParser.TryParse(line, out _));
    }

    [Fact]
    public void IgnoresAMalformedProgressLine()
    {
        Assert.False(ProgressParser.TryParse(ProgressParser.DownloadSentinel + "downloading|1", out _));
    }

    [Fact]
    public void RejectsAbsurdEtaValues()
    {
        // A stalled transfer can report an ETA of years, which is noise rather than information.
        var line = DownloadLine(eta: "99999999");
        Assert.True(ProgressParser.TryParse(line, out var update));
        Assert.Null(update.Eta);
    }
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("ERROR: [youtube] abc: Video unavailable", DownloadErrorKind.VideoUnavailable)]
    [InlineData("ERROR: [youtube] abc: This video has been removed by the uploader", DownloadErrorKind.VideoRemoved)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you have been granted access",
        DownloadErrorKind.PrivateVideo)]
    [InlineData("ERROR: Join this channel to get access to members-only content", DownloadErrorKind.MembersOnly)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age", DownloadErrorKind.AgeRestricted)]
    [InlineData("ERROR: The uploader has not made this video available in your country",
        DownloadErrorKind.GeoRestricted)]
    [InlineData("ERROR: Sign in to confirm you're not a bot", DownloadErrorKind.BotCheck)]
    [InlineData("ERROR: Requested format is not available", DownloadErrorKind.FormatUnavailable)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden",
        DownloadErrorKind.ExpiredStreamUrl)]
    [InlineData("ERROR: HTTP Error 429: Too Many Requests", DownloadErrorKind.RateLimited)]
    [InlineData("ERROR: Unable to download webpage: <urlopen error getaddrinfo failed>",
        DownloadErrorKind.NetworkFailure)]
    [InlineData("ERROR: The operation has timed out", DownloadErrorKind.Timeout)]
    [InlineData("OSError: [Errno 28] No space left on device", DownloadErrorKind.DiskFull)]
    [InlineData("PermissionError: [Errno 13] Permission denied", DownloadErrorKind.PermissionDenied)]
    [InlineData("The process cannot access the file because it is being used by another process",
        DownloadErrorKind.FileInUse)]
    [InlineData("ERROR: You have requested merging of multiple formats but ffmpeg is not installed",
        DownloadErrorKind.FfmpegMissing)]
    [InlineData("WARNING: No supported JavaScript runtime could be found", DownloadErrorKind.JsRuntimeMissing)]
    [InlineData("ERROR: Unsupported URL: https://example.com/x", DownloadErrorKind.UnsupportedUrl)]
    public void RecognisesRealToolMessages(string message, DownloadErrorKind expected)
    {
        var error = ErrorClassifier.Classify(message);
        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public void DiskFullIsMatchedBeforeAnythingElse()
    {
        // The Windows wording mentions writing a file, which could otherwise look like an IO error.
        var error = ErrorClassifier.Classify(
            "ERROR: unable to write data: There is not enough space on the disk.");
        Assert.Equal(DownloadErrorKind.DiskFull, error.Kind);
        Assert.Equal(RecommendedAction.FreeDiskSpace, error.Action);
    }

    [Fact]
    public void MembersOnlyBeatsTheGenericSignInMatch()
    {
        var error = ErrorClassifier.Classify(
            "ERROR: This video is available to this channel's members on level: Sign in to join");
        Assert.Equal(DownloadErrorKind.MembersOnly, error.Kind);
    }

    [Fact]
    public void RealErrorsAreDiagnosedAheadOfAdvisoryWarnings()
    {
        // Exactly what a failing run emits: a JavaScript-runtime warning first, the real reason
        // last. Matching the warning would tell someone to install a runtime when the video simply
        // does not exist, which is a confidently wrong diagnosis.
        const string output = """
            WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default
            [youtube] AAAAAAAAAAA: Downloading webpage
            ERROR: [youtube] AAAAAAAAAAA: Video unavailable
            """;

        var error = ErrorClassifier.Classify(output);

        Assert.Equal(DownloadErrorKind.VideoUnavailable, error.Kind);
        Assert.NotEqual(DownloadErrorKind.JsRuntimeMissing, error.Kind);
    }

    [Fact]
    public void AWarningIsStillUsedWhenNothingElseFailed()
    {
        // With no error line at all, the warning is the only signal there is, so it should be used.
        const string output = "WARNING: [youtube] No supported JavaScript runtime could be found.";

        Assert.Equal(DownloadErrorKind.JsRuntimeMissing, ErrorClassifier.Classify(output).Kind);
    }

    [Fact]
    public void EnvironmentFailuresWithoutAnErrorPrefixStillClassify()
    {
        // ffmpeg and the OS do not prefix their output with "ERROR:", so the fallback pass has to
        // catch these.
        Assert.Equal(DownloadErrorKind.DiskFull,
            ErrorClassifier.Classify("There is not enough space on the disk.").Kind);
    }

    [Fact]
    public void UnknownOutputKeepsTheRawTextForDiagnosis()
    {
        const string weird = "ERROR: something nobody has seen before";
        var error = ErrorClassifier.Classify(weird);

        Assert.Equal(DownloadErrorKind.Unknown, error.Kind);
        Assert.Equal(weird, error.TechnicalDetails);
    }

    [Fact]
    public void CancellationIsNotAFailure()
    {
        var error = ErrorClassifier.Classify(new OperationCanceledException());
        Assert.Equal(DownloadErrorKind.Cancelled, error.Kind);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public void MapsUnauthorizedAccessToAPermissionProblem()
    {
        var error = ErrorClassifier.Classify(new UnauthorizedAccessException("nope"));
        Assert.Equal(DownloadErrorKind.PermissionDenied, error.Kind);
        Assert.Equal(RecommendedAction.ChooseAnotherFolder, error.Action);
    }

    [Fact]
    public void TransientFailuresAreRetryableAndPermanentOnesAreNot()
    {
        Assert.True(ErrorClassifier.Classify("HTTP Error 429: Too Many Requests").IsRetryable);
        Assert.True(ErrorClassifier.Classify("The operation has timed out").IsRetryable);

        var removed = ErrorClassifier.Classify("This video has been removed by the uploader");
        Assert.True(removed.IsPermanent);
        Assert.False(removed.IsRetryable);
    }
}

public class JsRuntimeArgumentTests
{
    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe", @"node:C:\Program Files\nodejs\node.exe")]
    [InlineData(@"C:\tools\deno.exe", @"deno:C:\tools\deno.exe")]
    [InlineData(@"C:\tools\bun.exe", @"bun:C:\tools\bun.exe")]
    [InlineData(@"C:\tools\qjs.exe", @"quickjs:C:\tools\qjs.exe")]
    public void NamesTheRuntimeAlongsideItsPath(string path, string expected)
    {
        // The flag names a runtime; a bare path is not enough for the tool to know what it is.
        Assert.Equal(expected, YtDlpArguments.FormatJsRuntime(path));
    }

    [Fact]
    public void UnrecognisedExecutablesAreLeftAlone()
    {
        // Guessing a runtime name would break every extraction, not just this one setting.
        Assert.Null(YtDlpArguments.FormatJsRuntime(@"C:\tools\something-else.exe"));
    }

    [Fact]
    public void ADetectedRuntimeIsPassedToTheTool()
    {
        var args = YtDlpArguments.ForAnalysis(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new MiguelDownloader.Core.Settings.AdvancedSettings(),
            @"C:\Program Files\nodejs\node.exe");

        Assert.Contains("--js-runtimes", args);
        Assert.Contains(@"node:C:\Program Files\nodejs\node.exe", args);
    }

    [Fact]
    public void AnExplicitSettingOverridesDetection()
    {
        var args = YtDlpArguments.ForAnalysis(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new MiguelDownloader.Core.Settings.AdvancedSettings { JsRuntimePath = @"D:\deno.exe" },
            @"C:\Program Files\nodejs\node.exe");

        Assert.Contains(@"deno:D:\deno.exe", args);
        Assert.DoesNotContain(@"node:C:\Program Files\nodejs\node.exe", args);
    }

    [Fact]
    public void TheUrlIsSeparatedFromTheFlags()
    {
        // Everything after "--" is a URL even if it starts with a dash, so a crafted URL cannot
        // be read as an option.
        var args = YtDlpArguments.ForAnalysis(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new MiguelDownloader.Core.Settings.AdvancedSettings());

        var separator = args.ToList().IndexOf("--");
        Assert.True(separator >= 0, "the end-of-options separator is missing");
        Assert.Equal(args.Count - 1, separator + 1);
    }
}

public class CommandLineSplitterTests
{
    [Fact]
    public void SplitsOnWhitespace()
    {
        var result = CommandLineSplitter.Split("--flag value --other");
        Assert.Equal(["--flag", "value", "--other"], result);
    }

    [Fact]
    public void KeepsQuotedRunsTogether()
    {
        var result = CommandLineSplitter.Split("--path \"C:\\Program Files\\thing\" --flag");
        Assert.Equal(["--path", @"C:\Program Files\thing", "--flag"], result);
    }

    [Fact]
    public void HandlesEscapedQuotes()
    {
        var result = CommandLineSplitter.Split("--name \"say \\\"hi\\\"\"");
        Assert.Equal(2, result.Count);
        Assert.Equal("say \"hi\"", result[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyInputYieldsNoArguments(string? input)
    {
        Assert.Empty(CommandLineSplitter.Split(input));
    }

    [Fact]
    public void ShellMetacharactersStayInsideASingleArgument()
    {
        // These are passed through as argv values, so they reach the tool as literal text and are
        // never interpreted by a command interpreter.
        var result = CommandLineSplitter.Split("--title \"a & b | c > d\"");

        Assert.Equal(2, result.Count);
        Assert.Equal("a & b | c > d", result[1]);
    }
}

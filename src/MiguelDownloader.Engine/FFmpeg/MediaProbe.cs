using System.Globalization;
using System.Text.Json;
using MiguelDownloader.Engine.Processes;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.FFmpeg;

/// <summary>What ffprobe found inside a file.</summary>
public sealed record ProbeResult
{
    public bool IsReadable { get; init; }
    public TimeSpan? Duration { get; init; }
    public long? SizeBytes { get; init; }

    public bool HasVideoStream { get; init; }
    public bool HasAudioStream { get; init; }
    public int SubtitleStreamCount { get; init; }
    public int AudioStreamCount { get; init; }

    public string? FormatName { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>Why the probe judged the file unusable, when it did.</summary>
    public string? Problem { get; init; }
}

/// <summary>What the caller expected the file to contain, used to judge the probe result.</summary>
public sealed record ProbeExpectation
{
    public bool ExpectVideo { get; init; }
    public bool ExpectAudio { get; init; } = true;

    /// <summary>Source duration, used to catch a truncated download.</summary>
    public TimeSpan? ExpectedDuration { get; init; }

    /// <summary>How far the real duration may drift before it counts as truncated.</summary>
    public double DurationTolerancePercent { get; init; } = 5.0;

    public int MinimumAudioStreams { get; init; } = 1;
}

/// <summary>
/// Inspects finished files with ffprobe.
/// <para>
/// A download is only reported as complete once this confirms the file exists, is non-empty, can
/// actually be opened as media, and contains the streams that were asked for. Checking the file
/// size alone would happily accept a truncated transfer or a container whose header never got
/// written, which is exactly the failure a user discovers days later.
/// </para>
/// </summary>
public sealed class MediaProbe(ProcessRunner runner, ILogger<MediaProbe> logger)
{
    private readonly ProcessRunner _runner = runner;
    private readonly ILogger<MediaProbe> _logger = logger;

    /// <summary>Reads what is inside a media file.</summary>
    public async Task<ProbeResult> ProbeAsync(
        string ffprobePath, string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return new ProbeResult { IsReadable = false, Problem = "missing" };

        var info = new FileInfo(filePath);
        if (info.Length == 0)
            return new ProbeResult { IsReadable = false, SizeBytes = 0, Problem = "empty" };

        string[] arguments =
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath,
        ];

        ProcessResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            result = await _runner.RunAsync(
                ffprobePath, arguments,
                captureStandardOutput: true,
                cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffprobe timed out on {File}", Path.GetFileName(filePath));
            return new ProbeResult { IsReadable = false, SizeBytes = info.Length, Problem = "probe-timeout" };
        }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogWarning("ffprobe could not read {File}: {Error}",
                Path.GetFileName(filePath), result.StandardError.Trim());
            return new ProbeResult { IsReadable = false, SizeBytes = info.Length, Problem = "unreadable" };
        }

        return ParseProbeOutput(result.StandardOutput, info.Length);
    }

    private ProbeResult ParseProbeOutput(string json, long sizeBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var streams = root.TryGetProperty("streams", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().ToList()
                : [];

            var videoStreams = streams
                .Where(x => GetString(x, "codec_type") == "video")
                // Cover art is stored as a still image stream; it is not video content.
                .Where(x => GetString(x, "disposition_attached_pic") != "1")
                .ToList();

            var audioStreams = streams.Where(x => GetString(x, "codec_type") == "audio").ToList();
            var subtitleStreams = streams.Where(x => GetString(x, "codec_type") == "subtitle").ToList();

            TimeSpan? duration = null;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationElement) &&
                double.TryParse(durationElement.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            var firstVideo = videoStreams.FirstOrDefault();

            return new ProbeResult
            {
                IsReadable = true,
                SizeBytes = sizeBytes,
                Duration = duration,
                HasVideoStream = videoStreams.Count > 0,
                HasAudioStream = audioStreams.Count > 0,
                AudioStreamCount = audioStreams.Count,
                SubtitleStreamCount = subtitleStreams.Count,
                FormatName = root.TryGetProperty("format", out var f) ? GetString(f, "format_name") : null,
                VideoCodec = firstVideo.ValueKind == JsonValueKind.Object ? GetString(firstVideo, "codec_name") : null,
                AudioCodec = audioStreams.Count > 0 ? GetString(audioStreams[0], "codec_name") : null,
                Width = firstVideo.ValueKind == JsonValueKind.Object ? GetInt(firstVideo, "width") : null,
                Height = firstVideo.ValueKind == JsonValueKind.Object ? GetInt(firstVideo, "height") : null,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ffprobe output");
            return new ProbeResult { IsReadable = false, SizeBytes = sizeBytes, Problem = "unparsable-probe" };
        }
    }

    /// <summary>
    /// Judges a probe result against what was expected.
    /// Returns null when the file is acceptable, or a reason key when it is not.
    /// </summary>
    public static string? Validate(ProbeResult probe, ProbeExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(expectation);

        if (!probe.IsReadable) return probe.Problem ?? "unreadable";
        if (probe.SizeBytes is null or 0) return "empty";

        if (expectation.ExpectVideo && !probe.HasVideoStream) return "missing-video-stream";
        if (expectation.ExpectAudio && !probe.HasAudioStream) return "missing-audio-stream";

        if (expectation.MinimumAudioStreams > 1 && probe.AudioStreamCount < expectation.MinimumAudioStreams)
            return "missing-audio-tracks";

        // A container that reports no duration at all is usually one whose header never got
        // finalised, which is the signature of an interrupted mux.
        if (probe.Duration is null && expectation.ExpectedDuration is not null) return "no-duration";

        if (probe.Duration is { } actual && expectation.ExpectedDuration is { } expected &&
            expected > TimeSpan.Zero)
        {
            var drift = Math.Abs((actual - expected).TotalSeconds);
            var allowed = Math.Max(
                expected.TotalSeconds * expectation.DurationTolerancePercent / 100.0,
                // Always allow a couple of seconds: containers round, and the last audio frame
                // often extends slightly past the video.
                2.0);

            if (drift > allowed) return "duration-mismatch";
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var i)
            ? i
            : null;
}

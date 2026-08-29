using System.Globalization;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Processes;

namespace MiguelDownloader.Engine.YtDlp;

/// <summary>
/// Builds argument lists for yt-dlp.
/// <para>
/// Every method returns a list of separate arguments, never a command line. Nothing is quoted,
/// escaped or concatenated here, because the process layer passes the list straight through to
/// the Win32 argument parser. That is what makes a title containing quotes or an ampersand
/// harmless.
/// </para>
/// </summary>
public static class YtDlpArguments
{
    /// <summary>Arguments shared by every invocation.</summary>
    /// <param name="advanced">Tool configuration.</param>
    /// <param name="detectedJsRuntime">
    /// Path to a JavaScript runtime the app found on this machine, used when the user has not
    /// configured one explicitly.
    /// </param>
    private static List<string> Common(AdvancedSettings advanced, string? detectedJsRuntime)
    {
        var args = new List<string>
        {
            // Machine-readable output: no ANSI colours, one progress line per update.
            "--no-colors",
            "--newline",
            // The app decides what to retry and when, so the tool should not sleep on its own.
            "--no-playlist-reverse",
        };

        AddJsRuntime(args, advanced, detectedJsRuntime);
        AddCookies(args, advanced);
        AddFfmpegLocation(args, advanced);

        return args;
    }

    /// <summary>
    /// Tells the extractor which JavaScript runtime to use for YouTube's challenges.
    /// <para>
    /// Only Deno is enabled by default, so a machine that has Node instead gets the "no supported
    /// JavaScript runtime" warning and may be served fewer formats. Since the app already locates
    /// a runtime, naming it here is what actually puts it to work.
    /// </para>
    /// </summary>
    private static void AddJsRuntime(List<string> args, AdvancedSettings advanced, string? detected)
    {
        var path = !string.IsNullOrWhiteSpace(advanced.JsRuntimePath)
            ? advanced.JsRuntimePath
            : detected;

        if (string.IsNullOrWhiteSpace(path)) return;

        var value = FormatJsRuntime(path);
        if (value is null) return;

        args.Add("--js-runtimes");
        args.Add(value);
    }

    /// <summary>
    /// Builds the <c>name:path</c> value the tool expects. The runtime is identified from the
    /// executable name, because the flag names a runtime rather than pointing at a file.
    /// </summary>
    internal static string? FormatJsRuntime(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

        var runtime = fileName switch
        {
            "deno" => "deno",
            "node" => "node",
            "bun" => "bun",
            "qjs" or "quickjs" or "quickjs-ng" => "quickjs",
            _ => null,
        };

        // An unrecognised executable is left alone rather than guessed at: passing the wrong
        // runtime name would fail every extraction instead of just this one setting.
        return runtime is null ? null : $"{runtime}:{path}";
    }

    private static void AddCookies(List<string> args, AdvancedSettings advanced)
    {
        // Cookies are only ever added when the user explicitly configured them. They are never
        // logged, and the values never reach the UI.
        if (!string.IsNullOrWhiteSpace(advanced.CookiesFilePath))
        {
            args.Add("--cookies");
            args.Add(advanced.CookiesFilePath);
        }
        else if (!string.IsNullOrWhiteSpace(advanced.CookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(advanced.CookiesFromBrowser);
        }
    }

    private static void AddFfmpegLocation(List<string> args, AdvancedSettings advanced)
    {
        if (string.IsNullOrWhiteSpace(advanced.FfmpegPath)) return;

        // yt-dlp accepts either the binary or the directory containing it; the directory form
        // also lets it find ffprobe next door.
        var directory = Directory.Exists(advanced.FfmpegPath)
            ? advanced.FfmpegPath
            : Path.GetDirectoryName(advanced.FfmpegPath);

        if (string.IsNullOrWhiteSpace(directory)) return;

        args.Add("--ffmpeg-location");
        args.Add(directory);
    }

    /// <summary>
    /// Arguments to fetch full metadata and the format list for a single item, without downloading.
    /// </summary>
    public static IReadOnlyList<string> ForAnalysis(
        string url, AdvancedSettings advanced, string? detectedJsRuntime = null)
    {
        var args = Common(advanced, detectedJsRuntime);
        args.Add("--dump-single-json");
        // A URL carrying a &list= context must not expand into the whole playlist here.
        args.Add("--no-playlist");
        args.Add("--no-download");
        args.Add(Separator);
        args.Add(url);
        return args;
    }

    /// <summary>
    /// Arguments to list a playlist, album or channel without fetching each entry in full.
    /// <para>
    /// A flat listing returns ids, titles and durations but no formats, which is all the
    /// selection UI needs and is dramatically faster: a full extraction of a thousand-video
    /// channel would mean a thousand player requests.
    /// </para>
    /// </summary>
    /// <param name="limit">Stop after this many entries. Null means no limit.</param>
    public static IReadOnlyList<string> ForCollectionListing(
        string url, AdvancedSettings advanced, int? limit = null, string? detectedJsRuntime = null)
    {
        var args = Common(advanced, detectedJsRuntime);
        args.Add("--dump-single-json");
        args.Add("--flat-playlist");
        args.Add("--no-download");

        if (limit is > 0)
        {
            args.Add("--playlist-items");
            args.Add($"1:{limit.Value}");
        }

        args.Add(Separator);
        args.Add(url);
        return args;
    }

    /// <summary>
    /// Arguments for a search, used to find a catalogue recording somewhere it can be fetched.
    /// <para>
    /// One JSON object per line rather than a single document: the results are scored as they
    /// arrive and a malformed entry costs one candidate instead of the whole search. The listing
    /// stays flat because only the title, length and uploader are needed to rank a candidate —
    /// opening every result to read its formats would mean a request per candidate for
    /// information that does not affect the ranking at all.
    /// </para>
    /// </summary>
    /// <param name="searchExpressions">
    /// One or more yt-dlp search terms, e.g. <c>ytsearch6:artist - title</c>.
    /// <para>
    /// Several are passed together on purpose. Nearly all the cost of a search is starting the
    /// tool -- measured at five to six seconds whether it returns one result or six -- so four
    /// tracks searched in one invocation take eight seconds where four separate invocations take
    /// forty-eight. Each result carries the query that produced it in <c>playlist_title</c>, which
    /// is how the answers are matched back to the questions.
    /// </para>
    /// </param>
    public static IReadOnlyList<string> ForSearch(
        IEnumerable<string> searchExpressions,
        AdvancedSettings advanced,
        string? detectedJsRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(searchExpressions);

        var args = Common(advanced, detectedJsRuntime);
        args.Add("--dump-json");
        args.Add("--flat-playlist");
        args.Add("--no-download");
        // One failing search must not abandon the others sharing this invocation.
        args.Add("--ignore-errors");

        args.Add(Separator);
        foreach (var expression in searchExpressions) args.Add(expression);
        return args;
    }

    /// <summary>
    /// Arguments for one download job.
    /// <para>
    /// The job runs in its own empty working directory and writes with a template ending in
    /// <c>.%(ext)s</c>, so the real container decides the extension. The caller then takes
    /// whatever media file appears there, which is more reliable than predicting the name.
    /// </para>
    /// </summary>
    /// <param name="request">The resolved job.</param>
    /// <param name="resolved">The concrete streams chosen for it.</param>
    /// <param name="plan">The container plan, used to decide remux versus recode.</param>
    /// <param name="workingDirectory">Empty directory the job writes into.</param>
    /// <param name="fileStem">File name without extension, already sanitised.</param>
    /// <param name="settings">Queue settings for retries, concurrency and rate limiting.</param>
    /// <param name="advanced">Tool paths and cookie configuration.</param>
    public static IReadOnlyList<string> ForDownload(
        DownloadRequest request,
        ResolvedFormats resolved,
        MuxPlan? plan,
        string workingDirectory,
        string fileStem,
        DownloadSettings settings,
        AdvancedSettings advanced,
        string? detectedJsRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolved);

        var args = Common(advanced, detectedJsRuntime);

        args.Add("--no-playlist");

        // Progress is reported through a sentinel-prefixed template so it can be told apart from
        // the extractor's ordinary output on the same stream.
        args.Add("--progress-template");
        args.Add(ProgressParser.DownloadTemplate);
        args.Add("--progress-template");
        args.Add(ProgressParser.PostProcessTemplate);

        AddTransferOptions(args, settings);
        AddFormatSelection(args, request, resolved);
        AddOutputTemplate(args, workingDirectory, fileStem);
        AddPostProcessing(args, request, plan);
        AddSubtitles(args, request);
        AddExtraArguments(args, advanced);

        args.Add(Separator);
        args.Add(request.Url);
        return args;
    }

    private static void AddTransferOptions(List<string> args, DownloadSettings settings)
    {
        args.Add("--retries");
        args.Add(Math.Clamp(settings.RetryCount, 0, 20).ToString(CultureInfo.InvariantCulture));
        args.Add("--fragment-retries");
        args.Add(Math.Clamp(settings.RetryCount, 0, 20).ToString(CultureInfo.InvariantCulture));

        args.Add("--concurrent-fragments");
        args.Add(Math.Clamp(settings.ConcurrentFragments, 1, 16).ToString(CultureInfo.InvariantCulture));

        if (settings.SpeedLimitKbps is > 0)
        {
            args.Add("--limit-rate");
            args.Add($"{settings.SpeedLimitKbps.Value}K");
        }

        // Partial files are what make a resumed download possible, so they stay on unless the
        // user turned resuming off entirely.
        args.Add(settings.ResumePartialDownloads ? "--continue" : "--no-continue");
        if (!settings.ResumePartialDownloads) args.Add("--no-part");
    }

    /// <summary>
    /// Pins the exact formats the user was shown.
    /// <para>
    /// Format ids come from the analysis the user actually saw, so what downloads is what the UI
    /// promised. No fallback chain is appended on purpose: if a format has since disappeared, the
    /// job fails with a message telling the user to analyse again, rather than silently
    /// substituting a different quality.
    /// </para>
    /// </summary>
    private static void AddFormatSelection(
        List<string> args, DownloadRequest request, ResolvedFormats resolved)
    {
        var ids = new List<string>();

        if (request.Mode is DownloadMode.Audio)
        {
            if (resolved.Audio.Count > 0) ids.Add(resolved.Audio[0].FormatId);
        }
        else
        {
            if (resolved.Video is not null) ids.Add(resolved.Video.FormatId);
            ids.AddRange(resolved.Audio.Select(a => a.FormatId));
        }

        if (ids.Count == 0) return;

        args.Add("-f");
        args.Add(string.Join("+", ids));

        // Merging more than one audio stream into a single output requires this to be explicit.
        if (request.Mode is not DownloadMode.Audio && resolved.Audio.Count > 1)
            args.Add("--audio-multistreams");
    }

    private static void AddOutputTemplate(List<string> args, string workingDirectory, string fileStem)
    {
        // A literal stem can contain '%', which the output template language would otherwise
        // interpret. Doubling it is how yt-dlp escapes a literal percent sign.
        var escapedStem = fileStem.Replace("%", "%%", StringComparison.Ordinal);

        args.Add("--paths");
        args.Add($"home:{workingDirectory}");
        args.Add("--paths");
        args.Add($"temp:{workingDirectory}");

        args.Add("-o");
        args.Add($"{escapedStem}.%(ext)s");
    }

    private static void AddPostProcessing(List<string> args, DownloadRequest request, MuxPlan? plan)
    {
        if (request.Mode is DownloadMode.Audio)
        {
            AddAudioExtraction(args, request);
        }
        else if (plan is not null)
        {
            var target = ContainerCompatibility.Extension(plan.Container);

            if (plan.RequiresTranscode)
            {
                // Re-encoding is only ever reached because the user pinned a container the source
                // streams cannot go into; the UI has already explained the cost by this point.
                args.Add("--recode-video");
                args.Add(target);
            }
            else
            {
                // Remuxing rewrites the container and copies the streams untouched.
                args.Add("--remux-video");
                args.Add(target);
                args.Add("--merge-output-format");
                args.Add(target);
            }
        }

        if (request.EmbedMetadata)
        {
            args.Add("--embed-metadata");
            if (request.EmbedChapters) args.Add("--embed-chapters");
        }

        if (request.EmbedThumbnail) args.Add("--embed-thumbnail");
        if (request.WriteThumbnailFile)
        {
            args.Add("--write-thumbnail");
            // Cover art is far more useful as a JPEG than as the WebP YouTube prefers.
            args.Add("--convert-thumbnails");
            args.Add("jpg");
        }
    }

    private static void AddAudioExtraction(List<string> args, DownloadRequest request)
    {
        args.Add("--extract-audio");

        if (request.AudioFormat == AudioOutputFormat.KeepOriginal)
        {
            // Without --audio-format, yt-dlp keeps the source stream and only strips the video
            // container. Nothing is decoded, so nothing is lost.
            return;
        }

        args.Add("--audio-format");
        args.Add(request.AudioFormat switch
        {
            AudioOutputFormat.Mp3 => "mp3",
            AudioOutputFormat.M4a => "m4a",
            AudioOutputFormat.Opus => "opus",
            AudioOutputFormat.Flac => "flac",
            AudioOutputFormat.Wav => "wav",
            AudioOutputFormat.Alac => "alac",
            _ => "best",
        });

        // 0 is the best VBR setting for the lossy encoders; lossless targets ignore it.
        args.Add("--audio-quality");
        args.Add(Math.Clamp(request.LossyQuality, 0, 10).ToString(CultureInfo.InvariantCulture));
    }

    private static void AddSubtitles(List<string> args, DownloadRequest request)
    {
        var subtitles = request.Subtitles;
        if (!subtitles.Enabled) return;

        args.Add("--write-subs");
        if (subtitles.IncludeAutomatic) args.Add("--write-auto-subs");

        args.Add("--sub-langs");
        args.Add(subtitles.AllLanguages || subtitles.Languages.Count == 0
            ? "all"
            : string.Join(",", subtitles.Languages));

        if (subtitles.Format != SubtitleFormatPreference.Original)
        {
            // Converting only ever rewrites the text container; timing and characters are preserved.
            args.Add("--convert-subs");
            args.Add(subtitles.Format switch
            {
                SubtitleFormatPreference.Srt => "srt",
                SubtitleFormatPreference.Vtt => "vtt",
                SubtitleFormatPreference.Ass => "ass",
                _ => "srt",
            });
        }

        if (subtitles.Embed) args.Add("--embed-subs");

        // --embed-subs alone deletes the sidecar files once they are inside the container.
        if (subtitles.KeepFiles) args.Add("--keep-video");
    }

    /// <summary>
    /// Appends the user's own arguments, split with full quoting rules into separate entries.
    /// They are still passed as argv, so this cannot inject a shell command.
    /// </summary>
    private static void AddExtraArguments(List<string> args, AdvancedSettings advanced)
    {
        if (string.IsNullOrWhiteSpace(advanced.ExtraYtDlpArguments)) return;
        args.AddRange(CommandLineSplitter.Split(advanced.ExtraYtDlpArguments));
    }

    /// <summary>
    /// The end-of-options marker. Everything after it is treated as a URL even if it begins with
    /// a dash, which stops a crafted URL from being read as a flag.
    /// </summary>
    private const string Separator = "--";

    /// <summary>Arguments to print the tool version.</summary>
    public static IReadOnlyList<string> ForVersion() => ["--version"];

    /// <summary>Arguments to update the tool in place.</summary>
    public static IReadOnlyList<string> ForSelfUpdate() => ["--update"];
}

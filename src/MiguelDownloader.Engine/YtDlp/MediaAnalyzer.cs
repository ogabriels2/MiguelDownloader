using System.Text.Json;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Core.Urls;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.Processes;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.YtDlp;

/// <summary>Raised when analysis fails, carrying an already-classified cause.</summary>
public sealed class AnalysisException(DownloadError error)
    : Exception($"Analysis failed: {error.Kind}")
{
    public DownloadError Error { get; } = error;
}

/// <summary>
/// Fetches what a URL actually contains.
/// <para>
/// Single items are extracted in full, including every format, because the user is about to
/// choose between them. Collections are listed flat: ids, titles and durations arrive quickly and
/// are enough to pick from, whereas fully extracting a large channel would mean one player
/// request per video. Format details for a collection item are fetched when that item is
/// actually queued.
/// </para>
/// </summary>
public sealed class MediaAnalyzer(
    ProcessRunner runner,
    ILogger<MediaAnalyzer> logger)
{
    private readonly ProcessRunner _runner = runner;
    private readonly ILogger<MediaAnalyzer> _logger = logger;

    /// <summary>
    /// Above this many entries a collection is listed in a first pass only, so the UI can show a
    /// count and ask for confirmation before the full listing is fetched.
    /// </summary>
    public const int LargeCollectionThreshold = 200;

    /// <summary>
    /// Analyses a URL that has already been parsed and classified.
    /// </summary>
    /// <param name="urlInfo">The parsed URL.</param>
    /// <param name="tools">Resolved tool paths.</param>
    /// <param name="advanced">Tool configuration, including cookies and the JS runtime.</param>
    /// <param name="listingLimit">
    /// Cap on how many collection entries to list. Null lists everything.
    /// </param>
    public async Task<AnalysisResult> AnalyzeAsync(
        MediaUrlInfo urlInfo,
        ToolPaths tools,
        AdvancedSettings advanced,
        int? listingLimit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urlInfo);
        ArgumentNullException.ThrowIfNull(tools);

        if (!tools.YtDlp.IsAvailable)
        {
            throw new AnalysisException(new DownloadError
            {
                Kind = DownloadErrorKind.YtDlpMissing,
                MessageKey = "Error_YtDlpMissing",
                Action = RecommendedAction.InstallTools,
            });
        }

        if (!urlInfo.IsDownloadable)
        {
            throw new AnalysisException(new DownloadError
            {
                Kind = urlInfo.Kind == MediaUrlKind.Search
                    ? DownloadErrorKind.UnsupportedUrl
                    : DownloadErrorKind.InvalidUrl,
                MessageKey = urlInfo.Kind == MediaUrlKind.Search
                    ? "Error_SearchUrlNotSupported"
                    : "Error_InvalidUrl",
                Action = RecommendedAction.CheckUrl,
            });
        }

        var effectiveAdvanced = tools.ApplyExecutionPolicy(advanced);
        var arguments = urlInfo.IsBulk
            ? YtDlpArguments.ForCollectionListing(
                urlInfo.CanonicalUrl, effectiveAdvanced, listingLimit, tools.JsRuntime.Path)
            : YtDlpArguments.ForAnalysis(urlInfo.CanonicalUrl, effectiveAdvanced, tools.JsRuntime.Path);

        var warnings = new List<string>();

        var result = await _runner.RunAsync(
            tools.YtDlp.Path!,
            arguments,
            onLine: line =>
            {
                if (line.IsError) CollectWarning(line.Text, warnings);
            },
            captureStandardOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var error = ErrorClassifier.Classify(result.StandardError);

            // An unrecognised failure is exactly the one whose raw text is worth keeping: there is
            // no diagnosis to read instead, and without it the log says only that something went
            // wrong. Classified causes stay one line, because their message already says it.
            if (error.Kind == DownloadErrorKind.Unknown)
            {
                _logger.LogWarning(
                    "Analysis of {Kind} failed, cause not recognised. Tool output: {Output}",
                    urlInfo.Kind, Truncate(result.StandardError));
            }
            else
            {
                _logger.LogWarning("Analysis of {Kind} failed: {Cause}", urlInfo.Kind, error.Kind);
            }
            throw new AnalysisException(error);
        }

        return Parse(result.StandardOutput, urlInfo, warnings);
    }

    /// <summary>
    /// Fetches the full format list for one item that was previously listed flat.
    /// Called when a collection entry is about to be queued.
    /// </summary>
    public async Task<MediaItem> GetItemDetailsAsync(
        string url,
        ToolPaths tools,
        AdvancedSettings advanced,
        CancellationToken cancellationToken = default)
    {
        var arguments = YtDlpArguments.ForAnalysis(
            url, tools.ApplyExecutionPolicy(advanced), tools.JsRuntime.Path);

        var result = await _runner.RunAsync(
            tools.YtDlp.Path!,
            arguments,
            captureStandardOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            throw new AnalysisException(ErrorClassifier.Classify(result.StandardError));

        using var document = JsonDocument.Parse(result.StandardOutput);
        return InfoMapper.MapItem(document.RootElement);
    }

    /// <summary>
    /// Keeps an unrecognised failure readable in the log without letting a runaway extractor dump
    /// megabytes into it. The full text still reaches the diagnostics bundle.
    /// </summary>
    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no output)";
        var trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[..2000] + "... (truncated)";
    }

    private AnalysisResult Parse(string json, MediaUrlInfo urlInfo, List<string> warnings)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "The extractor returned output that is not valid JSON");
            throw new AnalysisException(new DownloadError
            {
                Kind = DownloadErrorKind.Unknown,
                MessageKey = "Error_UnexpectedToolOutput",
                Action = RecommendedAction.UpdateTools,
                TechnicalDetails = ex.Message,
            });
        }

        using (document)
        {
            var root = document.RootElement;

            if (InfoMapper.IsCollection(root))
            {
                var collection = InfoMapper.MapCollection(root);

                if (collection.Count == 0)
                {
                    throw new AnalysisException(new DownloadError
                    {
                        Kind = DownloadErrorKind.PlaylistEmpty,
                        MessageKey = "Error_PlaylistEmpty",
                        Action = RecommendedAction.CheckUrl,
                    });
                }

                if (collection.IsTruncated)
                    warnings.Add("Warning_CollectionTruncated");

                if (urlInfo.Kind == MediaUrlKind.Mix)
                    warnings.Add("Warning_MixIsUnbounded");

                return new AnalysisResult
                {
                    Source = urlInfo,
                    Collection = collection,
                    Profile = DecideProfile(collection.Kind, urlInfo),
                    Warnings = warnings,
                };
            }

            var item = InfoMapper.MapItem(root);

            if (item.HasDrmFormats && item.SelectableFormats.All(f => !f.HasVideo && !f.HasAudio))
            {
                throw new AnalysisException(new DownloadError
                {
                    Kind = DownloadErrorKind.DrmProtected,
                    MessageKey = "Error_DrmProtected",
                    Action = RecommendedAction.None,
                });
            }

            if (!item.SelectableFormats.Any())
            {
                throw new AnalysisException(new DownloadError
                {
                    Kind = DownloadErrorKind.NoFormatsFound,
                    MessageKey = "Error_NoFormatsFound",
                    Action = RecommendedAction.UpdateTools,
                });
            }

            if (item.IsUpcoming) warnings.Add("Warning_UpcomingBroadcast");
            if (item.IsLive) warnings.Add("Warning_LiveStream");

            return new AnalysisResult
            {
                Source = urlInfo,
                Item = item,
                Profile = DecideProfile(item.Kind, urlInfo),
                Warnings = warnings,
            };
        }
    }

    /// <summary>
    /// Chooses which flow to present. A music.youtube.com URL always means the music flow, even
    /// for a plain video, because that is what the user was looking at when they copied it.
    /// </summary>
    private static ContentProfile DecideProfile(MediaKind kind, MediaUrlInfo urlInfo)
    {
        // Sites that only carry audio open on the music flow for the same reason
        // music.youtube.com does: offering a video mode the source cannot satisfy would be a
        // control that does nothing.
        if (urlInfo.IsAudioFirst) return ContentProfile.Music;

        return kind switch
        {
            MediaKind.Music or MediaKind.Album => ContentProfile.Music,
            _ => ContentProfile.Video,
        };
    }

    /// <summary>
    /// Picks out the warnings worth surfacing. Most extractor chatter is noise; these two change
    /// what the user actually gets, so they are shown rather than only logged.
    /// </summary>
    private void CollectWarning(string line, List<string> warnings)
    {
        if (line.Contains("No supported JavaScript runtime", StringComparison.OrdinalIgnoreCase))
        {
            if (!warnings.Contains("Warning_NoJsRuntime")) warnings.Add("Warning_NoJsRuntime");
            _logger.LogWarning("No JavaScript runtime available; some formats may be withheld");
            return;
        }

        if (line.Contains("Some formats may be missing", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Signature extraction failed", StringComparison.OrdinalIgnoreCase))
        {
            if (!warnings.Contains("Warning_FormatsMayBeMissing")) warnings.Add("Warning_FormatsMayBeMissing");
        }
    }
}

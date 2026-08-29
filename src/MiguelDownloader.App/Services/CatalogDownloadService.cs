using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Naming;
using MiguelDownloader.Engine.Catalog;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.Files;
using Microsoft.Extensions.Logging;
using System.IO;

namespace MiguelDownloader.App.Services;

/// <summary>What happened to one catalogue track on its way to the queue.</summary>
public sealed record CatalogQueueResult(
    CatalogTrack Track, bool Queued, string Source, string Audio, string? Reason);

/// <summary>
/// Turns a resolved catalogue into queued downloads.
/// <para>
/// The matching happens here rather than during analysis, and each track is queued the moment it
/// is matched. Analysis therefore stays fast -- it is only a catalogue read -- and the first file
/// begins transferring while the rest of the release is still being looked for.
/// </para>
/// <para>
/// Nothing is fetched from the platform the link came from. Its audio is encrypted; what travels
/// from it is the identity of each recording, which then gets written into the file as tags.
/// </para>
/// </summary>
public sealed class CatalogDownloadService(
    CatalogMatchSession session,
    SettingsService settings,
    QueueCoordinator queue,
    ToolService tools,
    ILogger<CatalogDownloadService> logger)
{
    private readonly CatalogMatchSession _session = session;
    private readonly SettingsService _settings = settings;
    private readonly QueueCoordinator _queue = queue;
    private readonly ToolService _tools = tools;
    private readonly ILogger<CatalogDownloadService> _logger = logger;

    /// <summary>
    /// Matches every selected track and queues it as it resolves, reporting each outcome so the
    /// interface can show progress rather than a blank wait.
    /// </summary>
    public async Task<IReadOnlyList<CatalogQueueResult>> QueueAsync(
        MusicCatalog catalog,
        IReadOnlyList<CatalogTrack> selected,
        string targetDirectory,
        Action<CatalogQueueResult>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selected);

        var toolPaths = _tools.Current ?? await _tools.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!toolPaths.YtDlp.IsAvailable)
        {
            _logger.LogWarning("Cannot queue a catalogue without yt-dlp");
            return [];
        }

        // Only the chosen tracks are matched. Searching for the rest would cost time for downloads
        // nobody asked for.
        var wanted = catalog with { Tracks = selected };
        var advanced = _settings.Current.Advanced;

        // Album layout: one folder per release, so a fourteen-track album does not scatter itself
        // through the music folder.
        var directory = catalog.Kind is CatalogKind.Album or CatalogKind.Playlist
            ? Path.Combine(targetDirectory, FileNameSanitizer.SanitizeComponent(FolderNameFor(catalog)))
            : targetDirectory;

        var results = new List<CatalogQueueResult>(selected.Count);

        await foreach (var matched in _session
            .MatchAllAsync(wanted, toolPaths, advanced, cancellationToken)
            .ConfigureAwait(false))
        {
            var result = matched.Match is { } match
                ? await QueueOneAsync(matched.Track, match, catalog, directory, cancellationToken)
                    .ConfigureAwait(false)
                : new CatalogQueueResult(
                    matched.Track, false, string.Empty, string.Empty, "Error_TrackNotFound");

            results.Add(result);
            onProgress?.Invoke(result);
        }

        var queued = results.Count(r => r.Queued);
        _logger.LogInformation(
            "Queued {Queued} of {Total} track(s) from the {Platform} catalogue",
            queued, results.Count, catalog.Platform);

        return results;
    }

    private async Task<CatalogQueueResult> QueueOneAsync(
        CatalogTrack track, TrackMatch match, MusicCatalog catalog,
        string directory, CancellationToken cancellationToken)
    {
        try
        {
            // A stub carries no formats: the executor already knows to fetch them from the URL,
            // which is exactly what a flat playlist entry needs too. The metadata travelling
            // alongside is the catalogue's, not the source's, which is the whole point -- the
            // file ends up tagged as the release rather than as whatever the upload was called.
            var item = new MediaItem
            {
                Id = track.Isrc ?? match.Url,
                Title = track.Title,
                WebpageUrl = match.Url,
                ChannelName = track.Artist,
                Uploader = track.Artist,
                Duration = track.Duration,
                Kind = MediaKind.Music,
                Music = track.ToMetadata(),
                IsStub = true,
            };

            var name = FileNameSanitizer.SanitizeComponent(
                track.TrackNumber is > 0
                    ? $"{track.TrackNumber:D2} - {track.Artist} - {track.Title}"
                    : $"{track.Artist} - {track.Title}");

            var request = new DownloadRequest
            {
                Url = match.Url,
                Item = item,
                Mode = DownloadMode.Audio,
                Selection = new Core.Formats.FormatSelection(),
                Container = Core.Formats.ContainerFormat.Auto,
                AudioFormat = _settings.Current.Music.Format,
                LossyQuality = _settings.Current.Music.LossyQuality,
                EmbedThumbnail = _settings.Current.Music.EmbedCoverArt,
                EmbedMetadata = true,
                Music = track.ToMetadata(),
                TargetDirectory = directory,
                TargetFileName = FileNameSanitizer.EnsurePathFits(directory, name + ".m4a"),
                CollectionId = catalog.PlatformUrl,
                CollectionTitle = catalog.Title,
                IndexInCollection = track.TrackNumber,
            };

            _queue.Queue.Enqueue(new DownloadTask
            {
                Id = Guid.NewGuid().ToString("N"),
                Request = request,
            });

            await Task.CompletedTask.ConfigureAwait(false);

            return new CatalogQueueResult(
                track, true, match.Source,
                match.IsLossless ? match.AudioDescription : string.Empty, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue {Title}", track.Title);
            return new CatalogQueueResult(track, false, match.Source, string.Empty, "Error_Unknown");
        }
    }

    private static string FolderNameFor(MusicCatalog catalog)
        => string.IsNullOrWhiteSpace(catalog.Artist)
            ? catalog.Title
            : $"{catalog.Artist} - {catalog.Title}";
}

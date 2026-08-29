using System.IO;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Naming;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Files;
using MiguelDownloader.Engine.Music;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.Services;

/// <summary>The user's choices for one download, before they are turned into requests.</summary>
public sealed record DownloadChoice
{
    public DownloadMode Mode { get; init; } = DownloadMode.Video;
    public FormatSelection Selection { get; init; } = new();
    public ContainerFormat Container { get; init; } = ContainerFormat.Auto;
    public AudioOutputFormat AudioFormat { get; init; } = AudioOutputFormat.KeepOriginal;
    public int LossyQuality { get; init; }
    public SubtitleRequest Subtitles { get; init; } = SubtitleRequest.Disabled;
    public bool EmbedThumbnail { get; init; }
    public bool WriteThumbnailFile { get; init; }
    public bool EmbedMetadata { get; init; } = true;
    public bool EmbedChapters { get; init; } = true;

    /// <summary>Overrides the configured destination for this download only.</summary>
    public string? TargetDirectoryOverride { get; init; }
}

/// <summary>
/// Turns an analysis plus the user's choices into concrete, fully-resolved requests.
/// <para>
/// All naming happens here, before anything is queued, so the destination is known up front. That
/// is what lets the app check for collisions, keep within the Windows path limit, and lay out
/// album folders correctly rather than discovering a problem after the bytes have been fetched.
/// </para>
/// </summary>
public sealed class DownloadPlanner(SettingsService settings, ILogger<DownloadPlanner> logger)
{
    private readonly SettingsService _settings = settings;
    private readonly ILogger<DownloadPlanner> _logger = logger;

    /// <summary>Builds the request for a single item.</summary>
    public DownloadRequest PlanSingle(MediaItem item, DownloadChoice choice)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(choice);

        var isMusic = choice.Mode == DownloadMode.Audio;
        var music = isMusic ? MusicTagger.ForSingle(item) : null;

        var directory = choice.TargetDirectoryOverride
            ?? (isMusic ? _settings.ResolveMusicFolder() : _settings.ResolveDownloadFolder());

        var fileName = BuildFileName(item, choice, music, index: null, collection: null);

        return BuildRequest(item, choice, directory, fileName, music, collection: null, index: null);
    }

    /// <summary>
    /// Builds requests for a whole collection.
    /// <para>
    /// Position within the listing drives both the file name and the track number, so an album
    /// downloads in the right order even when the individual uploads carry no track tags.
    /// </para>
    /// </summary>
    public IReadOnlyList<DownloadRequest> PlanCollection(
        MediaCollection collection, IReadOnlyList<MediaItem> selectedItems, DownloadChoice choice)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(selectedItems);

        var isMusic = choice.Mode == DownloadMode.Audio;
        var isAlbum = collection.Kind == MediaKind.Album;
        var total = selectedItems.Count;

        var root = choice.TargetDirectoryOverride
            ?? (isMusic ? _settings.ResolveMusicFolder() : _settings.ResolveDownloadFolder());

        var directory = BuildCollectionDirectory(root, collection, choice, isMusic, isAlbum);

        var requests = new List<DownloadRequest>(total);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < total; i++)
        {
            var item = selectedItems[i];
            // The listing order is authoritative; item.PlaylistIndex can be absent on a flat listing.
            var position = i + 1;

            var music = isMusic
                ? isAlbum
                    ? MusicTagger.ForAlbumTrack(item, collection, position, total)
                    : MusicTagger.ForSingle(item) with { TrackNumber = position, TrackTotal = total }
                : null;

            var fileName = BuildFileName(item, choice, music, position, collection);

            // Two videos in one playlist can share a title; make the names distinct up front so
            // they do not race for the same path once several are downloading at once.
            fileName = EnsureDistinct(fileName, usedNames);

            requests.Add(BuildRequest(item, choice, directory, fileName, music, collection, position));
        }

        _logger.LogInformation("Planned {Count} downloads into {Directory}", requests.Count, directory);
        return requests;
    }

    private static string EnsureDistinct(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (used.Add(candidate)) return candidate;
        }
        return fileName;
    }

    /// <summary>
    /// How much shorter the name has to be so it also fits where the download is actually built.
    /// <para>
    /// The file is written into <c>&lt;temp root&gt;\&lt;task id&gt;</c> and only moved to the
    /// destination once it is complete, and while yt-dlp works it appends <c>.f&lt;format-id&gt;</c>
    /// to each part. A name budgeted against the destination alone is therefore too long wherever
    /// the working path is the longer of the two.
    /// </para>
    /// <para>
    /// This never showed on YouTube, whose titles are short. A Facebook reel carries the view
    /// count, the reaction count, the whole caption and the page name in its title — over 170
    /// characters — and the download failed with a path the user was never shown.
    /// </para>
    /// </summary>
    private int WorkingPathReserve(string destinationDirectory)
    {
        var root = WorkspaceManager.ResolveRoot(_settings.Current.Downloads);

        // Task ids are GUIDs in "N" form; allow for the separator too.
        const int TaskIdAllowance = 32 + 1;

        // yt-dlp's per-part suffix: ".f" plus a format id, which these sites make long.
        const int FormatSuffixAllowance = 24;

        var workingLength = root.Length + TaskIdAllowance;
        return Math.Max(0, workingLength - destinationDirectory.Length) + FormatSuffixAllowance;
    }

    private DownloadRequest BuildRequest(
        MediaItem item, DownloadChoice choice, string directory, string fileName,
        MusicMetadata? music, MediaCollection? collection, int? index)
    {
        var fitted = FileNameSanitizer.EnsurePathFits(
            directory, fileName, reserve: WorkingPathReserve(directory));

        return new DownloadRequest
        {
            Url = item.WebpageUrl,
            Item = item,
            Mode = choice.Mode,
            Selection = choice.Selection,
            Container = choice.Container,
            AudioFormat = choice.AudioFormat,
            LossyQuality = choice.LossyQuality,
            Subtitles = choice.Subtitles,
            EmbedThumbnail = choice.EmbedThumbnail,
            WriteThumbnailFile = choice.WriteThumbnailFile,
            EmbedMetadata = choice.EmbedMetadata,
            EmbedChapters = choice.EmbedChapters,
            TargetDirectory = directory,
            TargetFileName = fitted,
            ExistingFilePolicy = _settings.Current.Downloads.ExistingFilePolicy,
            Music = music,
            CollectionId = collection?.Id,
            CollectionTitle = collection?.Title,
            IndexInCollection = index,
        };
    }

    /// <summary>
    /// Works out the folder for a collection: an album gets the artist/album layout when that is
    /// enabled, a playlist gets its own folder, and everything else lands in the root.
    /// </summary>
    private string BuildCollectionDirectory(
        string root, MediaCollection collection, DownloadChoice choice, bool isMusic, bool isAlbum)
    {
        var music = _settings.Current.Music;
        var video = _settings.Current.Video;

        if (isMusic && isAlbum && music.UseAlbumFolders)
        {
            var template = music.IncludeYearInAlbumFolder && collection.Year is > 0
                ? NameTemplate.Defaults.AlbumDirectoryWithYear
                : NameTemplate.Defaults.AlbumDirectory;

            var values = NameTemplate.BuildValues(
                ("album_artist", collection.AlbumArtist ?? collection.ChannelName),
                ("album", collection.Title),
                ("year", collection.Year));

            var relative = NameTemplate.Expand(template, values, allowSubdirectories: true);
            if (!string.IsNullOrWhiteSpace(relative))
                return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        if (!isMusic && !video.CreatePlaylistFolder) return root;

        var folderValues = NameTemplate.BuildValues(("playlist_title", collection.Title));
        var folder = NameTemplate.Expand(NameTemplate.Defaults.PlaylistDirectory, folderValues);

        return string.IsNullOrWhiteSpace(folder) ? root : Path.Combine(root, folder);
    }

    /// <summary>
    /// Expands the configured template into a file name.
    /// <para>
    /// The extension is a best guess: the real container is only known once the streams are
    /// chosen, and the pipeline corrects it from what was actually produced before the file is
    /// moved into place.
    /// </para>
    /// </summary>
    private string BuildFileName(
        MediaItem item, DownloadChoice choice, MusicMetadata? music,
        int? index, MediaCollection? collection)
    {
        var isMusic = choice.Mode == DownloadMode.Audio;
        var settings = _settings.Current;

        var template = isMusic
            ? index is not null || (music?.HasAlbumContext ?? false)
                ? settings.Music.TrackTemplate
                : settings.Music.SingleTemplate
            : index is not null
                ? settings.Video.PlaylistItemTemplate
                : settings.Video.FileNameTemplate;

        var values = NameTemplate.BuildValues(
            ("title", music?.Title ?? item.Title),
            ("id", item.Id),
            ("uploader", item.Uploader),
            ("channel", item.ChannelName),
            ("artist", music?.Artist ?? item.DisplayAuthor),
            ("album", music?.Album),
            ("album_artist", music?.AlbumArtist),
            ("track_number", music?.TrackNumber ?? index),
            ("track_total", music?.TrackTotal),
            ("disc_number", music?.DiscNumber),
            ("year", music?.Year ?? item.UploadDate?.Year),
            ("upload_date", item.UploadDate?.ToString("yyyy-MM-dd")),
            ("playlist_title", collection?.Title),
            ("playlist_index", index),
            ("playlist_id", collection?.Id),
            ("genre", music?.Genre),
            ("duration", item.Duration?.ToString(@"hh\-mm\-ss")));

        var stem = NameTemplate.Expand(template, values);

        if (string.IsNullOrWhiteSpace(stem))
            stem = FileNameSanitizer.SanitizeComponent(item.Title, item.Id);

        return stem + "." + GuessExtension(choice);
    }

    /// <summary>
    /// The extension the result is expected to have. Corrected after the download from the file
    /// that was actually produced, so a wrong guess here is never visible to the user.
    /// </summary>
    private static string GuessExtension(DownloadChoice choice)
    {
        if (choice.Mode == DownloadMode.Audio)
        {
            return choice.AudioFormat switch
            {
                AudioOutputFormat.Mp3 => "mp3",
                AudioOutputFormat.M4a => "m4a",
                AudioOutputFormat.Opus => "opus",
                AudioOutputFormat.Flac => "flac",
                AudioOutputFormat.Wav => "wav",
                AudioOutputFormat.Alac => "m4a",
                _ => "m4a",
            };
        }

        return choice.Container switch
        {
            ContainerFormat.Mp4 => "mp4",
            ContainerFormat.WebM => "webm",
            ContainerFormat.Mkv => "mkv",
            _ => "mkv",
        };
    }
}

using System.Net.Http;
using System.Text.Json;
using MiguelDownloader.Core.Music;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>
/// Adds MusicBrainz identifiers to catalogue tracks, looked up by ISRC.
/// <para>
/// The ISRC is an exact key, so this is a lookup rather than a guess: the recording it returns is
/// the same recording, not one with a similar name. The identifiers it adds are what let a
/// library be reconciled later against any other tool that speaks MusicBrainz.
/// </para>
/// <para>
/// Tracks without an ISRC are left exactly as they were. Searching by title instead would return
/// something plausible for almost any input, and writing a plausible identifier into a file is
/// worse than writing none.
/// </para>
/// </summary>
public sealed class MusicBrainzEnricher(HttpClient http, ILogger<MusicBrainzEnricher> logger)
{
    private const string ApiRoot = "https://musicbrainz.org/ws/2";

    /// <summary>
    /// Identifies this client to MusicBrainz in the form their policy asks for: application,
    /// version, and a contact that reaches a person. The version is read from the assembly so it
    /// stops being a number someone has to remember to bump.
    /// </summary>
    private static readonly string UserAgent =
        $"MiguelDownloader/{typeof(MusicBrainzEnricher).Assembly.GetName().Version?.ToString(3) ?? "1.0"} " +
        "( contato@ogabriels.com )";

    /// <summary>
    /// MusicBrainz asks for no more than one request a second from a given client, and enforces
    /// it. Honouring that is the price of using an open service that asks for nothing else.
    /// </summary>
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// Above this many tracks the wait stops being worth it: a hundred-track playlist would spend
    /// nearly two minutes on identifiers the user did not ask for. Albums are enriched, playlists
    /// are not, unless the caller says otherwise.
    /// </summary>
    public const int DefaultMaximumTracks = 30;

    private readonly HttpClient _http = http;
    private readonly ILogger<MusicBrainzEnricher> _logger = logger;
    private DateTimeOffset _nextAllowedRequest = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<CatalogTrack>> EnrichAsync(
        IReadOnlyList<CatalogTrack> tracks,
        CancellationToken cancellationToken = default,
        int? maximumTracks = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var cap = maximumTracks ?? DefaultMaximumTracks;
        if (tracks.Count > cap)
        {
            _logger.LogInformation(
                "Skipping MusicBrainz for {Count} tracks; above the {Cap} the wait costs more than it adds",
                tracks.Count, cap);
            return tracks;
        }

        var result = new List<CatalogTrack>(tracks.Count);
        var found = 0;

        foreach (var track in tracks)
        {
            if (cancellationToken.IsCancellationRequested) { result.Add(track); continue; }

            if (string.IsNullOrWhiteSpace(track.Isrc)) { result.Add(track); continue; }

            var enriched = await LookupAsync(track, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(enriched, track)) found++;
            result.Add(enriched);
        }

        _logger.LogInformation("MusicBrainz identified {Found} of {Total} recording(s)", found, tracks.Count);
        return result;
    }

    private async Task<CatalogTrack> LookupAsync(CatalogTrack track, CancellationToken cancellationToken)
    {
        var json = await GetAsync(
            $"{ApiRoot}/recording?query=isrc:{Uri.EscapeDataString(track.Isrc!)}&fmt=json&limit=1",
            cancellationToken).ConfigureAwait(false);

        if (json is null
            || !json.Value.TryGetProperty("recordings", out var recordings)
            || recordings.ValueKind != JsonValueKind.Array
            || recordings.GetArrayLength() == 0)
        {
            return track;
        }

        var recording = recordings[0];
        var recordingId = recording.String("id");

        string? artistId = null;
        if (recording.TryGetProperty("artist-credit", out var credits)
            && credits.ValueKind == JsonValueKind.Array && credits.GetArrayLength() > 0
            && credits[0].TryGetProperty("artist", out var artist))
        {
            artistId = artist.String("id");
        }

        string? releaseId = null;
        if (recording.TryGetProperty("releases", out var releases)
            && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
        {
            releaseId = releases[0].String("id");
        }

        if (recordingId is null && artistId is null && releaseId is null) return track;

        return track with
        {
            MusicBrainzRecordingId = recordingId,
            MusicBrainzArtistId = artistId,
            MusicBrainzReleaseId = releaseId,
        };
    }

    private async Task<JsonElement?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var wait = _nextAllowedRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            _nextAllowedRequest = DateTimeOffset.UtcNow + RequestInterval;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // MusicBrainz requires an identifying agent and refuses generic ones. The contact has
            // to be real: their policy is to throttle or block clients they cannot reach about
            // misbehaviour, and the address here used to be a repository that does not exist.
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("MusicBrainz answered {Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "Could not reach MusicBrainz");
            return null;
        }
    }
}

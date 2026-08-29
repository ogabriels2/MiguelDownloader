using System.Net.Http;
using System.Text;
using System.Text.Json;
using MiguelDownloader.Core.Music;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>A place a recording might exist without loss, and the link to check.</summary>
public sealed record LosslessCandidate(string Url, string Source, string Description)
{
    /// <summary>
    /// True once a cheap check has confirmed this source really does hold a lossless file.
    /// <para>
    /// The confirmation matters because the expensive step is next: opening the candidate with
    /// yt-dlp costs six to seven seconds, and most candidates do not deserve it. A Bandcamp
    /// release that is for sale offers a 128 kbit/s stream and nothing else -- worse than the
    /// fallback -- and an Archive item without a FLAC has nothing to add either. Both can be
    /// ruled out with a single request costing about a second.
    /// </para>
    /// </summary>
    public bool ConfirmedLossless { get; init; }
}

/// <summary>
/// Looks for a recording where it may exist losslessly and free.
/// <para>
/// Both sources here are searched because their rights holders chose to distribute this way, not
/// because of any weakness in their protection. Bandcamp serves FLAC, WAV, ALAC and AIFF for a
/// release the artist priced at zero; the Internet Archive hosts, among much else, decades of
/// live recordings taped with the bands' explicit permission. Neither is a way around anything.
/// </para>
/// <para>
/// For major-label catalogue neither will have the recording, and the search costs a couple of
/// seconds before falling back. That is the trade: the sources that carry commercial music free
/// of charge carry it lossily, and no amount of searching changes it.
/// </para>
/// </summary>
public sealed class LosslessSearcher(HttpClient http, ILogger<LosslessSearcher> logger)
{
    private const string BandcampSearch =
        "https://bandcamp.com/api/bcsearch_public_api/1/autocomplete_elastic";

    private const string ArchiveSearch = "https://archive.org/advancedsearch.php";

    private readonly HttpClient _http = http;
    private readonly ILogger<LosslessSearcher> _logger = logger;

    /// <summary>
    /// Candidate links worth probing, best-guess first. An empty list means the recording is not
    /// on either, which for most commercial music is the expected answer.
    /// </summary>
    public async Task<IReadOnlyList<LosslessCandidate>> FindAsync(
        CatalogTrack track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        // Both searches at once: they are independent, and each is a second of waiting.
        var bandcamp = SearchBandcampAsync(track, cancellationToken);
        var archive = SearchArchiveAsync(track, cancellationToken);
        await Task.WhenAll(bandcamp, archive).ConfigureAwait(false);

        var candidates = bandcamp.Result.Concat(archive.Result).ToList();
        if (candidates.Count == 0) return [];

        // Confirm cheaply before anything expensive happens. Opening a candidate properly costs
        // six or seven seconds each; this costs about one, and rules out the great majority --
        // a Bandcamp release that is for sale, or an Archive item holding no FLAC.
        var checks = candidates.Select(c => ConfirmAsync(c, cancellationToken)).ToList();
        var confirmed = (await Task.WhenAll(checks).ConfigureAwait(false))
            .Where(c => c.ConfirmedLossless)
            .ToList();

        _logger.LogInformation(
            "{Track}: {Confirmed} of {Total} candidate(s) actually hold a lossless file",
            track.SearchQuery, confirmed.Count, candidates.Count);

        return confirmed;
    }

    /// <summary>
    /// Establishes with one request whether a candidate really offers a lossless file.
    /// </summary>
    private async Task<LosslessCandidate> ConfirmAsync(
        LosslessCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            if (candidate.Source == "Internet Archive")
            {
                // The Archive's own metadata lists every file with its format, in about a second,
                // where opening the item with yt-dlp enumerates the whole show and takes six.
                var identifier = candidate.Url.Split('/').Last();
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"https://archive.org/metadata/{identifier}");
                AddBrowserHeaders(request);

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return candidate;

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);

                if (!document.RootElement.TryGetProperty("files", out var files)
                    || files.ValueKind != JsonValueKind.Array)
                {
                    return candidate;
                }

                var hasLossless = files.EnumerateArray().Any(f =>
                {
                    var format = f.String("format")?.ToLowerInvariant() ?? string.Empty;
                    return format.Contains("flac", StringComparison.Ordinal)
                        || format.Contains("wave", StringComparison.Ordinal);
                });

                return candidate with { ConfirmedLossless = hasLossless };
            }

            // Bandcamp names a free download page only when the artist priced the release at
            // zero, and that is exactly when FLAC, WAV, ALAC and AIFF are on offer. Its absence
            // means a 128 kbit/s stream, which is worse than the fallback and not worth opening.
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
            AddBrowserHeaders(pageRequest);

            using var page = await _http.SendAsync(pageRequest, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccessStatusCode) return candidate;

            var html = await page.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var free = html.Contains("freeDownloadPage", StringComparison.Ordinal)
                       || html.Contains("free_download_page", StringComparison.Ordinal);

            return candidate with { ConfirmedLossless = free };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "Could not confirm {Url}", candidate.Url);
            return candidate;
        }
    }

    private async Task<List<LosslessCandidate>> SearchBandcampAsync(
        CatalogTrack track, CancellationToken cancellationToken)
    {
        var results = new List<LosslessCandidate>();
        try
        {
            // "t" restricts to tracks. The album search would find the release without telling us
            // which entry is the recording we want.
            var payload = JsonSerializer.Serialize(new
            {
                search_text = $"{track.Artist} {track.Title}",
                search_filter = "t",
                full_page = false,
                fan_id = (string?)null,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, BandcampSearch)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            AddBrowserHeaders(request);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return results;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("auto", out var auto)
                || !auto.TryGetProperty("results", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var entry in list.EnumerateArray().Take(4))
            {
                var url = entry.String("item_url_path") ?? entry.String("url");
                var name = entry.String("name");
                var band = entry.String("band_name");
                if (url is null || name is null) continue;

                // Only worth probing when the names agree; Bandcamp's search is fuzzy enough to
                // return an unrelated track for almost any input.
                if (!DeezerCatalogResolver.TitlesAgree(track.Title, name)) continue;

                results.Add(new LosslessCandidate(url, "Bandcamp", $"{name} / {band}"));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "Bandcamp search failed");
        }
        return results;
    }

    private async Task<List<LosslessCandidate>> SearchArchiveAsync(
        CatalogTrack track, CancellationToken cancellationToken)
    {
        var results = new List<LosslessCandidate>();
        try
        {
            // Restricted to items that actually hold a FLAC: an audio item without one has
            // nothing to offer over the fallback.
            var query = $"mediatype:audio AND format:FLAC AND ({Quote(track.Title)})";
            if (!string.IsNullOrWhiteSpace(track.Artist))
                query += $" AND ({Quote(track.Artist)})";

            var url = $"{ArchiveSearch}?q={Uri.EscapeDataString(query)}" +
                      "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&rows=3&output=json";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return results;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("response", out var wrapper)
                || !wrapper.TryGetProperty("docs", out var docs)
                || docs.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var doc in docs.EnumerateArray())
            {
                var identifier = doc.String("identifier");
                if (identifier is null) continue;

                results.Add(new LosslessCandidate(
                    $"https://archive.org/details/{identifier}",
                    "Internet Archive",
                    doc.String("title") ?? identifier));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "Internet Archive search failed");
        }
        return results;
    }

    /// <summary>
    /// Wraps a phrase for the Archive's Lucene syntax, dropping the characters it treats as
    /// operators rather than escaping them one by one.
    /// </summary>
    internal static string Quote(string value)
    {
        var cleaned = new StringBuilder(value.Length + 2);
        cleaned.Append('"');
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '\'') cleaned.Append(c);
            else cleaned.Append(' ');
        }
        cleaned.Append('"');
        return cleaned.ToString();
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }
}

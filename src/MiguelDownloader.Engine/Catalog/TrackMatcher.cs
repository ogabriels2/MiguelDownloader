using System.Text.Json;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>One candidate for a catalogue recording, with the reasoning behind its score.</summary>
public sealed record TrackMatch
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Uploader { get; init; }
    public TimeSpan? Duration { get; init; }
    public required int Score { get; init; }

    /// <summary>Difference from the length the catalogue states, when both are known.</summary>
    public TimeSpan? DurationDelta { get; init; }

    /// <summary>Why this scored as it did, shown so a questionable match can be judged.</summary>
    public required string Reasoning { get; init; }

    /// <summary>Where it was found, named as the user would name it.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// True when this source offers the recording without loss.
    /// <para>
    /// Read from the format list, never assumed from the site: Bandcamp serves FLAC for a release
    /// priced at zero and a 128 kbit/s stream for one that is not, and the difference is visible
    /// only in the formats it actually returns.
    /// </para>
    /// </summary>
    public bool IsLossless { get; init; }

    /// <summary>
    /// What the best audio here really is, phrased for display: "FLAC 16 bit/44,1 kHz" or
    /// "Opus 128 kbit/s". Never a claim the formats do not support.
    /// </summary>
    public string AudioDescription { get; init; } = string.Empty;

    /// <summary>
    /// True when the match is close enough to queue without asking. A weaker one is still offered,
    /// but as a suggestion rather than a conclusion.
    /// </summary>
    public bool IsConfident => Score >= 70;
}

/// <summary>
/// Finds a catalogue recording somewhere that serves audio openly.
/// <para>
/// Duration decides this. A title search returns something for any input, and the plausible wrong
/// answers are the dangerous ones: searching SoundCloud for a Daft Punk track returns the
/// artist's own official upload, at thirty seconds, because it is a preview. Only the length
/// tells the two apart, and it tells them apart reliably.
/// </para>
/// </summary>
public sealed class TrackMatcher(
    ProcessRunner runner, LosslessSearcher lossless, ILogger<TrackMatcher> logger)
{
    private readonly LosslessSearcher _lossless = lossless;

    /// <summary>
    /// Lossless codecs, as yt-dlp names them, best first. Membership of this list is the only
    /// thing that earns a result the word "lossless" anywhere in the interface.
    /// <para>
    /// The order is by size for identical audio. Bandcamp offers the same recording as FLAC at
    /// 39 MB and as WAV at 63 MB, bit for bit the same sound, so taking the WAV would cost 60%
    /// more disk and more transfer for nothing at all. Compressed lossless first, uncompressed
    /// last.
    /// </para>
    /// </summary>
    private static readonly string[] LosslessCodecs =
        ["flac", "alac", "ape", "wv", "tta", "aiff", "wav", "pcm"];

    /// <summary>
    /// How far a candidate may sit from the stated length before it is refused outright.
    /// <para>
    /// Generous enough for a track that fades differently or carries a couple of seconds of
    /// silence, tight enough that a preview, an edit or a different arrangement cannot pass.
    /// </para>
    /// </summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(12);

    private readonly ProcessRunner _runner = runner;
    private readonly ILogger<TrackMatcher> _logger = logger;

    /// <summary>Words that mean this is not the recording the catalogue described.</summary>
    private static readonly (string Marker, int Penalty)[] Disqualifiers =
    [
        ("live at", 40), ("live in", 40), ("live from", 40), ("(live", 40),
        ("cover by", 45), ("karaoke", 60), ("instrumental", 35),
        ("sped up", 50), ("slowed", 50), ("nightcore", 60), ("8d audio", 50),
        ("reverb", 30), ("mashup", 40), ("tribute", 40), ("preview", 50),
        ("reaction", 60), ("tutorial", 60), ("lesson", 50),
    ];

    /// <summary>Words that suggest this is the recording itself rather than something around it.</summary>
    private static readonly (string Marker, int Bonus)[] Qualifiers =
    [
        ("official audio", 12), ("official video", 6), ("official music video", 6),
        ("audio", 4), ("full album", -0), ("topic", 10),
    ];

    /// <summary>
    /// Looks for one recording. Returns the candidates it found, best first, or an empty list.
    /// </summary>
    /// <param name="searchLossless">
    /// Whether to ask Bandcamp and the Archive at all. A caller matching a whole release turns
    /// this off once the first few tracks have established that the release is not on either,
    /// which saves the rest of the album a search apiece that would find nothing.
    /// </param>
    public async Task<IReadOnlyList<TrackMatch>> FindAsync(
        CatalogTrack track,
        ToolPaths tools,
        AdvancedSettings advanced,
        bool searchLossless = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(tools);

        if (!tools.YtDlp.IsAvailable) return [];

        var query = track.SearchQuery;

        // Sources that may hold the recording without loss are asked first. Where one of them has
        // it, the result is better than anything the fallbacks can offer by a wide margin -- a
        // FLAC against a 128 kbit/s stream -- so a confident lossless match ends the search.
        var losslessMatches = searchLossless
            ? await FindLosslessAsync(track, tools, advanced, cancellationToken).ConfigureAwait(false)
            : [];

        var best = losslessMatches.FirstOrDefault();
        if (best is { IsConfident: true, IsLossless: true })
        {
            _logger.LogInformation(
                "{Query}: lossless on {Source} ({Audio})", query, best.Source, best.AudioDescription);
            return losslessMatches;
        }

        var candidates = new List<TrackMatch>(losslessMatches);

        // YouTube carries essentially every commercial recording, including the artist-attributed
        // "Topic" uploads that are the label's own audio. SoundCloud is searched too because it
        // holds a great deal that YouTube does not. Both are lossy, which is why they come second.
        //
        // One track through the batched path: a caller with a whole album to match should call
        // SearchManyAsync directly, because the saving is in sharing one invocation between them.
        var fallback = await SearchManyAsync([track], tools, advanced, cancellationToken)
            .ConfigureAwait(false);
        if (fallback.TryGetValue(query, out var lossy)) candidates.AddRange(lossy);

        // Lossless wins ties, but never overrides a materially better match: a confident lossy
        // result beats a doubtful lossless one, because the wrong recording in FLAC is still the
        // wrong recording.
        var ranked = candidates
            .OrderByDescending(c => c.Score >= 70 && c.IsLossless)
            .ThenByDescending(c => c.Score)
            .ToList();

        _logger.LogInformation(
            "{Query}: {Count} candidate(s), best {Score} on {Source} ({Audio})",
            query, ranked.Count, ranked.FirstOrDefault()?.Score ?? 0,
            ranked.FirstOrDefault()?.Source ?? "none",
            ranked.FirstOrDefault()?.AudioDescription ?? "-");

        return ranked;
    }

    /// <summary>
    /// Probes the places that may hold this recording without loss.
    /// <para>
    /// Each candidate is opened in full rather than listed flat, because the question here is what
    /// formats it offers, and a flat listing does not say. That costs a request apiece, which is
    /// affordable only because these searches return a handful of candidates rather than dozens.
    /// </para>
    /// </summary>
    /// <summary>
    /// The lossless probe on its own, for a caller that batches the fallback search separately.
    /// </summary>
    public Task<List<TrackMatch>> FindLosslessOnlyAsync(
        CatalogTrack track, ToolPaths tools, AdvancedSettings advanced,
        CancellationToken cancellationToken = default)
        => FindLosslessAsync(track, tools, advanced, cancellationToken);

    private async Task<List<TrackMatch>> FindLosslessAsync(
        CatalogTrack track, ToolPaths tools, AdvancedSettings advanced,
        CancellationToken cancellationToken)
    {
        var matches = new List<TrackMatch>();

        // Only candidates a cheap check has already confirmed reach this point, so the list is
        // usually empty and, when it is not, short. Two is plenty: they are ranked, and each one
        // opened costs six or seven seconds.
        var candidates = await _lossless.FindAsync(track, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates.Take(2))
        {
            var arguments = YtDlpArguments.ForAnalysis(
                candidate.Url, tools.ApplyExecutionPolicy(advanced), tools.JsRuntime.Path);
            var result = await _runner.RunAsync(
                tools.YtDlp.Path!, arguments, captureStandardOutput: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) continue;

            try
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement;

                // An Archive item is a whole show; the wanted recording is one entry inside it.
                foreach (var item in EnumerateItems(root))
                {
                    var title = item.String("title");
                    if (title is null) continue;

                    var seconds = item.Double("duration");
                    var duration = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null;

                    var scored = Score(track, title, item.String("uploader") ?? candidate.Source, duration);
                    if (scored is null) continue;

                    var (isLossless, description) = DescribeBestAudio(item);
                    matches.Add(scored with
                    {
                        Url = item.String("webpage_url") ?? candidate.Url,
                        Source = candidate.Source,
                        IsLossless = isLossless,
                        AudioDescription = description,
                    });
                }
            }
            catch (JsonException)
            {
                // One unreadable candidate is not a failed search.
            }

            // Nothing a later candidate could offer would beat a confident lossless hit, and each
            // one costs a full request. Stop as soon as the answer is good enough.
            if (matches.Any(m => m is { IsConfident: true, IsLossless: true })) break;
        }

        return matches.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>
    /// The entries of a playlist payload, or the single item itself. An Internet Archive show
    /// arrives as a playlist of its tracks; a Bandcamp track arrives on its own.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateItems(JsonElement root)
    {
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray()) yield return entry;
            yield break;
        }
        yield return root;
    }

    /// <summary>
    /// Reads the best audio a source genuinely offers, from its format list.
    /// <para>
    /// Never inferred from the site. Bandcamp answers with FLAC, WAV, ALAC and AIFF for a release
    /// the artist priced at zero, and with a single 128 kbit/s stream for one that is for sale.
    /// Only the formats distinguish them, so only the formats are consulted.
    /// </para>
    /// </summary>
    internal static (bool IsLossless, string Description) DescribeBestAudio(JsonElement item)
    {
        if (!item.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return (false, string.Empty);

        JsonElement? bestLossless = null;
        var bestLosslessRank = int.MaxValue;
        JsonElement? bestLossy = null;
        double bestLossyRate = 0;

        foreach (var format in formats.EnumerateArray())
        {
            var codec = (format.String("acodec") ?? format.String("ext") ?? string.Empty).ToLowerInvariant();
            if (codec is "none" or "") continue;

            var family = codec.Split('.')[0];
            var rank = Array.IndexOf(LosslessCodecs, family);
            if (rank >= 0)
            {
                // Ranked, not first-found: the same audio as FLAC and as WAV is the same audio,
                // and one of them is far smaller.
                if (rank < bestLosslessRank) { bestLosslessRank = rank; bestLossless = format; }
                continue;
            }

            var rate = format.Double("abr") ?? format.Double("tbr") ?? 0;
            if (rate > bestLossyRate) { bestLossyRate = rate; bestLossy = format; }
        }

        if (bestLossless is { } lossless)
        {
            var codec = (lossless.String("acodec") ?? lossless.String("ext") ?? "?").ToUpperInvariant();
            var sampleRate = lossless.Double("asr");
            var detail = sampleRate is > 0
                ? $" {sampleRate.Value / 1000:0.#} kHz"
                : string.Empty;
            return (true, $"{codec}{detail}");
        }

        if (bestLossy is { } lossy)
        {
            var codec = (lossy.String("acodec") ?? lossy.String("ext") ?? "?").Split('.')[0].ToUpperInvariant();
            return (false, bestLossyRate > 0 ? $"{codec} {bestLossyRate:F0} kbit/s" : codec);
        }

        return (false, string.Empty);
    }

    /// <summary>
    /// Searches the fallback sources for several tracks in a single invocation, returning the
    /// candidates for each keyed by its query.
    /// <para>
    /// Batched because the cost is the tool starting, not the searching: one invocation covering
    /// four tracks takes eight seconds where four invocations take forty-eight. Results are
    /// attributed back to their track by the query yt-dlp echoes in <c>playlist_title</c>.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, List<TrackMatch>>> SearchManyAsync(
        IReadOnlyList<CatalogTrack> tracks,
        ToolPaths tools,
        AdvancedSettings advanced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var found = new Dictionary<string, List<TrackMatch>>(StringComparer.Ordinal);
        if (tracks.Count == 0 || !tools.YtDlp.IsAvailable) return found;

        var byQuery = new Dictionary<string, CatalogTrack>(StringComparer.Ordinal);
        var expressions = new List<string>(tracks.Count * 2);

        foreach (var track in tracks)
        {
            var query = track.SearchQuery;
            if (!byQuery.TryAdd(query, track)) continue;

            found[query] = [];
            expressions.Add($"ytsearch5:{query}");
            expressions.Add($"scsearch3:{query}");
        }

        var arguments = YtDlpArguments.ForSearch(
            expressions, tools.ApplyExecutionPolicy(advanced), tools.JsRuntime.Path);
        var result = await _runner.RunAsync(
            tools.YtDlp.Path!, arguments, captureStandardOutput: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogInformation("The batched search returned nothing for {Count} track(s)", tracks.Count);
            return found;
        }

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] != '{') continue;

            try
            {
                using var document = JsonDocument.Parse(text);
                var entry = document.RootElement;

                // The query yt-dlp echoes is what ties this answer to its question.
                var query = entry.String("playlist_title") ?? entry.String("playlist");
                if (query is null || !byQuery.TryGetValue(query, out var track)) continue;

                var url = entry.String("url") ?? entry.String("webpage_url");
                var title = entry.String("title");
                if (url is null || title is null) continue;

                var seconds = entry.Double("duration");
                var duration = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null;
                var uploader = entry.String("uploader") ?? entry.String("channel");

                var scored = Score(track, title, uploader, duration);
                if (scored is null) continue;

                // A flat listing carries no formats, so nothing is claimed about the audio here.
                // Both of these sources are lossy in any case; the figure appears once the
                // download resolves its formats.
                found[query].Add(scored with
                {
                    Url = url,
                    Source = url.Contains("soundcloud", StringComparison.OrdinalIgnoreCase)
                        ? "SoundCloud"
                        : "YouTube",
                });
            }
            catch (JsonException)
            {
                // A malformed line is one lost candidate, not a failed search.
            }
        }

        foreach (var list in found.Values) list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return found;
    }

    /// <summary>
    /// Scores a candidate against what the catalogue says. Returns null when the candidate is
    /// disqualified outright rather than merely weak.
    /// </summary>
    internal static TrackMatch? Score(
        CatalogTrack track, string title, string? uploader, TimeSpan? duration)
    {
        var reasons = new List<string>();
        var score = 0;

        // --- Duration. The decisive signal, and the only one that catches a preview. ---
        TimeSpan? delta = null;
        if (track.Duration is { } wanted && duration is { } actual)
        {
            delta = actual - wanted;
            var off = Math.Abs(delta.Value.TotalSeconds);

            if (off > DurationTolerance.TotalSeconds)
            {
                // Refused, not merely penalised: a thirty-second preview of the right song by the
                // right artist would otherwise win on every other signal there is.
                return null;
            }

            var closeness = 1.0 - (off / DurationTolerance.TotalSeconds);
            score += (int)Math.Round(50 * closeness);
            reasons.Add($"length off by {off:F0}s");
        }
        else
        {
            // Nothing to check against. The candidate stays, with none of the points that
            // agreement would have earned.
            reasons.Add("length unknown");
        }

        // --- Title ---
        var titleScore = (int)Math.Round(30 * Similarity(track.Title, StripDecoration(title)));
        score += titleScore;
        reasons.Add($"title {titleScore}/30");

        // --- Artist, in the title or as the uploader ---
        var haystack = $"{title} {uploader}".ToLowerInvariant();
        var artistKey = DeezerCatalogResolver.Normalize(track.Artist);
        if (artistKey.Length > 0 && haystack.Contains(artistKey, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reasons.Add("artist named");
        }
        else if (artistKey.Length > 0
                 && Similarity(track.Artist, uploader ?? string.Empty) > 0.7)
        {
            score += 15;
            reasons.Add("uploader matches artist");
        }

        // --- Markers ---
        var lower = title.ToLowerInvariant();
        var wantedTitle = track.Title.ToLowerInvariant();

        foreach (var (marker, penalty) in Disqualifiers)
        {
            // A remix the catalogue itself names is the recording, not a deviation from it.
            if (!lower.Contains(marker, StringComparison.Ordinal)) continue;
            if (wantedTitle.Contains(marker, StringComparison.Ordinal)) continue;

            score -= penalty;
            reasons.Add($"-{penalty} {marker}");
        }

        foreach (var (marker, bonus) in Qualifiers)
        {
            if (bonus == 0) continue;
            if (!haystack.Contains(marker, StringComparison.Ordinal)) continue;
            score += bonus;
            reasons.Add($"+{bonus} {marker}");
        }

        return new TrackMatch
        {
            Url = string.Empty,
            Title = title,
            Uploader = uploader,
            Duration = duration,
            DurationDelta = delta,
            Score = Math.Max(0, score),
            Reasoning = string.Join(", ", reasons),
        };
    }

    /// <summary>Removes the decoration uploads carry so titles can be compared on their words.</summary>
    internal static string StripDecoration(string title)
    {
        var text = title;
        foreach (var noise in new[]
                 {
                     "(official video)", "(official audio)", "(official music video)",
                     "(official)", "[official video]", "[official audio]", "(lyrics)",
                     "[lyrics]", "(audio)", "(hd)", "(4k)", "(remastered)", "(hq)",
                 })
        {
            text = text.Replace(noise, " ", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// How much two titles share, measured on words rather than characters: an upload that adds
    /// "official audio" is the same recording, whereas "Recovery" and "Discovery" are not, and a
    /// character measure gets both of those backwards.
    /// </summary>
    internal static double Similarity(string a, string b)
    {
        var left = DeezerCatalogResolver.Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = DeezerCatalogResolver.Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (left.Length == 0 || right.Length == 0) return 0;

        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        var shared = left.Count(rightSet.Contains);

        // Measured against the wanted title, so extra words in the upload cost nothing.
        return (double)shared / left.Length;
    }
}

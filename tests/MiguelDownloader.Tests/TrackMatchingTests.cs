using MiguelDownloader.Core.Music;
using MiguelDownloader.Engine.Catalog;
using Xunit;

namespace MiguelDownloader.Tests;

/// <summary>
/// Scoring a candidate against what the catalogue says the recording is.
/// <para>
/// The numbers in these tests are real: they come from searching for a Daft Punk track that
/// Deezer states is 226 seconds long, and they include the result that makes duration matter.
/// </para>
/// </summary>
public class TrackMatchingTests
{
    private static CatalogTrack Wanted(string title = "Harder, Better, Faster, Stronger",
                                       string artist = "Daft Punk", int seconds = 226)
        => new()
        {
            Title = title,
            Artist = artist,
            Duration = TimeSpan.FromSeconds(seconds),
        };

    [Fact]
    public void AThirtySecondPreviewIsRefused()
    {
        // Measured: searching SoundCloud returns the artist's own official upload of this track,
        // at thirty seconds, because it is a preview. It wins on artist, on title and on being
        // official. Only its length gives it away, so length has to be able to refuse outright.
        var match = TrackMatcher.Score(
            Wanted(), "Harder, Better, Faster, Stronger", "Daft Punk", TimeSpan.FromSeconds(30));

        Assert.Null(match);
    }

    [Fact]
    public void TheOfficialAudioWins()
    {
        var video = TrackMatcher.Score(
            Wanted(), "Daft Punk - Harder, Better, Faster, Stronger (Official Video)",
            "Daft Punk", TimeSpan.FromSeconds(223));

        var audio = TrackMatcher.Score(
            Wanted(), "Daft Punk - Harder, Better, Faster, Stronger (Official Audio)",
            "Daft Punk", TimeSpan.FromSeconds(225));

        Assert.NotNull(video);
        Assert.NotNull(audio);
        Assert.True(audio!.Score > video!.Score,
            $"audio {audio.Score} should beat video {video.Score}");
        Assert.True(audio.IsConfident);
    }

    [Fact]
    public void AnArtistTopicUploadIsTreatedAsTheRecording()
    {
        // "Topic" channels carry the label's own audio, which is the best thing to find.
        var match = TrackMatcher.Score(
            Wanted(), "Harder, Better, Faster, Stronger", "Daft Punk - Topic",
            TimeSpan.FromSeconds(226));

        Assert.NotNull(match);
        Assert.True(match!.IsConfident);
        Assert.Contains("topic", match.Reasoning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Harder Better Faster Stronger (Live at Coachella)")]
    [InlineData("Harder Better Faster Stronger - Karaoke Version")]
    [InlineData("Harder Better Faster Stronger (sped up)")]
    [InlineData("Harder Better Faster Stronger [Nightcore]")]
    [InlineData("Harder Better Faster Stronger - Piano Cover by Someone")]
    public void SomethingAroundTheRecordingIsNotTheRecording(string title)
    {
        // These all sit at roughly the right length, so only the wording separates them.
        var wrong = TrackMatcher.Score(Wanted(), title, "Someone", TimeSpan.FromSeconds(224));
        var right = TrackMatcher.Score(
            Wanted(), "Daft Punk - Harder, Better, Faster, Stronger (Official Audio)",
            "Daft Punk", TimeSpan.FromSeconds(225));

        Assert.NotNull(right);
        var wrongScore = wrong?.Score ?? 0;
        Assert.True(right!.Score > wrongScore, $"'{title}' scored {wrongScore} against {right.Score}");
    }

    [Fact]
    public void ARemixTheCatalogueItselfNamesIsTheRecording()
    {
        // Penalising "remix" blindly would reject the very thing that was asked for.
        var track = Wanted("One More Time (Remix)", "Daft Punk", 320);
        var match = TrackMatcher.Score(
            track, "Daft Punk - One More Time (Remix)", "Daft Punk", TimeSpan.FromSeconds(320));

        Assert.NotNull(match);
        Assert.DoesNotContain("-", match!.Reasoning.Split(',').FirstOrDefault(r => r.Contains("remix")) ?? "");
        Assert.True(match.IsConfident);
    }

    [Fact]
    public void ACandidateWithoutALengthIsKeptButNotRewarded()
    {
        // Some listings omit the duration. That is missing evidence, not evidence of a mismatch,
        // so the candidate survives without the points agreement would have earned.
        var unknown = TrackMatcher.Score(
            Wanted(), "Daft Punk - Harder, Better, Faster, Stronger", "Daft Punk", null);
        var known = TrackMatcher.Score(
            Wanted(), "Daft Punk - Harder, Better, Faster, Stronger", "Daft Punk",
            TimeSpan.FromSeconds(226));

        Assert.NotNull(unknown);
        Assert.NotNull(known);
        Assert.True(known!.Score > unknown!.Score);
        Assert.Contains("length unknown", unknown.Reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public void AWhollyDifferentSongIsNotConfident()
    {
        var match = TrackMatcher.Score(
            Wanted(), "Around the World", "Daft Punk", TimeSpan.FromSeconds(224));

        // Right artist, right length, wrong song: it may still appear, but never as a conclusion.
        Assert.False(match?.IsConfident ?? false);
    }

    [Fact]
    public void TheSameAudioIsTakenInTheSmallerContainer()
    {
        // Bandcamp offers a free release as FLAC at 39 MB and as WAV at 63 MB: identical sound,
        // and taking the WAV would cost 60% more disk and transfer for nothing.
        var formats = System.Text.Json.JsonDocument.Parse(
            """
            { "formats": [
              { "format_id": "mp3-128", "acodec": "mp3",  "abr": 128 },
              { "format_id": "wav",     "acodec": "wav",  "asr": 44100 },
              { "format_id": "flac",    "acodec": "flac", "asr": 44100 },
              { "format_id": "falac",   "acodec": "alac", "asr": 44100 }
            ] }
            """);

        var (isLossless, description) = MiguelDownloader.Engine.Catalog.TrackMatcher
            .DescribeBestAudio(formats.RootElement);

        Assert.True(isLossless);
        Assert.StartsWith("FLAC", description, StringComparison.Ordinal);
        Assert.Contains("44,1 kHz", description.Replace('.', ','), StringComparison.Ordinal);
    }

    [Fact]
    public void ALossyOnlySourceIsDescribedAsWhatItIs()
    {
        // The wording has to state the bitrate rather than imply quality. A 128 kbit/s stream is
        // not "high quality" and must never be labelled lossless.
        var formats = System.Text.Json.JsonDocument.Parse(
            """
            { "formats": [
              { "format_id": "249", "acodec": "opus", "abr": 48 },
              { "format_id": "251", "acodec": "opus", "abr": 128 }
            ] }
            """);

        var (isLossless, description) = MiguelDownloader.Engine.Catalog.TrackMatcher
            .DescribeBestAudio(formats.RootElement);

        Assert.False(isLossless);
        Assert.Contains("128", description, StringComparison.Ordinal);
        Assert.DoesNotContain("lossless", description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Discovery", "Discovery", 1.0)]
    [InlineData("Harder, Better, Faster, Stronger", "harder better faster stronger", 1.0)]
    [InlineData("Discovery", "Recovery", 0.0)]
    public void TitlesAreComparedOnWordsNotCharacters(string a, string b, double expected)
    {
        // "Recovery" and "Discovery" differ by two characters and are different records; a
        // character measure calls them nearly identical.
        Assert.Equal(expected, TrackMatcher.Similarity(a, b), 2);
    }

    [Fact]
    public void UploadDecorationDoesNotCountAgainstATitle()
    {
        Assert.Equal(
            1.0,
            TrackMatcher.Similarity(
                "Harder, Better, Faster, Stronger",
                TrackMatcher.StripDecoration("Harder, Better, Faster, Stronger (Official Video)")),
            2);
    }

    [Fact]
    public void ExtraWordsInTheUploadCostNothing()
    {
        // The measure is against what was wanted, so an upload that adds the artist and a tag is
        // still a complete match for the title.
        Assert.Equal(
            1.0,
            TrackMatcher.Similarity(
                "One More Time",
                "Daft Punk - One More Time (Official Audio) HD"),
            2);
    }
}

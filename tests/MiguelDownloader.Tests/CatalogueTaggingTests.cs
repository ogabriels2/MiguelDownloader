using System.Diagnostics;
using System.IO;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Engine.Music;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// Proves the catalogue identifiers survive the trip into a real file, in each container that
/// stores them differently.
/// <para>
/// Writing a tag and assuming it landed is not the same as checking. MP3, FLAC and M4A keep these
/// fields in three unrelated places -- ID3v2 frames, Vorbis comments and iTunes atoms -- so each
/// one is written, closed, reopened and read back.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class CatalogueTaggingTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "md-tagging-" + Guid.NewGuid().ToString("N")[..8]);

    private static string FfmpegPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "build", "tools-cache")))
            directory = directory.Parent;

        return directory is null
            ? "ffmpeg"
            : Path.Combine(directory.FullName, "build", "tools-cache", "ffmpeg.exe");
    }

    private string MakeSilentFile(string extension)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"sample{extension}");

        var psi = new ProcessStartInfo(FfmpegPath()) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "2", path,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit(60_000);
        return path;
    }

    private static MusicMetadata FullMetadata() => new()
    {
        Title = "Harder, Better, Faster, Stronger",
        Artist = "Daft Punk",
        AlbumArtist = "Daft Punk",
        Album = "Discovery",
        TrackNumber = 4,
        TrackTotal = 14,
        DiscNumber = 1,
        DiscTotal = 1,
        Year = 2001,
        Genre = "Electronic",
        Copyright = "2001 Daft Life Limited",
        Isrc = "GBDUW0000059",
        Barcode = "724384960650",
        Label = "Daft Life Ltd./ADA France",
        BeatsPerMinute = 123,
        ReplayGainTrackGain = -12.4,
        MusicBrainzRecordingId = "f1a6a40f-78f5-4918-968d-f64363bae94c",
        MusicBrainzReleaseId = "1b8b1b8b-1b8b-4b8b-8b8b-1b8b1b8b1b8b",
        MusicBrainzArtistId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56",
        SourceUrl = "https://www.deezer.com/track/3135556",
    };

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".m4a")]
    // Opus is what a YouTube-sourced track actually arrives as, so it is the container these
    // tags most often have to survive in.
    [InlineData(".opus")]
    public void EveryIdentifierSurvivesIntoTheFile(string extension)
    {
        var path = MakeSilentFile(extension);
        Assert.True(File.Exists(path), $"ffmpeg did not produce {extension}");

        var tagger = new MusicTagger(NullLogger<MusicTagger>.Instance);
        Assert.True(tagger.Apply(path, FullMetadata(), coverImagePath: null));

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;

        _output.WriteLine(
            $"{extension}: ISRC={tag.ISRC} publisher={tag.Publisher} bpm={tag.BeatsPerMinute} " +
            $"mbid={tag.MusicBrainzTrackId}");

        Assert.Equal("Harder, Better, Faster, Stronger", tag.Title);
        Assert.Equal("Daft Punk", tag.FirstPerformer);
        Assert.Equal("Discovery", tag.Album);
        Assert.Equal(4u, tag.Track);
        Assert.Equal(14u, tag.TrackCount);
        Assert.Equal(2001u, tag.Year);

        // The fields that make this a catalogue-quality tag rather than a filename.
        // An Ogg stream hands out a grouped view that forwards some properties to the comment
        // inside it and drops others, so these two are read from the comment itself there.
        var isrc = tag.ISRC ?? XiphField(file, "ISRC");
        var publisher = tag.Publisher ?? XiphField(file, "LABEL");

        Assert.Equal("GBDUW0000059", isrc);
        Assert.Equal("Daft Life Ltd./ADA France", publisher);
        Assert.Equal(123u, tag.BeatsPerMinute);
        Assert.Equal("f1a6a40f-78f5-4918-968d-f64363bae94c", tag.MusicBrainzTrackId);
        Assert.Equal("056e4f3e-d505-4dad-8ec1-d04f521cbb56", tag.MusicBrainzArtistId);
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    public void TheBarcodeAndLoudnessLandInTheFieldEachContainerDefines(string extension)
    {
        var path = MakeSilentFile(extension);
        var tagger = new MusicTagger(NullLogger<MusicTagger>.Instance);
        Assert.True(tagger.Apply(path, FullMetadata(), coverImagePath: null));

        using var file = TagLib.File.Create(path);

        string? barcode = null, gain = null;

        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            barcode = xiph.GetFirstField("BARCODE");
            gain = xiph.GetFirstField("REPLAYGAIN_TRACK_GAIN");
        }
        else if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            barcode = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "BARCODE", false)?.Text.FirstOrDefault();
            gain = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "REPLAYGAIN_TRACK_GAIN", false)?.Text.FirstOrDefault();
        }
        else if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            barcode = apple.GetDashBox("com.apple.iTunes", "BARCODE");
            gain = apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_GAIN");
        }

        _output.WriteLine($"{extension}: barcode={barcode} gain={gain}");
        Assert.Equal("724384960650", barcode);
        Assert.Equal("-12.40 dB", gain);
    }

    [Fact]
    public void AFieldTheCatalogueDidNotSupplyIsNotInvented()
    {
        // The whole point of nullable metadata: a file must not claim a genre, a year or an ISRC
        // that nothing supplied.
        var path = MakeSilentFile(".flac");
        var tagger = new MusicTagger(NullLogger<MusicTagger>.Instance);

        Assert.True(tagger.Apply(
            path,
            new MusicMetadata { Title = "Untitled", Artist = "Someone" },
            coverImagePath: null));

        using var file = TagLib.File.Create(path);
        Assert.Null(file.Tag.ISRC);
        Assert.Null(file.Tag.Publisher);
        Assert.Equal(0u, file.Tag.Year);
        Assert.Equal(0u, file.Tag.BeatsPerMinute);
    }

    private static string? XiphField(TagLib.File file, string name)
        => file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph
            ? xiph.GetFirstField(name)
            : null;

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
        GC.SuppressFinalize(this);
    }
}

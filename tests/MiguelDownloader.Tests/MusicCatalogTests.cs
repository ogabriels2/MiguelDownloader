using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Urls;
using MiguelDownloader.Engine.Catalog;
using Xunit;

namespace MiguelDownloader.Tests;

/// <summary>
/// The catalogue layer: reading what a streaming link names, without fetching audio from it.
/// </summary>
public class MusicCatalogTests
{
    [Theory]
    [InlineData("https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc", MediaProvider.Spotify)]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", MediaProvider.Spotify)]
    [InlineData("https://spotify.link/abc123", MediaProvider.Spotify)]
    [InlineData("https://music.apple.com/us/album/discovery/697194953", MediaProvider.AppleMusic)]
    [InlineData("https://music.apple.com/br/playlist/hits/pl.abc", MediaProvider.AppleMusic)]
    [InlineData("https://www.deezer.com/track/3135556", MediaProvider.Deezer)]
    [InlineData("https://www.deezer.com/us/album/302127", MediaProvider.Deezer)]
    [InlineData("https://tidal.com/browse/album/77646164", MediaProvider.Tidal)]
    [InlineData("https://listen.tidal.com/album/77646164", MediaProvider.Tidal)]
    [InlineData("https://music.amazon.com/albums/B08KZBLTQ2", MediaProvider.AmazonMusic)]
    [InlineData("https://music.amazon.com.br/albums/B08KZBLTQ2", MediaProvider.AmazonMusic)]
    public void RecognisesTheStreamingPlatforms(string url, MediaProvider expected)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.Equal(expected, info.Provider);
        Assert.True(info.IsEncryptedCatalogue,
            "audio from this platform is encrypted, so the link is a catalogue reference");
        Assert.True(info.IsAudioFirst);
    }

    [Fact]
    public void AShopLinkIsNotAMusicLink()
    {
        // Bare amazon.com is a shop, and yt-dlp has working extractors for its product videos.
        // Claiming it as Amazon Music would shadow them.
        Assert.True(MediaUrlParser.TryParse("https://www.amazon.com/dp/B08KZBLTQ2", out var info));
        Assert.NotEqual(MediaProvider.AmazonMusic, info.Provider);
        Assert.False(info.IsEncryptedCatalogue);
    }

    [Fact]
    public void TheseLinksAreStillAccepted()
    {
        // Being undownloadable is not being unusable: the link still identifies a release, and
        // that identity is the whole point of accepting it.
        Assert.True(MediaUrlParser.TryParse("https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc", out var info));
        Assert.True(info.IsDownloadable);
        Assert.True(CatalogService.Handles(info));
    }

    [Fact]
    public void OrdinaryVideoLinksAreNotRoutedToTheCatalogue()
    {
        Assert.True(MediaUrlParser.TryParse("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out var youtube));
        Assert.False(CatalogService.Handles(youtube));

        Assert.True(MediaUrlParser.TryParse("https://soundcloud.com/tycho/tycho-awake", out var soundcloud));
        Assert.False(CatalogService.Handles(soundcloud));
    }

    [Theory]
    [InlineData("https://www.deezer.com/track/3135556", CatalogKind.Track, "3135556")]
    [InlineData("https://www.deezer.com/album/302127", CatalogKind.Album, "302127")]
    [InlineData("https://www.deezer.com/us/album/302127", CatalogKind.Album, "302127")]
    [InlineData("https://www.deezer.com/fr/playlist/908622995", CatalogKind.Playlist, "908622995")]
    [InlineData("https://www.deezer.com/en/artist/27", CatalogKind.Artist, "27")]
    public void ReadsWhatADeezerPathPointsAt(string url, CatalogKind kind, string id)
    {
        var (actualKind, actualId) = DeezerCatalogResolver.IdentifyPath(url);
        Assert.Equal(kind, actualKind);
        Assert.Equal(id, actualId);
    }

    [Theory]
    [InlineData("https://music.apple.com/us/album/discovery/697194953", CatalogKind.Album, "697194953", null)]
    [InlineData("https://music.apple.com/us/album/discovery/697194953?i=697194954", CatalogKind.Album, "697194953", "697194954")]
    [InlineData("https://music.apple.com/us/artist/daft-punk/5468295", CatalogKind.Artist, "5468295", null)]
    public void ReadsWhatAnAppleMusicPathPointsAt(
        string url, CatalogKind kind, string id, string? trackId)
    {
        var (actualKind, actualId, actualTrack) = AppleMusicCatalogResolver.IdentifyPath(url);
        Assert.Equal(kind, actualKind);
        Assert.Equal(id, actualId);
        Assert.Equal(trackId, actualTrack);
    }

    [Fact]
    public void AnEditorialPlaylistHasNoNumericId()
    {
        // Apple's curated playlists use "pl.xxxx", which the lookup service does not answer for.
        // Reporting no id is what sends the link down the bridge instead of failing outright.
        var (_, id, _) = AppleMusicCatalogResolver.IdentifyPath(
            "https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb");
        Assert.Null(id);
    }

    [Theory]
    [InlineData("https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc",
                "https://open.spotify.com/embed/album/2noRn2Aes5aoNVsU6iWThc")]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT",
                "https://open.spotify.com/embed/track/4cOdK2wGLETKBW3PvgPWqT")]
    public void FindsTheEmbedThatCarriesTheArtist(string url, string expected)
    {
        Assert.Equal(expected, PublicPageReader.ToEmbedUrl(url));
    }

    [Theory]
    [InlineData("Discovery by Daft Punk on Apple Music", "Discovery", "Daft Punk")]
    [InlineData("Daft Punk - Discovery", "Discovery", "Daft Punk")]
    [InlineData("One More Time by Daft Punk", "One More Time", "Daft Punk")]
    public void PullsTheArtistOutOfTheOnlyStringThesePagesGive(
        string title, string expectedTitle, string expectedArtist)
    {
        var (actualTitle, actualArtist) = PublicPageReader.SplitTitleAndArtist(title, null);
        Assert.Equal(expectedTitle, actualTitle);
        Assert.Equal(expectedArtist, actualArtist);
    }

    [Theory]
    [InlineData("TIDAL - High Fidelity Music Streaming")]
    [InlineData("Amazon Music")]
    [InlineData("Spotify")]
    public void APageServingItsOwnNameHasSaidNothing(string title)
    {
        // Measured: TIDAL answers with its own tagline for anything it has not rendered. Treating
        // that as the release name would send the lookup after an album called "TIDAL".
        Assert.True(PublicPageReader.LooksLikeSiteName(title));
    }

    [Theory]
    [InlineData("Discovery", "Discovery", true)]
    [InlineData("Discovery", "Discovery (Deluxe Edition)", true)]
    [InlineData("Discovery", "Disc-Overy", false)]
    [InlineData("Discovery", "Recovery", false)]
    [InlineData("Random Access Memories", "random access memories", true)]
    public void ComparesTitlesLooselyButNotCarelessly(string wanted, string candidate, bool agree)
    {
        Assert.Equal(agree, DeezerCatalogResolver.TitlesAgree(wanted, candidate));
    }

    [Fact]
    public void AskingAppleForABiggerCoverIsASubstitution_NotAnUpscale()
    {
        // The 1400px file genuinely exists on Apple's servers; the 100px name is just what the
        // lookup service returns. Nothing is being enlarged.
        Assert.Equal(
            "https://is1-ssl.mzstatic.com/image/thumb/Music/x/y/z.jpg/1400x1400bb.jpg",
            AppleMusicCatalogResolver.UpscaleArtwork(
                "https://is1-ssl.mzstatic.com/image/thumb/Music/x/y/z.jpg/100x100bb.jpg"));
    }

    [Fact]
    public void ACatalogueTrackCarriesItsIdentifiersIntoTheFileTags()
    {
        var track = new CatalogTrack
        {
            Title = "Harder, Better, Faster, Stronger",
            Artist = "Daft Punk",
            Album = "Discovery",
            TrackNumber = 4,
            TrackTotal = 14,
            Isrc = "GBDUW0000059",
            Barcode = "724384960650",
            Label = "Daft Life Ltd./ADA France",
            ReleaseDate = "2001-03-07",
            BeatsPerMinute = 123.4,
            MusicBrainzRecordingId = "f1a6a40f-78f5-4918-968d-f64363bae94c",
        };

        var tags = track.ToMetadata();

        Assert.Equal("GBDUW0000059", tags.Isrc);
        Assert.Equal("724384960650", tags.Barcode);
        Assert.Equal("Daft Life Ltd./ADA France", tags.Label);
        Assert.Equal(2001, tags.Year);
        Assert.Equal(123.4, tags.BeatsPerMinute);
        Assert.Equal("f1a6a40f-78f5-4918-968d-f64363bae94c", tags.MusicBrainzRecordingId);
        Assert.Equal("Daft Punk", tags.AlbumArtist);
    }

    [Fact]
    public void AnUnmeasuredBpmIsNotZeroBpm()
    {
        // Deezer reports 0 for tracks it has not analysed. Writing that into a file would state
        // that the track has no tempo, which is a claim about the music rather than about the data.
        var track = new CatalogTrack { Title = "x", Artist = "y", BeatsPerMinute = 0 };
        Assert.Null(track.ToMetadata().BeatsPerMinute);
    }

    [Fact]
    public void AShortListIsNotPresentedAsAWholeAlbum()
    {
        var partial = new MusicCatalog
        {
            Kind = CatalogKind.Playlist,
            Title = "Big playlist",
            Platform = MusicPlatform.Deezer,
            DeclaredCount = 500,
            Tracks = [new CatalogTrack { Title = "a", Artist = "b" }],
        };

        Assert.False(partial.IsComplete);
    }

    [Theory]
    [InlineData(MusicPlatform.Deezer, CatalogReach.Full)]
    [InlineData(MusicPlatform.AppleMusic, CatalogReach.Full)]
    [InlineData(MusicPlatform.Spotify, CatalogReach.Basic)]
    [InlineData(MusicPlatform.Tidal, CatalogReach.Basic)]
    [InlineData(MusicPlatform.AmazonMusic, CatalogReach.Basic)]
    public void RecordsHowMuchEachPlatformPublishes(MusicPlatform platform, CatalogReach expected)
    {
        Assert.Equal(expected, MusicPlatformInfo.Reach(platform));
        Assert.Equal(expected == CatalogReach.Basic, MusicPlatformInfo.NeedsBridge(platform));
    }
}

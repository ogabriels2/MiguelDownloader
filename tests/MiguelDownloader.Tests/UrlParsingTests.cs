using MiguelDownloader.Core.Urls;
using Xunit;

namespace MiguelDownloader.Tests;

public class UrlParsingTests
{
    [Theory]
    // Standard watch URLs
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    // Short host
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", "dQw4w9WgXcQ")]
    // Embeds and the legacy forms
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/v/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    // Extra query parameters must not confuse the id
    [InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ&ab_channel=X", "dQw4w9WgXcQ")]
    // Missing scheme
    [InlineData("youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void ParsesVideoUrls(string url, string expectedId)
    {
        Assert.True(YouTubeUrlReader.TryParse(url, out var info));
        Assert.Equal(MediaUrlKind.Video, info.Kind);
        Assert.Equal(expectedId, info.VideoId);
    }

    [Fact]
    public void RecognisesShorts()
    {
        Assert.True(YouTubeUrlReader.TryParse("https://www.youtube.com/shorts/dQw4w9WgXcQ", out var info));
        Assert.Equal(MediaUrlKind.Short, info.Kind);
        Assert.Equal("dQw4w9WgXcQ", info.VideoId);
    }

    [Fact]
    public void RecognisesMusicDomain()
    {
        Assert.True(YouTubeUrlReader.TryParse("https://music.youtube.com/watch?v=dQw4w9WgXcQ", out var info));
        Assert.True(info.IsMusicDomain);
        Assert.Equal("dQw4w9WgXcQ", info.VideoId);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9c9r", MediaUrlKind.Playlist)]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_ktoOAT8UPQhUFuBTHgHt1234567890", MediaUrlKind.Album)]
    [InlineData("https://www.youtube.com/playlist?list=RDdQw4w9WgXcQabc", MediaUrlKind.Mix)]
    public void ClassifiesPlaylistsByIdPrefix(string url, MediaUrlKind expected)
    {
        Assert.True(YouTubeUrlReader.TryParse(url, out var info));
        Assert.Equal(expected, info.Kind);
    }

    [Fact]
    public void VideoWithPlaylistContextStaysAVideo()
    {
        // Analysing one video from a playlist must not expand into the whole playlist.
        const string url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmRdnEQy6nuLM&index=3";

        Assert.True(YouTubeUrlReader.TryParse(url, out var info));
        Assert.Equal(MediaUrlKind.Video, info.Kind);
        Assert.True(info.HasPlaylistContext);
        Assert.Equal(3, info.PlaylistIndex);
        // The canonical URL is what gets sent to the extractor, and it carries no list.
        Assert.DoesNotContain("list=", info.CanonicalUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWithoutVideoIdIsAPlaylist()
    {
        Assert.True(YouTubeUrlReader.TryParse(
            "https://www.youtube.com/watch?list=PLrAXtmRdnEQy6nuLM", out var info));
        Assert.Equal(MediaUrlKind.Playlist, info.Kind);
    }

    [Theory]
    [InlineData("https://www.youtube.com/@Blender", "Blender", ChannelTab.Default)]
    [InlineData("https://www.youtube.com/@Blender/videos", "Blender", ChannelTab.Videos)]
    [InlineData("https://www.youtube.com/@Blender/shorts", "Blender", ChannelTab.Shorts)]
    [InlineData("https://www.youtube.com/@Blender/streams", "Blender", ChannelTab.Streams)]
    public void ParsesHandleChannels(string url, string handle, ChannelTab tab)
    {
        Assert.True(YouTubeUrlReader.TryParse(url, out var info));
        Assert.Equal(MediaUrlKind.Channel, info.Kind);
        Assert.Equal(handle, info.Handle);
        Assert.Equal(tab, info.Tab);
    }

    [Fact]
    public void ParsesChannelIdUrls()
    {
        Assert.True(YouTubeUrlReader.TryParse(
            "https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg/videos", out var info));
        Assert.Equal(MediaUrlKind.Channel, info.Kind);
        Assert.Equal("UCSMOQeBJ2RAnuFungnQOxLg", info.ChannelId);
        Assert.Equal(ChannelTab.Videos, info.Tab);
    }

    [Fact]
    public void ChannelIdWithoutTabHasNoTab()
    {
        // The id segment must not be mistaken for a tab name.
        Assert.True(YouTubeUrlReader.TryParse(
            "https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg", out var info));
        Assert.Equal(ChannelTab.Default, info.Tab);
        Assert.Equal("UCSMOQeBJ2RAnuFungnQOxLg", info.ChannelId);
    }

    [Fact]
    public void SearchUrlsAreRecognisedButNotDownloadable()
    {
        Assert.True(YouTubeUrlReader.TryParse(
            "https://www.youtube.com/results?search_query=blender", out var info));
        Assert.Equal(MediaUrlKind.Search, info.Kind);
        Assert.False(info.IsDownloadable);
        Assert.Equal("blender", info.SearchQuery);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://www.youtube.com/watch?v=tooshort")]
    [InlineData("https://www.youtube.com/watch?v=waaaaaaaaaaytoolong")]
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/")]
    public void RejectsInvalidInput(string? url)
    {
        Assert.False(YouTubeUrlReader.TryParse(url, out _));
    }

    [Fact]
    public void RejectsLookalikeHost()
    {
        // A host that merely contains "youtube.com" must not be accepted.
        Assert.False(YouTubeUrlReader.TryParse("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ", out _));
    }

    [Theory]
    [InlineData("?t=90", 90)]
    [InlineData("?t=90s", 90)]
    [InlineData("?t=1h2m3s", 3723)]
    [InlineData("?t=2m", 120)]
    [InlineData("?start=45", 45)]
    public void ParsesStartTime(string query, int expectedSeconds)
    {
        Assert.True(YouTubeUrlReader.TryParse($"https://youtu.be/dQw4w9WgXcQ{query}", out var info));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), info.StartAt);
    }

    [Fact]
    public void ExtractsUrlFromSurroundingText()
    {
        const string clipboard = "olha isso https://youtu.be/dQw4w9WgXcQ que legal";
        var extracted = YouTubeUrlReader.ExtractFirstUrl(clipboard);

        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", extracted);
        Assert.True(YouTubeUrlReader.LooksLikeYouTubeUrl(clipboard));
    }

    [Fact]
    public void StripsTrailingPunctuationFromExtractedUrl()
    {
        var extracted = YouTubeUrlReader.ExtractFirstUrl("veja (https://youtu.be/dQw4w9WgXcQ).");
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", extracted);
    }

    [Fact]
    public void BulkUrlsAreFlaggedForConfirmation()
    {
        Assert.True(YouTubeUrlReader.TryParse("https://www.youtube.com/@Blender/videos", out var channel));
        Assert.True(channel.IsBulk);

        Assert.True(YouTubeUrlReader.TryParse("https://youtu.be/dQw4w9WgXcQ", out var video));
        Assert.False(video.IsBulk);
    }
}

using MiguelDownloader.Core.Errors;
using MiguelDownloader.Core.Urls;
using Xunit;

namespace MiguelDownloader.Tests;

/// <summary>
/// The failure messages the other networks actually produce.
/// <para>
/// Every string here was copied from a real run of the yt-dlp build this app ships, not written
/// from memory. A misclassified failure sends the user after the wrong fix, which is worse than
/// showing the raw output: an IP block reported as a regional restriction has them looking for a
/// VPN when waiting would have worked.
/// </para>
/// </summary>
public class ProviderErrorTests
{
    [Theory]
    [InlineData(
        "ERROR: [TikTok] 7107337212743830830: Your IP address is blocked from accessing this post",
        DownloadErrorKind.IpBlocked)]
    [InlineData(
        "ERROR: [facebook] 3676516585958356: Cannot parse data; please report this issue on " +
        "https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template.",
        DownloadErrorKind.ToolOutdated)]
    [InlineData(
        "ERROR: [vimeo] 76979871: The web client only works when logged-in. Use --cookies, " +
        "--cookies-from-browser, --username and --password, --netrc-cmd, or --netrc (vimeo)",
        DownloadErrorKind.LoginRequired)]
    [InlineData("ERROR: Unsupported URL: https://example.invalid/watch/1", DownloadErrorKind.UnsupportedUrl)]
    public void ClassifiesWhatTheseSitesActuallySay(string output, DownloadErrorKind expected)
    {
        Assert.Equal(expected, ErrorClassifier.Classify(output).Kind);
    }

    [Fact]
    public void AMalformedRequestIsNotDressedUpAsAConnectionProblem()
    {
        // Measured on Bluesky. A 400 says the request was wrong, not that the network was: telling
        // the user to check their connection would send them after a fault that is not there. With
        // nothing better to offer, the raw output is the honest answer.
        var error = ErrorClassifier.Classify(
            "ERROR: [Bluesky] 3l3vgf77zio2a: Unable to download JSON metadata: HTTP Error 400: Bad Request");

        Assert.Equal(DownloadErrorKind.Unknown, error.Kind);
        Assert.Contains("400", error.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIpBlockIsNotReportedAsARegionalRestriction()
    {
        var error = ErrorClassifier.Classify(
            "ERROR: [TikTok] 123: Your IP address is blocked from accessing this post");

        Assert.NotEqual(DownloadErrorKind.GeoRestricted, error.Kind);
        // Waiting is the fix that works; there is nothing about the item to change.
        Assert.Equal(RecommendedAction.RetryLater, error.Action);
    }

    [Fact]
    public void AMuxedStreamIsNeverPairedWithASeparateAudioTrack()
    {
        // Regression: TikTok publishes only muxed video plus one loose audio track. Taking both
        // asks ffmpeg to lay a second soundtrack over one that is already in the file, and the
        // download fails outright. The test is on the chosen stream, not on what the source offers.
        var resolved = Core.Formats.FormatSelector.ResolveVideo(
            TestMedia.MuxedWithSeparateAudioTrack(),
            new Core.Formats.FormatSelection());

        Assert.NotNull(resolved.Video);
        Assert.True(resolved.Video!.IsMuxed);
        Assert.Empty(resolved.Audio);
        Assert.True(resolved.UsesMuxedSource);
    }

    [Fact]
    public void AVideoDownloadNeverComesOutSilent()
    {
        // Regression: X names no codec for its audio renditions, so judging a format by its codec
        // string alone discarded them as carrying nothing. The selector then had no audio to pair
        // and every download from X arrived as a silent video that ffprobe still called valid.
        var resolved = Core.Formats.FormatSelector.ResolveVideo(
            TestMedia.UnreportedCodecs(), new Core.Formats.FormatSelection());

        Assert.NotNull(resolved.Video);
        Assert.True(resolved.Video!.HasVideo);
        Assert.False(resolved.UsesMuxedSource);
        Assert.NotEmpty(resolved.Audio);
        Assert.Equal("hls-audio-64000-Audio", resolved.Audio[0].FormatId);
    }

    [Fact]
    public void ADescribedFormatBeatsAnUndescribedOneOfTheSameSize()
    {
        // http-832 is the higher bitrate at 360p, but nothing says whether it carries sound.
        // Pairing it with a separate track would give two soundtracks; taking it alone might give
        // none. hls-348 states its contents, so it is the one that can be handled correctly --
        // which is also what yt-dlp itself picks for this post.
        var resolved = Core.Formats.FormatSelector.ResolveVideo(
            TestMedia.UnreportedCodecs(), new Core.Formats.FormatSelection());

        Assert.Equal("hls-348", resolved.Video!.FormatId);
    }

    [Fact]
    public void AnUnnamedCodecIsUnknown_NotAbsent()
    {
        // The distinction decides whether audio is copied or re-encoded. Read as absent, a
        // container check concludes the audio cannot be carried and ffmpeg re-encodes AAC that
        // was perfectly fine -- which is how X downloads ended up with a Vorbis soundtrack.
        var payload = System.Text.Json.JsonDocument.Parse(
            """
            {
              "id": "1001551417340022785",
              "title": "Post with video",
              "webpage_url": "https://twitter.com/x/status/1",
              "formats": [
                { "format_id": "hls-audio-64000-Audio", "ext": "mp4", "vcodec": "none", "abr": 64 },
                { "format_id": "http-832", "ext": "mp4", "width": 480, "height": 360, "tbr": 832 }
              ]
            }
            """);

        var item = MiguelDownloader.Engine.YtDlp.InfoMapper.MapItem(payload.RootElement);

        var audio = item.Formats.Single(f => f.FormatId == "hls-audio-64000-Audio");
        Assert.Equal(Core.Models.AudioCodec.Unknown, audio.AudioCodec);
        Assert.True(audio.IsAudioOnly);

        var progressive = item.Formats.Single(f => f.FormatId == "http-832");
        Assert.Equal(Core.Models.VideoCodec.Unknown, progressive.VideoCodec);
        Assert.True(progressive.HasVideo);
    }

    [Fact]
    public void AudioOfUnknownCodecIsCopied_NotReEncoded()
    {
        // Matroska carries anything, so an audio track whose codec was never named still only
        // needs remuxing. Deciding otherwise costs quality for no reason at all.
        var item = TestMedia.UnreportedCodecs();
        var video = item.Formats.Single(f => f.FormatId == "hls-348");
        var audio = item.Formats.Single(f => f.FormatId == "hls-audio-64000-Audio")
            with { AudioCodec = Core.Models.AudioCodec.Unknown };

        var plan = Core.Formats.MuxPlanner.Plan(video, [audio], Core.Formats.ContainerFormat.Mkv);

        Assert.Equal(Core.Formats.StreamAction.Copy, plan.Audio);
    }

    [Fact]
    public void AReportedFrameSizeIsProofOfVideo()
    {
        // The progressive renditions must not be discarded outright either: they are real
        // downloadable formats, just undescribed ones.
        var progressive = TestMedia.UnreportedCodecs().Formats.First(f => f.FormatId == "http-832");

        Assert.True(progressive.HasVideo);
        Assert.False(progressive.IsStoryboard);
        Assert.False(progressive.HasKnownComposition);
    }

    [Fact]
    public void SeparateStreamsAreStillPairedWhenTheSourceOffersThem()
    {
        // The fix must not cost YouTube its separate video and audio streams.
        var resolved = Core.Formats.FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(), new Core.Formats.FormatSelection());

        Assert.NotNull(resolved.Video);
        Assert.False(resolved.Video!.IsMuxed);
        Assert.Single(resolved.Audio);
        Assert.False(resolved.UsesMuxedSource);
    }

    [Fact]
    public void AudioOnlyDownloadsStillFindTheLooseTrack()
    {
        // The muxed rule applies to video downloads. Asking for audio alone must still reach the
        // standalone track rather than extracting it back out of a video.
        var resolved = Core.Formats.FormatSelector.ResolveAudioOnly(
            TestMedia.MuxedWithSeparateAudioTrack(), new Core.Formats.FormatSelection());

        Assert.Single(resolved.Audio);
        Assert.Equal("audio", resolved.Audio[0].FormatId);
    }

    [Fact]
    public void ALongSocialTitleStillFitsTheWorkingPath()
    {
        // A real Facebook reel title: the view count, the reaction count, the whole caption and
        // the page name. YouTube titles are a fraction of this, which is why the working-path
        // overflow stayed hidden until the app was pointed at another site.
        const string RealTitle =
            "9.8K views 343 reactions When your trying to help your partner out with an arrest " +
            "and #FAAFO games begin. Let the Slapathon commence!! Beast Camp Training";

        const string Destination = @"C:\Users\gabal\Downloads\Miguel Downloader";

        // The file is built in the working directory, not the destination, and yt-dlp appends
        // ".f<format-id>" to every part while it works.
        const string WorkingDirectory = @"C:\Users\gabal\AppData\Local\MiguelDownloader\work";
        var reserve = Math.Max(0, WorkingDirectory.Length + 33 - Destination.Length) + 24;

        var fitted = Core.Naming.FileNameSanitizer.EnsurePathFits(
            Destination, RealTitle + ".mp4", reserve: reserve);

        var workingPath = $@"{WorkingDirectory}\{new string('0', 32)}\" +
                          Path.GetFileNameWithoutExtension(fitted) + ".f1360826452158385v.mp4";

        Assert.True(workingPath.Length < 260,
            $"the working path is {workingPath.Length} characters, which Windows will refuse");
        Assert.EndsWith(".mp4", fitted, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortTitlesAreLeftAlone()
    {
        // The reserve must not start truncating names that were never at risk.
        var fitted = Core.Naming.FileNameSanitizer.EnsurePathFits(
            @"C:\Users\gabal\Downloads\Miguel Downloader", "Me at the zoo.mp4", reserve: 80);

        Assert.Equal("Me at the zoo.mp4", fitted);
    }

    [Fact]
    public void ASignInWallSuggestsSigningIn()
    {
        var error = ErrorClassifier.Classify(
            "ERROR: [Instagram] ABC: Requested content is not available, rate-limit reached or login required");

        Assert.Equal(RecommendedAction.SignIn, error.Action);
    }
}

/// <summary>
/// Covers the front door that every link now goes through, for the sites beyond YouTube.
/// <para>
/// The URLs here are real shapes taken from each site, several of them from yt-dlp's own test
/// suite, so a change that quietly stops recognising a site fails here rather than in front of
/// the user.
/// </para>
/// </summary>
public class MultiProviderUrlTests
{
    [Theory]
    [InlineData("https://www.tiktok.com/@tatemcrae/video/7107337212743830830", MediaProvider.TikTok)]
    [InlineData("https://vm.tiktok.com/ZMhvHNNKA/", MediaProvider.TikTok)]
    [InlineData("https://www.instagram.com/reel/C0nXyZaBcDe/", MediaProvider.Instagram)]
    [InlineData("https://www.instagram.com/p/C0nXyZaBcDe/", MediaProvider.Instagram)]
    [InlineData("https://x.com/NASA/status/1698723458267050328", MediaProvider.Twitter)]
    [InlineData("https://twitter.com/LisPower1/status/1001551623938805763", MediaProvider.Twitter)]
    [InlineData("https://www.facebook.com/reel/1195289147628387", MediaProvider.Facebook)]
    [InlineData("https://fb.watch/abcdefghij/", MediaProvider.Facebook)]
    [InlineData("https://www.reddit.com/r/videos/comments/6rrwyj/title/", MediaProvider.Reddit)]
    [InlineData("https://vimeo.com/76979871", MediaProvider.Vimeo)]
    [InlineData("https://soundcloud.com/tycho/tycho-awake", MediaProvider.SoundCloud)]
    [InlineData("https://bsky.app/profile/bsky.app/post/3l3vgf77zio2a", MediaProvider.Bluesky)]
    [InlineData("https://www.twitch.tv/videos/1234567890", MediaProvider.Twitch)]
    [InlineData("https://www.dailymotion.com/video/x2iuewm", MediaProvider.Dailymotion)]
    [InlineData("https://www.pinterest.com/pin/1234567890/", MediaProvider.Pinterest)]
    [InlineData("https://kick.com/video/abc-123", MediaProvider.Kick)]
    public void IdentifiesTheProvider(string url, MediaProvider expected)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.Equal(expected, info.Provider);
        Assert.True(info.IsDownloadable);
    }

    [Fact]
    public void MusicDomainIsItsOwnProvider()
    {
        Assert.True(MediaUrlParser.TryParse("https://music.youtube.com/watch?v=dQw4w9WgXcQ", out var info));
        Assert.Equal(MediaProvider.YouTubeMusic, info.Provider);
        Assert.True(info.IsAudioFirst);
        // The longest host match must win, or music.youtube.com resolves as plain YouTube.
        Assert.Equal("YouTube Music", info.ProviderName);
    }

    [Fact]
    public void YouTubeKeepsItsDetailedParsing()
    {
        Assert.True(MediaUrlParser.TryParse("https://youtu.be/dQw4w9WgXcQ?t=42", out var info));
        Assert.Equal(MediaProvider.YouTube, info.Provider);
        Assert.Equal("dQw4w9WgXcQ", info.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(42), info.StartAt);
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@nasa", "nasa")]
    [InlineData("https://www.tiktok.com/@tatemcrae/video/7107337212743830830", "tatemcrae")]
    public void ReadsTheHandle(string url, string expected)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.Equal(expected, info.Handle);
    }

    [Theory]
    // A person's page expands to everything they have posted, so it must be flagged as bulk.
    [InlineData("https://www.tiktok.com/@nasa")]
    [InlineData("https://soundcloud.com/tycho/sets/awake")]
    [InlineData("https://www.reddit.com/r/videos/")]
    public void CollectionsAreFlaggedAsBulk(string url)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.True(info.IsBulk, $"{url} should be treated as a bulk expansion");
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@tatemcrae/video/7107337212743830830")]
    [InlineData("https://x.com/NASA/status/1698723458267050328")]
    [InlineData("https://www.instagram.com/reel/C0nXyZaBcDe/")]
    public void SingleItemsAreNotBulk(string url)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.False(info.IsBulk);
    }

    [Fact]
    public void AudioFirstSitesOpenOnTheMusicFlow()
    {
        Assert.True(MediaUrlParser.TryParse("https://soundcloud.com/tycho/tycho-awake", out var soundcloud));
        Assert.True(soundcloud.IsAudioFirst);

        Assert.True(MediaUrlParser.TryParse("https://artist.bandcamp.com/track/song", out var bandcamp));
        Assert.True(bandcamp.IsAudioFirst);

        Assert.True(MediaUrlParser.TryParse("https://www.tiktok.com/@a/video/1", out var tiktok));
        Assert.False(tiktok.IsAudioFirst);
    }

    [Theory]
    // Measured: both refuse anonymous requests often enough to be worth warning about up front.
    [InlineData("https://www.instagram.com/reel/C0nXyZaBcDe/")]
    [InlineData("https://vimeo.com/76979871")]
    public void SitesThatUsuallyWantASessionSaySoBeforeTheAttempt(string url)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.True(info.SignInLikely);
    }

    [Theory]
    [InlineData("https://www.tiktok.com/@a/video/1")]
    [InlineData("https://www.reddit.com/r/videos/comments/6rrwyj/title/")]
    public void SitesThatUsuallyWorkAnonymouslyDoNotNagAboutSignIn(string url)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.False(info.SignInLikely);
    }

    [Fact]
    public void UnknownSitesAreStillAttempted()
    {
        // yt-dlp ships over 1700 extractors and gains more between our releases. Refusing a host
        // this file has not heard of would turn working links into "unsupported".
        Assert.True(MediaUrlParser.TryParse("https://some-video-site.example/watch/12345", out var info));
        Assert.Equal(MediaProvider.Generic, info.Provider);
        Assert.True(info.IsDownloadable);
        Assert.Equal(string.Empty, info.ProviderName);
    }

    [Fact]
    public void AnUnknownPathOnYouTubeIsStillRejected()
    {
        // The asymmetry is deliberate: YouTube's URL shapes are finite and known, so an
        // unrecognised one is genuinely wrong rather than merely unfamiliar.
        Assert.False(MediaUrlParser.TryParse("https://www.youtube.com/feed/subscriptions", out var info)
                     && info.IsDownloadable);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com/file.mp4")]
    [InlineData("file:///C:/local.mp4")]
    public void RejectsWhatIsNotALink(string input)
    {
        Assert.False(MediaUrlParser.TryParse(input, out _));
    }

    [Theory]
    // A share id identifies who sent the link, not what it points at. Keeping it would queue the
    // same item twice under two spellings and put the sender's id in the history.
    [InlineData("https://www.instagram.com/reel/C0nXyZaBcDe/?igsh=SOMETHING",
                "https://www.instagram.com/reel/C0nXyZaBcDe/")]
    [InlineData("https://x.com/NASA/status/1698723458267050328?s=20&t=abcd",
                "https://x.com/NASA/status/1698723458267050328")]
    [InlineData("https://www.tiktok.com/@a/video/1?_r=1&_t=8abc&utm_source=x",
                "https://www.tiktok.com/@a/video/1")]
    public void StripsTrackingParameters(string url, string expected)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.Equal(expected, info.CanonicalUrl);
    }

    [Fact]
    public void KeepsQueryParametersItDoesNotRecognise()
    {
        // Dropping a parameter that mattered breaks the link, which is far worse than a tidy URL.
        Assert.True(MediaUrlParser.TryParse("https://example.com/watch?id=42&quality=hd", out var info));
        Assert.Contains("id=42", info.CanonicalUrl);
        Assert.Contains("quality=hd", info.CanonicalUrl);
    }

    [Theory]
    [InlineData("olha isso https://www.tiktok.com/@a/video/1 muito bom", "https://www.tiktok.com/@a/video/1")]
    [InlineData("veja (https://x.com/NASA/status/123).", "https://x.com/NASA/status/123")]
    [InlineData("instagram.com/reel/C0nXyZaBcDe/", "https://instagram.com/reel/C0nXyZaBcDe/")]
    public void PullsTheLinkOutOfPastedText(string clipboard, string expected)
    {
        Assert.Equal(expected, MediaUrlParser.ExtractFirstUrl(clipboard));
        Assert.True(MediaUrlParser.LooksLikeMediaUrl(clipboard));
    }

    [Fact]
    public void ABareHostIsNotALinkToAnything()
    {
        // "tiktok.com" on its own is a home page; treating it as a download target would send the
        // extractor after the whole site.
        Assert.Null(MediaUrlParser.ExtractFirstUrl("tiktok.com"));
    }

    [Theory]
    [InlineData("https://www.instagram.com/reel/X/", MediaUrlKind.Short)]
    [InlineData("https://www.facebook.com/reel/1195289147628387", MediaUrlKind.Short)]
    [InlineData("https://www.tiktok.com/@a/video/1", MediaUrlKind.Short)]
    [InlineData("https://x.com/NASA/status/123", MediaUrlKind.Post)]
    [InlineData("https://www.reddit.com/r/v/comments/abc/title/", MediaUrlKind.Post)]
    [InlineData("https://www.instagram.com/p/C0nXyZaBcDe/", MediaUrlKind.Post)]
    public void ClassifiesTheItemShape(string url, MediaUrlKind expected)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info));
        Assert.Equal(expected, info.Kind);
    }
}

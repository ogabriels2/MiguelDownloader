using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Presets;
using Xunit;

namespace MiguelDownloader.Tests;

public class FormatSelectorTests
{
    [Fact]
    public void BestTierPicksTheHighestResolutionAvailable()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, FormatSelection.ForTier(QualityTier.Best));

        Assert.NotNull(resolved.Video);
        Assert.Equal(2160, resolved.Video!.Height);
    }

    [Fact]
    public void HighTierCapsAt1080p()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, FormatSelection.ForTier(QualityTier.High));

        Assert.Equal(1080, resolved.Video!.Height);
    }

    [Fact]
    public void BalancedTierCapsAt720p()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, FormatSelection.ForTier(QualityTier.Balanced));

        Assert.Equal(720, resolved.Video!.Height);
    }

    [Fact]
    public void SmallestTierStaysWatchable()
    {
        // 240p exists in the fixture, but "smallest" should not drop below 360p when it can help it.
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, FormatSelection.ForTier(QualityTier.Smallest));

        Assert.Equal(360, resolved.Video!.Height);
    }

    [Fact]
    public void AutoCodecPrefersH264AmongOtherwiseEqualStreams()
    {
        // At the same height and frame rate, H.264 wins: it plus AAC remuxes into an MP4 with no
        // re-encoding at all. The fixture has H.264 only at 30fps, so the ceiling matches that.
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            MaxHeight = 1080,
            MaxFps = 30,
        });

        Assert.Equal(1080, resolved.Video!.Height);
        Assert.Equal(VideoCodec.H264, resolved.Video.VideoCodec);
    }

    [Fact]
    public void FrameRateOutranksCodecPreference()
    {
        // The fixture publishes H.264 at 1080p30 and VP9/AV1 at 1080p60. Preferring a codec must
        // never cost the user half the frame rate, so the 60fps stream wins.
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { MaxHeight = 1080 });

        Assert.Equal(60, resolved.Video!.Fps);
        Assert.NotEqual(VideoCodec.H264, resolved.Video.VideoCodec);
    }

    [Fact]
    public void ResolutionAlwaysBeatsCodecPreference()
    {
        // The user prefers H.264, but it only goes to 1080p. Asking for the best quality must not
        // silently drop from 2160p to 1080p just to honour a codec preference.
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            Tier = QualityTier.Best,
            VideoCodec = VideoCodecPreference.H264,
        });

        Assert.Equal(2160, resolved.Video!.Height);
        Assert.NotEqual(VideoCodec.H264, resolved.Video.VideoCodec);
    }

    [Fact]
    public void ExplicitCodecPreferenceWinsAmongEqualResolutions()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            MaxHeight = 1080,
            VideoCodec = VideoCodecPreference.Av1,
        });

        Assert.Equal(VideoCodec.Av1, resolved.Video!.VideoCodec);
        Assert.Equal(1080, resolved.Video.Height);
    }

    [Fact]
    public void NeverUpscalesWhenTheCeilingExceedsWhatExists()
    {
        // Asking for 8K from a video that tops out at 360p yields 360p, not an invented format.
        var item = TestMedia.MuxedOnlyVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { MaxHeight = 4320 });

        Assert.Equal(360, resolved.Video!.Height);
    }

    [Fact]
    public void FallsBackToTheSmallestWhenTheCeilingExcludesEverything()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { MaxHeight = 100 });

        Assert.NotNull(resolved.Video);
        Assert.Equal(240, resolved.Video!.Height);
    }

    [Fact]
    public void FpsCeilingIsRespected()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { MaxFps = 30 });

        Assert.True(resolved.Video!.Fps <= 30);
    }

    [Fact]
    public void PrefersHdrWhenAskedAndAvailable()
    {
        var item = TestMedia.HdrVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { Hdr = HdrPolicy.PreferHdr });

        Assert.True(resolved.Video!.IsHdr);
    }

    [Fact]
    public void PrefersSdrWhenAskedTo()
    {
        // Asking for SDR must actively avoid HDR, not merely stop seeking it: the compatibility
        // preset relies on this to keep an HDR stream out of a tone-mapping re-encode.
        var item = TestMedia.HdrVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { Hdr = HdrPolicy.PreferSdr });

        Assert.Equal(2160, resolved.Video!.Height);
        Assert.False(resolved.Video.IsHdr);
    }

    [Fact]
    public void AutoHdrPolicyNeitherSeeksNorAvoidsHdr()
    {
        // Auto must not rank on dynamic range at all, so the choice is decided by the other
        // signals and HDR is simply preserved when it happens to win on them.
        var item = TestMedia.HdrVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection { Hdr = HdrPolicy.Auto });

        Assert.NotNull(resolved.Video);
        Assert.Equal(2160, resolved.Video!.Height);
    }

    [Fact]
    public void HdrIsNeverDiscardedByTheDefaultQualityTier()
    {
        // "Best available" should come back with the HDR rendition when the source has one.
        var item = TestMedia.HdrVideo();
        var resolved = FormatSelector.ResolveVideo(item, FormatSelection.ForTier(QualityTier.Best));

        Assert.True(resolved.Video!.IsHdr, "the best-quality tier dropped an available HDR stream");
    }

    [Fact]
    public void ExcludesStoryboardsAndDrmStreams()
    {
        var item = TestMedia.StandardVideo() with
        {
            Formats = [.. TestMedia.StandardVideo().Formats, TestMedia.DrmFormat("drm1", 4320)],
        };

        var candidates = FormatSelector.Candidates(item, allowDrc: false).ToList();

        Assert.DoesNotContain(candidates, f => f.HasDrm);
        Assert.DoesNotContain(candidates, f => f.IsStoryboard);
    }

    [Fact]
    public void HidesDynamicRangeCompressedAudioByDefault()
    {
        var item = TestMedia.StandardVideo();
        var candidates = FormatSelector.Candidates(item, allowDrc: false).ToList();

        Assert.DoesNotContain(candidates, f => f.IsDrcVariant);
    }

    [Fact]
    public void PicksTheHighestBitrateAudio()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveAudioOnly(item, new FormatSelection());

        Assert.Single(resolved.Audio);
        Assert.Equal(129, resolved.Audio[0].AudioBitrate);
    }

    [Fact]
    public void AudioCodecPreferenceIsHonoured()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveAudioOnly(item, new FormatSelection
        {
            AudioCodec = AudioCodecPreference.Aac,
        });

        Assert.Equal(AudioCodec.Aac, resolved.Audio[0].AudioCodec);
    }

    [Fact]
    public void SelectsASpecificAudioLanguage()
    {
        var item = TestMedia.MultiLanguageVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            AudioLanguages = ["pt-BR"],
        });

        Assert.Single(resolved.Audio);
        Assert.Equal("pt-BR", resolved.Audio[0].Language);
    }

    [Fact]
    public void MatchesACoarseLanguageTagAgainstARegionalTrack()
    {
        var item = TestMedia.MultiLanguageVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            AudioLanguages = ["pt"],
        });

        Assert.Single(resolved.Audio);
        Assert.Equal("pt-BR", resolved.Audio[0].Language);
    }

    [Fact]
    public void SelectsEveryAudioLanguageWhenAsked()
    {
        var item = TestMedia.MultiLanguageVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            AllAudioLanguages = true,
        });

        Assert.Equal(3, resolved.Audio.Count);
        Assert.Contains(resolved.Audio, a => a.Language == "en");
        Assert.Contains(resolved.Audio, a => a.Language == "pt-BR");
        Assert.Contains(resolved.Audio, a => a.Language == "es");
    }

    [Fact]
    public void PinnedFormatIdsAreUsedExactly()
    {
        var item = TestMedia.StandardVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection
        {
            PinnedVideoFormatId = "247",
            PinnedAudioFormatId = "140",
        });

        Assert.Equal("247", resolved.Video!.FormatId);
        Assert.Equal("140", resolved.Audio[0].FormatId);
    }

    [Fact]
    public void UsesAMuxedStreamWhenNothingElseExists()
    {
        var item = TestMedia.MuxedOnlyVideo();
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection());

        Assert.NotNull(resolved.Video);
        Assert.True(resolved.UsesMuxedSource);
        Assert.Empty(resolved.Audio);
    }

    [Fact]
    public void AudioOnlyFallsBackToAMuxedStream()
    {
        var item = TestMedia.MuxedOnlyVideo();
        var resolved = FormatSelector.ResolveAudioOnly(item, new FormatSelection());

        Assert.Single(resolved.Audio);
        Assert.True(resolved.UsesMuxedSource);
    }

    [Fact]
    public void ReturnsNothingWhenThereAreNoUsableFormats()
    {
        var item = TestMedia.StandardVideo() with { Formats = [TestMedia.Storyboard("sb0")] };
        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection());

        Assert.False(resolved.HasAnything);
    }

    [Fact]
    public void EstimatedSizeSumsTheChosenStreams()
    {
        var item = TestMedia.StandardVideo() with
        {
            Formats =
            [
                TestMedia.VideoFormat("v", 1080, size: 100_000_000),
                TestMedia.AudioFormat("a", 129, size: 5_000_000),
            ],
        };

        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection());

        Assert.Equal(105_000_000, resolved.EstimatedSize);
    }

    [Fact]
    public void EstimatedSizeIsUnknownWhenAStreamDoesNotReportOne()
    {
        // A 4K video whose size the source omits must not be advertised as the size of its audio
        // track. Reporting nothing is honest; reporting 9 MB for a gigabyte download is not.
        var item = TestMedia.StandardVideo() with
        {
            Formats =
            [
                TestMedia.VideoFormat("v", 2160, size: null),
                TestMedia.AudioFormat("a", 129, size: 10_000_000),
            ],
        };

        var resolved = FormatSelector.ResolveVideo(item, new FormatSelection());

        Assert.NotNull(resolved.Video);
        Assert.Null(resolved.EstimatedSize);
    }
}

/// <summary>
/// The presets must genuinely rank formats differently. If every priority produced the same
/// choice, the preset names would be decoration over a single fixed rule.
/// </summary>
public class FormatPriorityTests
{
    [Fact]
    public void QualityTakesTheHighestResolutionWhateverTheCodec()
    {
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Quality });

        Assert.Equal(2160, resolved.Video!.Height);
    }

    [Fact]
    public void CompatibilityTakesH264EvenWhenThatCostsResolution()
    {
        // The fixture publishes H.264 only up to 1080p30, while VP9 and AV1 reach 2160p60.
        // A preset whose whole purpose is playing anywhere should accept that trade knowingly.
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Compatibility });

        Assert.Equal(VideoCodec.H264, resolved.Video!.VideoCodec);
        Assert.Equal(1080, resolved.Video.Height);
    }

    [Fact]
    public void QualityAndCompatibilityDisagreeOnTheSameItem()
    {
        var item = TestMedia.StandardVideo();

        var quality = FormatSelector.ResolveVideo(item, new FormatSelection { Priority = FormatPriority.Quality });
        var compatible = FormatSelector.ResolveVideo(item, new FormatSelection { Priority = FormatPriority.Compatibility });

        Assert.NotEqual(quality.Video!.FormatId, compatible.Video!.FormatId);
    }

    [Fact]
    public void SizeTakesTheSmallestWatchableStream()
    {
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Size });

        Assert.Equal(360, resolved.Video!.Height);
    }

    [Fact]
    public void CompatibilityPrefersAacAudio()
    {
        // AAC is what every player and editor accepts; Opus is not.
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Compatibility });

        Assert.Equal(AudioCodec.Aac, resolved.Audio[0].AudioCodec);
    }

    [Fact]
    public void QualityPrefersTheHighestBitrateAudio()
    {
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Quality });

        Assert.Equal(129, resolved.Audio[0].AudioBitrate);
    }

    [Fact]
    public void SizePrefersTheLeanestAudio()
    {
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Size });

        // The lowest bitrate track, not the best one.
        Assert.True(resolved.Audio[0].AudioBitrate < 60,
            $"size priority chose a {resolved.Audio[0].AudioBitrate} kbps track");
    }

    [Fact]
    public void ResolutionCeilingStillOutranksPriority()
    {
        // A ceiling is an instruction, not a hint: no priority may exceed it.
        var resolved = FormatSelector.ResolveVideo(
            TestMedia.StandardVideo(),
            new FormatSelection { Priority = FormatPriority.Quality, MaxHeight = 720 });

        Assert.True(resolved.Video!.Height <= 720);
    }

    [Theory]
    [InlineData(PresetCatalog.VideoBestId, FormatPriority.Quality, HdrPolicy.PreferHdr)]
    [InlineData(PresetCatalog.VideoCompatibleId, FormatPriority.Compatibility, HdrPolicy.PreferSdr)]
    [InlineData(PresetCatalog.VideoSmallId, FormatPriority.Size, HdrPolicy.PreferSdr)]
    public void BuiltInPresetsCarryTheirPolicyIntoSelection(
        string presetId, FormatPriority expectedPriority, HdrPolicy expectedHdr)
    {
        var preset = PresetCatalog.Find(presetId);
        Assert.NotNull(preset);

        var selection = preset!.ToSelection();

        Assert.Equal(expectedPriority, selection.Priority);
        Assert.Equal(expectedHdr, selection.Hdr);
    }

    [Fact]
    public void TheCompatibilityPresetAvoidsHdrEndToEnd()
    {
        // The point of the preset is an MP4 that needs no re-encoding. Picking an HDR stream
        // would force a tone-map, so the preset must not select one.
        var preset = PresetCatalog.Find(PresetCatalog.VideoCompatibleId)!;
        var resolved = FormatSelector.ResolveVideo(TestMedia.HdrVideo(), preset.ToSelection());

        Assert.False(resolved.Video!.IsHdr);
    }
}

/// <summary>
/// The size shown must never claim more than is known. These cover each state and the case
/// where re-encoding makes the figure describe the download rather than the resulting file.
/// </summary>
public class SizeEstimateTests
{
    [Fact]
    public void EveryComponentExactGivesAnExactTotal()
    {
        var size = SizeEstimate.Combine([100_000_000L, 5_000_000L], anyIsApproximate: false);

        Assert.Equal(SizeCertainty.Exact, size.Certainty);
        Assert.Equal(105_000_000, size.Bytes);
        Assert.Equal("Size_Exact", size.LabelKey);
    }

    [Fact]
    public void AnApproximateComponentDowngradesToEstimated()
    {
        var size = SizeEstimate.Combine([100_000_000L, 5_000_000L], anyIsApproximate: true);

        Assert.Equal(SizeCertainty.Estimated, size.Certainty);
        Assert.Equal("Size_Estimated", size.LabelKey);
    }

    [Fact]
    public void AMissingComponentMakesTheTotalAFloor()
    {
        // The bug this exists for: a 4K stream with no reported size plus a 10 MB audio track
        // must not be announced as a 10 MB download.
        var size = SizeEstimate.Combine([null, 10_000_000L], anyIsApproximate: false);

        Assert.Equal(SizeCertainty.AtLeast, size.Certainty);
        Assert.Equal(10_000_000, size.Bytes);
        Assert.Equal("Size_AtLeast", size.LabelKey);
    }

    [Fact]
    public void NothingKnownIsUnknown()
    {
        var size = SizeEstimate.Combine([null, null], anyIsApproximate: false);

        Assert.Equal(SizeCertainty.Unknown, size.Certainty);
        Assert.False(size.HasValue);
        Assert.Equal("Size_Unknown", size.LabelKey);
    }

    [Fact]
    public void NoStreamsAtAllIsUnknown()
    {
        Assert.Equal(SizeCertainty.Unknown, SizeEstimate.Combine([], false).Certainty);
    }

    [Fact]
    public void ReencodingRelabelsTheFigureAsTheDownloadSize()
    {
        // Converting a ~130 kbit/s source to VBR MP3 roughly doubles the file, so the download
        // figure must not be presented as the size of the result.
        var size = SizeEstimate.Combine([2_700_000L], anyIsApproximate: false);

        Assert.Equal("Size_Exact", size.LabelKeyFor(outputWillBeReencoded: false));
        Assert.Equal("Size_BeforeConversion", size.LabelKeyFor(outputWillBeReencoded: true));
    }

    [Fact]
    public void AnUnknownSizeStaysUnknownEvenWhenReencoding()
    {
        // With no number there is nothing to qualify; "download size" beside no value would be
        // worse than saying it is unknown.
        var size = SizeEstimate.Unknown;

        Assert.Equal("Size_Unknown", size.LabelKeyFor(outputWillBeReencoded: true));
    }
}

public class FormatCatalogTests
{
    [Fact]
    public void ListsOnlyResolutionsThatActuallyExist()
    {
        var catalog = FormatCatalog.Build(TestMedia.StandardVideo());
        var heights = catalog.Resolutions.Select(r => r.Height).ToList();

        Assert.Contains(2160, heights);
        Assert.Contains(1080, heights);
        Assert.Contains(240, heights);
        // Nothing above what the source publishes.
        Assert.DoesNotContain(4320, heights);
        Assert.Equal(heights.OrderByDescending(h => h), heights);
    }

    [Fact]
    public void ReportsFrameRatesPerResolution()
    {
        var catalog = FormatCatalog.Build(TestMedia.StandardVideo());
        var uhd = catalog.Resolutions.First(r => r.Height == 2160);

        Assert.Contains(60d, uhd.FrameRates);
        Assert.Equal("2160p60", uhd.Label);
        Assert.Equal("4K", uhd.CommonName);
    }

    [Fact]
    public void ReportsCodecsPerResolution()
    {
        var catalog = FormatCatalog.Build(TestMedia.StandardVideo());
        var fullHd = catalog.Resolutions.First(r => r.Height == 1080);

        Assert.Contains(VideoCodec.H264, fullHd.Codecs);
        Assert.Contains(VideoCodec.Vp9, fullHd.Codecs);
        Assert.Contains(VideoCodec.Av1, fullHd.Codecs);
    }

    [Fact]
    public void DetectsHdrAvailability()
    {
        Assert.True(FormatCatalog.Build(TestMedia.HdrVideo()).HasHdr);
        Assert.False(FormatCatalog.Build(TestMedia.StandardVideo()).HasHdr);
    }

    [Fact]
    public void GroupsAudioTracksByLanguage()
    {
        var catalog = FormatCatalog.Build(TestMedia.MultiLanguageVideo());

        Assert.True(catalog.HasMultipleAudioTracks);
        Assert.Equal(3, catalog.AudioTracks.Count);
    }

    [Fact]
    public void ReportsTheRealAudioBitrate()
    {
        // YouTube tops out near 130 kbit/s. The catalog must report that, not a rounder number.
        var catalog = FormatCatalog.Build(TestMedia.StandardVideo());
        var track = catalog.AudioTracks.Single();

        Assert.Equal(129, track.BestBitrate);
    }

    [Fact]
    public void IsEmptyWhenNothingIsSelectable()
    {
        var item = TestMedia.StandardVideo() with { Formats = [TestMedia.Storyboard("sb0")] };
        Assert.True(FormatCatalog.Build(item).IsEmpty);
    }
}

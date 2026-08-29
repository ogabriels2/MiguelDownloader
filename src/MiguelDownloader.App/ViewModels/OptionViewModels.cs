using MiguelDownloader.App.Converters;
using MiguelDownloader.App.Localization;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Presets;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiguelDownloader.App.ViewModels;

/// <summary>A reusable output profile shown in the download workspace.</summary>
public sealed class PresetOptionViewModel(DownloadPreset preset)
{
    public DownloadPreset Preset { get; } = preset;

    public string Name => Preset.IsBuiltIn ? Loc.Get(Preset.Name) : Preset.Name;

    public string Description => Preset.IsBuiltIn && !string.IsNullOrWhiteSpace(Preset.Description)
        ? Loc.Get(Preset.Description)
        : Preset.Description;
}

/// <summary>
/// A resolution the source genuinely offers.
/// <para>
/// Built only from formats present in the analysis, so the list can never advertise a quality
/// that does not exist for this particular video.
/// </para>
/// </summary>
public sealed class ResolutionOptionViewModel(ResolutionOption option)
{
    public ResolutionOption Option { get; } = option;

    public int Height => Option.Height;

    /// <summary>e.g. "1080p60 · Full HD".</summary>
    public string Display => Option.CommonName is { Length: > 0 } common
        ? $"{Option.Label} · {common}"
        : Option.Label;

    /// <summary>Codecs and size range, shown as secondary detail.</summary>
    public string Detail
    {
        get
        {
            var codecs = string.Join(", ", Option.Codecs.Select(CodecParser.Label));
            if (Option.SmallestKnownSize is not { } smallest) return codecs;

            var size = Option.LargestKnownSize is { } largest && largest != smallest
                ? $"{ByteSizeConverter.Format(smallest)} – {ByteSizeConverter.Format(largest)}"
                : ByteSizeConverter.Format(smallest);

            return $"{codecs} · {size}";
        }
    }

    public bool HasHdr => Option.HasHdr;
}

/// <summary>One audio track, selectable when the source publishes several languages.</summary>
public sealed partial class AudioTrackOptionViewModel(AudioTrackOption option) : ObservableObject
{
    public AudioTrackOption Option { get; } = option;

    public string LanguageCode => Option.LanguageCode;

    public string Display => Option.DisplayName;

    /// <summary>
    /// The real characteristics of the track. The bitrate shown is what the source reports, which
    /// for YouTube is around 130 kbit/s at best; no rounder, more flattering figure is invented.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (Option.Codecs.Count > 0)
                parts.Add(string.Join(", ", Option.Codecs.Select(CodecParser.Label)));

            if (Option.BestBitrate is > 0)
                parts.Add($"{Option.BestBitrate:0} kbps");

            if (Option.SampleRate is > 0)
                parts.Add($"{Option.SampleRate / 1000.0:0.#} kHz");

            if (Option.Channels is > 2)
                parts.Add($"{Option.Channels} ch");

            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One subtitle track offered by the source.</summary>
public sealed partial class SubtitleOptionViewModel(SubtitleTrack track) : ObservableObject
{
    public SubtitleTrack Track { get; } = track;

    public string LanguageCode => Track.LanguageCode;

    public string Display => Track.DisplayName;

    /// <summary>Says plainly whether a track was authored or machine-generated.</summary>
    public string OriginLabel => Track.Origin switch
    {
        SubtitleOrigin.Manual => Loc.Get("Subtitle_Manual"),
        SubtitleOrigin.Automatic => Loc.Get("Subtitle_Automatic"),
        _ => Loc.Get("Subtitle_Translated"),
    };

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One raw format row for the advanced view, showing exactly what the extractor reported.
/// </summary>
public sealed class FormatRowViewModel(MediaFormat format)
{
    public MediaFormat Format { get; } = format;

    public string FormatId => Format.FormatId;
    public string Extension => Format.Extension;
    public string Resolution => Format.IsAudioOnly ? "—" : Format.Resolution;

    public string Codecs => Format.IsAudioOnly
        ? CodecParser.Label(Format.AudioCodec)
        : Format.HasAudio
            ? $"{CodecParser.Label(Format.VideoCodec)} + {CodecParser.Label(Format.AudioCodec)}"
            : CodecParser.Label(Format.VideoCodec);

    public string Fps => Format.Fps is > 0 ? $"{Format.Fps:0.##}" : "—";

    public string Bitrate
    {
        get
        {
            var value = Format.IsAudioOnly
                ? Format.AudioBitrate
                : Format.VideoBitrate ?? Format.TotalBitrate;
            return value is > 0 ? $"{value:0} kbps" : "—";
        }
    }

    /// <summary>Size, marked with a tilde when the source only offered an estimate.</summary>
    public string Size => Format.BestKnownSize is { } size
        ? (Format.SizeIsEstimate ? "~" : string.Empty) + ByteSizeConverter.Format(size)
        : "—";

    public string DynamicRange => Format.IsHdr ? Format.DynamicRange ?? "HDR" : "SDR";

    public string Language => Format.Language ?? "—";

    public string Kind => Format.IsMuxed
        ? $"{Loc.Get("Mode_Video")} + {Loc.Get("Mode_Audio")}"
        : Format.IsAudioOnly
            ? Loc.Get("Mode_Audio")
            : Loc.Get("Mode_Video");
}

/// <summary>One entry of a playlist, album or channel, with its selection state.</summary>
public sealed partial class CollectionItemViewModel(MediaItem item, int position) : ObservableObject
{
    public MediaItem Item { get; } = item;

    public int Position { get; } = position;

    public string Title => Item.Title;
    public string Author => Item.DisplayAuthor;
    public string? ThumbnailUrl => Item.BestThumbnail?.Url;

    public string DurationText => Item.Duration is { } duration
        ? TimeSpanDisplayConverter.Format(duration)
        : "—";

    [ObservableProperty]
    private bool _isSelected = true;
}

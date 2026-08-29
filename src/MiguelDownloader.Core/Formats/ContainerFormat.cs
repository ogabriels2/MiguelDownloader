namespace MiguelDownloader.Core.Formats;

/// <summary>Output containers the app can produce for video.</summary>
public enum ContainerFormat
{
    /// <summary>Pick whichever container holds the chosen streams without re-encoding.</summary>
    Auto = 0,
    Mp4,
    Mkv,
    WebM,
}

/// <summary>What has to happen to a stream on the way into the output container.</summary>
public enum StreamAction
{
    /// <summary>No such stream in the output.</summary>
    None = 0,
    /// <summary>Copy the bits verbatim. No quality loss, no CPU cost.</summary>
    Copy,
    /// <summary>Decode and re-encode. Always lossy; only ever done when the user asks for it.</summary>
    Transcode,
}

/// <summary>How subtitles end up in the result.</summary>
public enum SubtitleDisposition
{
    None = 0,
    /// <summary>Written next to the media file.</summary>
    Sidecar,
    /// <summary>Embedded in the container in its native text format.</summary>
    Embedded,
    /// <summary>Embedded after conversion, because the container only accepts one subtitle codec.</summary>
    EmbeddedConverted,
}

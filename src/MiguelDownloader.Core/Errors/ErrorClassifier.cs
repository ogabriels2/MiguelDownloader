namespace MiguelDownloader.Core.Errors;

/// <summary>
/// Turns raw tool output into a <see cref="DownloadError"/>.
/// <para>
/// The patterns come from the messages yt-dlp and ffmpeg actually emit. Matching is ordered:
/// the most specific causes are tested first, because several messages overlap (a members-only
/// video also mentions signing in, and a bot check is a kind of login prompt).
/// </para>
/// </summary>
public static class ErrorClassifier
{
    private readonly record struct Rule(
        string[] Needles,
        DownloadErrorKind Kind,
        string MessageKey,
        RecommendedAction Action);

    // Ordered most-specific first. Every needle is matched case-insensitively.
    private static readonly Rule[] Rules =
    [
        // --- Local environment. Checked first: these look like tool failures but are ours to fix. ---
        new(["no space left on device", "not enough space on the disk", "insufficient disk space",
             "espaço insuficiente"],
            DownloadErrorKind.DiskFull, "Error_DiskFull", RecommendedAction.FreeDiskSpace),

        new(["permission denied", "access is denied", "acesso negado", "errno 13"],
            DownloadErrorKind.PermissionDenied, "Error_PermissionDenied", RecommendedAction.ChooseAnotherFolder),

        new(["being used by another process", "used by another process", "sharing violation",
             "text file busy"],
            DownloadErrorKind.FileInUse, "Error_FileInUse", RecommendedAction.CloseFileAndRetry),

        new(["path too long", "filename too long", "could not create directory"],
            DownloadErrorKind.PathTooLong, "Error_PathTooLong", RecommendedAction.ChooseAnotherFolder),

        // --- Tooling ---
        new(["ffmpeg not found", "ffprobe not found", "ffmpeg is not installed",
             "you have requested merging of multiple formats but ffmpeg is not installed"],
            DownloadErrorKind.FfmpegMissing, "Error_FfmpegMissing", RecommendedAction.InstallTools),

        new(["no supported javascript runtime"],
            DownloadErrorKind.JsRuntimeMissing, "Error_JsRuntimeMissing", RecommendedAction.InstallTools),

        new(["is not a valid url", "unsupported url", "unable to extract webpage"],
            DownloadErrorKind.UnsupportedUrl, "Error_UnsupportedUrl", RecommendedAction.CheckUrl),

        // --- Availability. Specific reasons before the generic "unavailable". ---
        new(["this video has been removed by the uploader", "video has been removed",
             "removed for violating"],
            DownloadErrorKind.VideoRemoved, "Error_VideoRemoved", RecommendedAction.None),

        new(["private video", "this video is private"],
            DownloadErrorKind.PrivateVideo, "Error_PrivateVideo", RecommendedAction.SignIn),

        new(["members-only", "available to this channel's members", "join this channel"],
            DownloadErrorKind.MembersOnly, "Error_MembersOnly", RecommendedAction.None),

        new(["sign in to confirm you're not a bot", "confirm you are not a bot", "not a bot"],
            DownloadErrorKind.BotCheck, "Error_BotCheck", RecommendedAction.SignIn),

        new(["sign in to confirm your age", "age-restricted", "age restricted",
             "inappropriate for some users"],
            DownloadErrorKind.AgeRestricted, "Error_AgeRestricted", RecommendedAction.SignIn),

        // Measured on TikTok: "Your IP address is blocked from accessing this post". This is about
        // the requester, not the country, so it is tested before the geo rule and worded its own way.
        new(["ip address is blocked", "your ip is blocked", "ip has been blocked"],
            DownloadErrorKind.IpBlocked, "Error_IpBlocked", RecommendedAction.RetryLater),

        new(["not made this video available in your country", "not available in your country",
             "blocked it in your country", "geo restricted", "geo-restricted"],
            DownloadErrorKind.GeoRestricted, "Error_GeoRestricted", RecommendedAction.None),

        new(["this live event will begin", "premieres in", "live event has not started",
             "is not currently live"],
            DownloadErrorKind.LiveNotStarted, "Error_LiveNotStarted", RecommendedAction.RetryLater),

        new(["drm", "protected by drm", "widevine", "playready"],
            DownloadErrorKind.DrmProtected, "Error_DrmProtected", RecommendedAction.None),

        // Measured on Facebook: the extractor asks to be told it broke rather than naming a cause.
        // A stale extractor is the usual reason, and updating the tools is the fix that works.
        // "please report this issue on" is deliberately not a needle: yt-dlp appends it to errors
        // that already have a specific cause, and matching it would relabel those as a stale tool.
        new(["cannot parse data", "unable to extract video data", "unable to extract shared data",
             "unable to extract player version"],
            DownloadErrorKind.ToolOutdated, "Error_ToolOutdated", RecommendedAction.UpdateTools),

        // "log in" covers TikTok's "Log in for access" and Vimeo's "only works when logged-in";
        // "cookies" covers every extractor that names the flag in its own error.
        new(["sign in", "login required", "log in", "logged-in", "authentication", "cookies"],
            DownloadErrorKind.LoginRequired, "Error_LoginRequired", RecommendedAction.SignIn),

        new(["video unavailable", "this video is unavailable", "content isn't available",
             "video indisponível"],
            DownloadErrorKind.VideoUnavailable, "Error_VideoUnavailable", RecommendedAction.CheckUrl),

        // --- Formats ---
        new(["requested format is not available", "requested format not available"],
            DownloadErrorKind.FormatUnavailable, "Error_FormatUnavailable", RecommendedAction.ChooseAnotherFormat),

        new(["no video formats found", "no formats found", "failed to extract any player response"],
            DownloadErrorKind.NoFormatsFound, "Error_NoFormatsFound", RecommendedAction.UpdateTools),

        // --- Network ---
        new(["http error 429", "too many requests", "rate-limit", "rate limit"],
            DownloadErrorKind.RateLimited, "Error_RateLimited", RecommendedAction.RetryLater),

        new(["http error 403", "forbidden"],
            DownloadErrorKind.ExpiredStreamUrl, "Error_ExpiredStreamUrl", RecommendedAction.Retry),

        new(["timed out", "timeout", "the operation has timed out"],
            DownloadErrorKind.Timeout, "Error_Timeout", RecommendedAction.Retry),

        new(["getaddrinfo failed", "temporary failure in name resolution", "network is unreachable",
             "connection refused", "connection reset", "unable to download webpage",
             "no such host is known", "ssl", "certificate", "connection aborted",
             "remote name could not be resolved"],
            DownloadErrorKind.NetworkFailure, "Error_NetworkFailure", RecommendedAction.CheckConnection),

        // --- Processing ---
        new(["error while muxing", "muxing failed", "could not write header",
             "invalid argument" ],
            DownloadErrorKind.MuxFailed, "Error_MuxFailed", RecommendedAction.Retry),

        new(["conversion failed", "encoder not found", "unknown encoder", "error while encoding"],
            DownloadErrorKind.ConversionFailed, "Error_ConversionFailed", RecommendedAction.ChooseAnotherFormat),

        new(["postprocessing:", "embedding metadata", "unable to embed"],
            DownloadErrorKind.MetadataFailed, "Error_MetadataFailed", RecommendedAction.Retry),

        // --- Collections ---
        new(["the playlist does not exist", "playlist is empty", "no entries"],
            DownloadErrorKind.PlaylistEmpty, "Error_PlaylistEmpty", RecommendedAction.CheckUrl),
    ];

    /// <summary>
    /// Classifies raw tool output. <paramref name="text"/> is normally the collected stderr from
    /// a failed run; it is returned untouched in <see cref="DownloadError.TechnicalDetails"/>.
    /// </summary>
    public static DownloadError Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DownloadError
            {
                Kind = DownloadErrorKind.Unknown,
                MessageKey = "Error_Unknown",
                Action = RecommendedAction.Retry,
                TechnicalDetails = text,
            };
        }

        // Actual failures are diagnosed before advisory output.
        //
        // A run that fails typically emits warnings first and the real reason last: an
        // unavailable video is commonly preceded by a warning about the JavaScript runtime.
        // Scanning everything at once would match the warning and tell the user to install a
        // runtime when the video simply does not exist. Restricting the first pass to lines the
        // tool marked as errors keeps the diagnosis on the thing that actually went wrong.
        var errorLines = ExtractErrorLines(text);
        if (errorLines is { Length: > 0 } && Match(errorLines, text) is { } fromErrors)
            return fromErrors;

        // Nothing was marked as an error: fall back to the whole output, which is where a bare
        // ffmpeg or operating-system message shows up.
        if (Match(text, text) is { } fromAll) return fromAll;

        return new DownloadError
        {
            Kind = DownloadErrorKind.Unknown,
            MessageKey = "Error_Unknown",
            Action = RecommendedAction.ContactSupportWithLogs,
            TechnicalDetails = text,
        };
    }

    /// <summary>Runs the rule table over one body of text.</summary>
    private static DownloadError? Match(string haystackSource, string fullTextForDetails)
    {
        var haystack = haystackSource.ToLowerInvariant();

        foreach (var rule in Rules)
        {
            foreach (var needle in rule.Needles)
            {
                if (!haystack.Contains(needle, StringComparison.Ordinal)) continue;

                return new DownloadError
                {
                    Kind = rule.Kind,
                    MessageKey = rule.MessageKey,
                    Action = rule.Action,
                    TechnicalDetails = fullTextForDetails,
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Pulls out the lines the tool marked as errors. Python tracebacks are included because the
    /// exception line they end with carries the real cause.
    /// </summary>
    private static string ExtractErrorLines(string text)
    {
        var matched = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Exception", StringComparison.Ordinal))
            {
                matched.Add(trimmed);
            }
        }

        return matched.Count == 0 ? string.Empty : string.Join('\n', matched);
    }

    /// <summary>Classifies a caught exception, mapping the common IO failures onto real causes.</summary>
    public static DownloadError Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case OperationCanceledException:
                return new DownloadError
                {
                    Kind = DownloadErrorKind.Cancelled,
                    MessageKey = "Error_Cancelled",
                    Action = RecommendedAction.None,
                };

            case UnauthorizedAccessException:
                return new DownloadError
                {
                    Kind = DownloadErrorKind.PermissionDenied,
                    MessageKey = "Error_PermissionDenied",
                    Action = RecommendedAction.ChooseAnotherFolder,
                    TechnicalDetails = exception.ToString(),
                };

            case PathTooLongException:
                return new DownloadError
                {
                    Kind = DownloadErrorKind.PathTooLong,
                    MessageKey = "Error_PathTooLong",
                    Action = RecommendedAction.ChooseAnotherFolder,
                    TechnicalDetails = exception.ToString(),
                };

            case IOException io:
                // Windows reports "disk full" as an HRESULT rather than a distinct exception type.
                const int errorDiskFull = unchecked((int)0x80070070);
                const int errorHandleDiskFull = unchecked((int)0x80070027);
                if (io.HResult == errorDiskFull || io.HResult == errorHandleDiskFull)
                {
                    return new DownloadError
                    {
                        Kind = DownloadErrorKind.DiskFull,
                        MessageKey = "Error_DiskFull",
                        Action = RecommendedAction.FreeDiskSpace,
                        TechnicalDetails = exception.ToString(),
                    };
                }
                // Everything else still runs through the text rules, which catch sharing violations.
                var fromText = Classify(io.Message);
                return fromText with { TechnicalDetails = exception.ToString() };

            default:
                var classified = Classify(exception.Message);
                return classified with { TechnicalDetails = exception.ToString() };
        }
    }
}

namespace MiguelDownloader.Core.Errors;

/// <summary>
/// The kinds of failure the app can recognise and explain.
/// <para>
/// Each value maps to a message and a recommended action in the UI. Anything that cannot be
/// matched becomes <see cref="Unknown"/>, which shows the raw tool output rather than pretending
/// to a diagnosis it does not have.
/// </para>
/// </summary>
public enum DownloadErrorKind
{
    Unknown = 0,

    // --- Input ---
    InvalidUrl,
    UnsupportedUrl,

    // --- Availability ---
    VideoUnavailable,
    VideoRemoved,
    PrivateVideo,
    MembersOnly,
    GeoRestricted,
    AgeRestricted,
    LoginRequired,
    /// <summary>The extractor was asked to prove it is not automated.</summary>
    BotCheck,
    /// <summary>The stream is DRM-protected. Not something the app works around.</summary>
    DrmProtected,
    /// <summary>
    /// The site refused this requester rather than this content. Distinct from
    /// <see cref="GeoRestricted"/>, which is about where the item may be shown: TikTok says
    /// plainly that the address is blocked, and telling the user their country is the problem
    /// would send them looking for the wrong fix.
    /// </summary>
    IpBlocked,
    LiveNotStarted,

    // --- Formats ---
    FormatUnavailable,
    NoFormatsFound,

    // --- Network ---
    NetworkFailure,
    Timeout,
    RateLimited,
    /// <summary>Signed stream URLs expired mid-transfer; retrying re-signs them.</summary>
    ExpiredStreamUrl,

    // --- Local environment ---
    DiskFull,
    PermissionDenied,
    FileInUse,
    PathTooLong,

    // --- Tooling ---
    FfmpegMissing,
    YtDlpMissing,
    ToolOutdated,
    JsRuntimeMissing,

    // --- Processing ---
    MuxFailed,
    ConversionFailed,
    MetadataFailed,
    ValidationFailed,

    // --- Collections ---
    PlaylistPartiallyUnavailable,
    PlaylistEmpty,

    /// <summary>
    /// The target file exists and the policy is to ask. Not really a failure: the queue prompts
    /// and resubmits the job with the answer.
    /// </summary>
    FileAlreadyExists,

    // --- Control flow ---
    Cancelled,
}

/// <summary>What the user can usefully do next.</summary>
public enum RecommendedAction
{
    None = 0,
    Retry,
    RetryLater,
    CheckUrl,
    CheckConnection,
    FreeDiskSpace,
    ChooseAnotherFolder,
    CloseFileAndRetry,
    ChooseAnotherFormat,
    SignIn,
    UpdateTools,
    InstallTools,
    ContactSupportWithLogs,
}

/// <summary>
/// A classified failure: what went wrong, what to suggest, and the untouched tool output for
/// the log and the diagnostics bundle.
/// </summary>
public sealed record DownloadError
{
    public required DownloadErrorKind Kind { get; init; }
    public RecommendedAction Action { get; init; } = RecommendedAction.None;

    /// <summary>Resource key for the user-facing explanation.</summary>
    public required string MessageKey { get; init; }

    /// <summary>Raw output from the failing tool. Logged and shown under "details", never lost.</summary>
    public string? TechnicalDetails { get; init; }

    /// <summary>True when retrying has a real chance of a different outcome.</summary>
    public bool IsRetryable => Kind is
        DownloadErrorKind.NetworkFailure or DownloadErrorKind.Timeout or
        DownloadErrorKind.RateLimited or DownloadErrorKind.ExpiredStreamUrl or
        DownloadErrorKind.FileInUse or DownloadErrorKind.MuxFailed or
        DownloadErrorKind.Unknown;

    /// <summary>
    /// True when the cause sits outside the app and retrying cannot help, so the queue should
    /// not burn attempts on it.
    /// </summary>
    public bool IsPermanent => Kind is
        DownloadErrorKind.VideoRemoved or DownloadErrorKind.PrivateVideo or
        DownloadErrorKind.DrmProtected or DownloadErrorKind.MembersOnly or
        DownloadErrorKind.InvalidUrl or DownloadErrorKind.UnsupportedUrl;
}

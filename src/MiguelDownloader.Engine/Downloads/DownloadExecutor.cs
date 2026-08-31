using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.FFmpeg;
using MiguelDownloader.Engine.Files;
using MiguelDownloader.Engine.Music;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Downloads;

/// <summary>What a finished job produced.</summary>
public sealed record DownloadOutcome
{
    public required DownloadStatus Status { get; init; }
    public string? FilePath { get; init; }
    public long? FileSizeBytes { get; init; }
    public DownloadError? Error { get; init; }

    /// <summary>Subtitle and cover files written next to the media.</summary>
    public IReadOnlyList<string> SideFiles { get; init; } = [];

    public static DownloadOutcome Failed(DownloadError error)
        => new() { Status = DownloadStatus.Failed, Error = error };
}

/// <summary>
/// Runs one download from start to validated file.
/// <para>
/// The pipeline is deliberately linear and every stage reports itself, because the slow parts
/// are not always the transfer: a long video can spend longer being remuxed than downloaded, and
/// a user watching a progress bar deserves to know which is happening.
/// </para>
/// <para>
/// Nothing is reported as complete until the file has been probed and found to contain what was
/// asked for. A job that fails leaves its working directory intact when the partial data could
/// still be resumed, and removes it when it could not.
/// </para>
/// </summary>
public sealed class DownloadExecutor(
    ProcessRunner runner,
    MediaAnalyzer analyzer,
    MediaProbe probe,
    WorkspaceManager workspace,
    MusicTagger tagger,
    ILogger<DownloadExecutor> logger)
{
    private readonly ProcessRunner _runner = runner;
    private readonly MediaAnalyzer _analyzer = analyzer;
    private readonly MediaProbe _probe = probe;
    private readonly WorkspaceManager _workspace = workspace;
    private readonly MusicTagger _tagger = tagger;
    private readonly ILogger<DownloadExecutor> _logger = logger;

    /// <summary>
    /// Executes one task.
    /// </summary>
    /// <param name="task">The queued job. Its status and progress are updated as work proceeds.</param>
    /// <param name="tools">Resolved tool paths.</param>
    /// <param name="settings">Current settings.</param>
    /// <param name="onProgress">Called whenever progress or stage changes.</param>
    /// <param name="cancellationToken">Cancels the job and terminates the tool process.</param>
    public async Task<DownloadOutcome> ExecuteAsync(
        DownloadTask task,
        ToolPaths tools,
        AppSettings settings,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(settings);

        var request = task.Request;

        try
        {
            if (!tools.CanDownload)
                return DownloadOutcome.Failed(Missing(DownloadErrorKind.YtDlpMissing, "Error_YtDlpMissing"));

            // --- 1. Make sure we have real formats for this item -------------------------------
            Report(onProgress, DownloadProgress.ForStage(DownloadStage.Analyzing));

            var item = request.Item;
            if (item.IsStub || item.Formats.Count == 0)
            {
                item = await _analyzer
                    .GetItemDetailsAsync(request.Url, tools, settings.Advanced, cancellationToken)
                    .ConfigureAwait(false);
            }

            // --- 2. Resolve the exact streams -------------------------------------------------
            var resolved = request.Mode is DownloadMode.Audio
                ? FormatSelector.ResolveAudioOnly(item, request.Selection)
                : FormatSelector.ResolveVideo(item, request.Selection);

            if (!resolved.HasAnything)
                return DownloadOutcome.Failed(Missing(DownloadErrorKind.NoFormatsFound, "Error_NoFormatsFound"));

            // Merging separate streams needs ffmpeg; a single pre-muxed stream does not.
            var needsFfmpeg = request.Mode is DownloadMode.Audio
                ? request.AudioFormat != AudioOutputFormat.KeepOriginal
                : resolved.Video is not null && resolved.Audio.Count > 0;

            if (needsFfmpeg && !tools.CanProcess)
                return DownloadOutcome.Failed(Missing(DownloadErrorKind.FfmpegMissing, "Error_FfmpegMissing"));

            var plan = request.Mode is DownloadMode.Audio
                ? null
                : MuxPlanner.Plan(
                    resolved.Video, resolved.Audio, request.Container,
                    request.Subtitles is { Enabled: true, Embed: true }, request.EmbedThumbnail);

            // --- 3. Refuse early if the disk clearly cannot hold it ----------------------------
            if (settings.Downloads.CheckDiskSpace)
            {
                var check = DiskSpace.Check(request.TargetDirectory, resolved.EstimatedSize);
                if (!check.IsSufficient)
                {
                    _logger.LogWarning("Not enough space for {Title}: need {Required}, have {Available}",
                        item.Title, check.RequiredBytes, check.AvailableBytes);
                    return DownloadOutcome.Failed(new DownloadError
                    {
                        Kind = DownloadErrorKind.DiskFull,
                        MessageKey = "Error_DiskFullPredicted",
                        Action = RecommendedAction.FreeDiskSpace,
                        TechnicalDetails =
                            $"Required ~{check.RequiredBytes:N0} bytes, available {check.AvailableBytes:N0}.",
                    });
                }
            }

            // --- 4. Decide the destination ----------------------------------------------------
            var destination = _workspace.PlanDestination(
                request.TargetDirectory, request.TargetFileName, request.ExistingFilePolicy);

            if (destination.Outcome == CollisionOutcome.Skip)
            {
                var existing = Path.Combine(request.TargetDirectory, destination.FileName);
                _logger.LogInformation("Skipping {Title}; the file already exists", item.Title);
                return new DownloadOutcome
                {
                    Status = DownloadStatus.Skipped,
                    FilePath = existing,
                    FileSizeBytes = File.Exists(existing) ? new FileInfo(existing).Length : null,
                };
            }

            if (destination.Outcome == CollisionOutcome.NeedsUserDecision)
            {
                // Not a failure: the queue prompts and resubmits the job with an explicit policy.
                // Nothing has been written at this point, so no cleanup is needed.
                return DownloadOutcome.Failed(new DownloadError
                {
                    Kind = DownloadErrorKind.FileAlreadyExists,
                    MessageKey = "Error_FileExists",
                    Action = RecommendedAction.None,
                    TechnicalDetails = destination.FileName,
                });
            }

            // --- 5. Download into a private workspace -----------------------------------------
            var workspacePath = task.WorkingDirectory
                ?? _workspace.CreateWorkspace(task.Id, settings.Downloads);
            task.WorkingDirectory = workspacePath;

            var stem = Path.GetFileNameWithoutExtension(destination.FileName);
            var effectiveAdvanced = tools.ApplyExecutionPolicy(settings.Advanced);

            var arguments = YtDlpArguments.ForDownload(
                request with { Item = item }, resolved, plan,
                workspacePath, stem, settings.Downloads, effectiveAdvanced, tools.JsRuntime.Path,
                tools.UseLgplTranscodeEncoders);

            var tracker = new StageTracker(resolved, onProgress);

            var result = await _runner.RunAsync(
                tools.YtDlp.Path!,
                arguments,
                onLine: line => tracker.Consume(line),
                captureStandardOutput: false,
                workingDirectory: workspacePath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Succeeded)
            {
                var error = ErrorClassifier.Classify(result.StandardError);

                // Keep the raw text when nothing was recognised; otherwise the log records that a
                // download failed and nothing about why.
                if (error.Kind == DownloadErrorKind.Unknown)
                {
                    _logger.LogWarning(
                        "Download of {Title} failed, cause not recognised. Tool output: {Output}",
                        item.Title, TruncateForLog(result.StandardError));
                }
                else
                {
                    _logger.LogWarning("Download of {Title} failed: {Cause}", item.Title, error.Kind);
                }

                // Partial data is only worth keeping when resuming could actually use it.
                if (!error.IsRetryable) _workspace.CleanWorkspace(workspacePath);
                return DownloadOutcome.Failed(error);
            }

            // --- 6. Find and validate what was produced ---------------------------------------
            Report(onProgress, DownloadProgress.ForStage(DownloadStage.Validating));

            var produced = _workspace.FindProducedMedia(workspacePath);
            if (produced is null)
            {
                _logger.LogError("The downloader reported success but produced no media file in {Path}", workspacePath);
                return DownloadOutcome.Failed(new DownloadError
                {
                    Kind = DownloadErrorKind.ValidationFailed,
                    MessageKey = "Error_NoOutputProduced",
                    Action = RecommendedAction.Retry,
                    TechnicalDetails = result.StandardError,
                });
            }

            if (tools.CanValidate)
            {
                var validationError = await ValidateAsync(
                    produced, item, request, resolved, tools, cancellationToken).ConfigureAwait(false);
                if (validationError is not null) return DownloadOutcome.Failed(validationError);
            }

            // --- 7. Music tags ----------------------------------------------------------------
            if (request.Mode is DownloadMode.Audio && request.Music is not null && settings.Music.WriteMetadata)
            {
                Report(onProgress, DownloadProgress.ForStage(DownloadStage.WritingMetadata));
                var cover = settings.Music.EmbedCoverArt
                    ? _workspace.FindImageFiles(workspacePath).FirstOrDefault()
                    : null;

                _tagger.Apply(produced, request.Music, cover);
            }

            // --- 8. Move into place -----------------------------------------------------------
            Report(onProgress, DownloadProgress.ForStage(DownloadStage.Finalizing));

            // The real extension comes from what was produced, which can differ from the guess
            // when the source turned out to use a different container.
            var finalName = Path.GetFileNameWithoutExtension(destination.FileName) + Path.GetExtension(produced);
            finalName = Core.Naming.FileNameSanitizer.EnsurePathFits(request.TargetDirectory, finalName);

            if (destination.Outcome == CollisionOutcome.NoCollision ||
                destination.Outcome == CollisionOutcome.Renamed)
            {
                // Re-check: the extension may have changed the name since the plan was made.
                finalName = Core.Naming.FileNameSanitizer.MakeUnique(
                    request.TargetDirectory, finalName, File.Exists);
            }

            var finalPath = await _workspace.MoveIntoPlaceAsync(
                produced, request.TargetDirectory, finalName,
                overwrite: destination.Outcome == CollisionOutcome.Overwrite,
                cancellationToken).ConfigureAwait(false);

            var sideFiles = await MoveSideFilesAsync(
                workspacePath, request, Path.GetFileNameWithoutExtension(finalName), cancellationToken)
                .ConfigureAwait(false);

            _workspace.CleanWorkspace(workspacePath);
            task.WorkingDirectory = null;

            var size = new FileInfo(finalPath).Length;
            _logger.LogInformation("Completed {Title} -> {Size:N0} bytes", item.Title, size);

            return new DownloadOutcome
            {
                Status = DownloadStatus.Completed,
                FilePath = finalPath,
                FileSizeBytes = size,
                SideFiles = sideFiles,
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cancelled {Title}", task.Title);
            // The workspace is kept so the transfer can resume where it stopped.
            return new DownloadOutcome
            {
                Status = DownloadStatus.Cancelled,
                Error = new DownloadError
                {
                    Kind = DownloadErrorKind.Cancelled,
                    MessageKey = "Error_Cancelled",
                },
            };
        }
        catch (AnalysisException ex)
        {
            return DownloadOutcome.Failed(ex.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure downloading {Title}", task.Title);
            return DownloadOutcome.Failed(ErrorClassifier.Classify(ex));
        }
    }

    private async Task<DownloadError?> ValidateAsync(
        string filePath, MediaItem item, DownloadRequest request, ResolvedFormats resolved,
        ToolPaths tools, CancellationToken cancellationToken)
    {
        var probeResult = await _probe
            .ProbeAsync(tools.Ffprobe.Path!, filePath, cancellationToken).ConfigureAwait(false);

        var expectation = new ProbeExpectation
        {
            ExpectVideo = request.Mode is not DownloadMode.Audio && resolved.Video is not null,
            ExpectAudio = resolved.Audio.Count > 0 || resolved.UsesMuxedSource,
            ExpectedDuration = item.Duration,
            // Live recordings and clipped uploads legitimately differ from the advertised length,
            // so the tolerance is generous enough not to reject a good file.
            DurationTolerancePercent = item.IsLive ? 100 : 8,
            MinimumAudioStreams = request.Mode is DownloadMode.Audio ? 1 : Math.Max(1, resolved.Audio.Count),
        };

        var problem = MediaProbe.Validate(probeResult, expectation);
        if (problem is null) return null;

        _logger.LogError("Validation failed for {File}: {Problem}", Path.GetFileName(filePath), problem);

        return new DownloadError
        {
            Kind = DownloadErrorKind.ValidationFailed,
            MessageKey = problem switch
            {
                "missing-video-stream" => "Error_ValidationNoVideo",
                "missing-audio-stream" => "Error_ValidationNoAudio",
                "missing-audio-tracks" => "Error_ValidationMissingTracks",
                "duration-mismatch" => "Error_ValidationTruncated",
                "empty" => "Error_ValidationEmpty",
                _ => "Error_ValidationUnreadable",
            },
            Action = RecommendedAction.Retry,
            TechnicalDetails =
                $"probe: readable={probeResult.IsReadable}, duration={probeResult.Duration}, " +
                $"video={probeResult.HasVideoStream}, audio={probeResult.AudioStreamCount}, " +
                $"expected duration={item.Duration}",
        };
    }

    /// <summary>Moves subtitle and cover files next to the media, renaming them to match it.</summary>
    private async Task<List<string>> MoveSideFilesAsync(
        string workspacePath, DownloadRequest request, string finalStem, CancellationToken cancellationToken)
    {
        var moved = new List<string>();

        var wanted = new List<string>();
        if (request.Subtitles is { Enabled: true, KeepFiles: true })
            wanted.AddRange(_workspace.FindSubtitleFiles(workspacePath));
        if (request.WriteThumbnailFile)
            wanted.AddRange(_workspace.FindImageFiles(workspacePath));

        foreach (var file in wanted)
        {
            try
            {
                // Subtitles carry a language suffix such as ".pt.srt" that must be preserved.
                var original = Path.GetFileName(file);
                var suffix = original.Length > finalStem.Length && original.StartsWith(finalStem, StringComparison.Ordinal)
                    ? original[finalStem.Length..]
                    : Path.GetExtension(file);

                var targetName = Core.Naming.FileNameSanitizer.EnsurePathFits(
                    request.TargetDirectory, finalStem + suffix);

                var path = await _workspace.MoveIntoPlaceAsync(
                    file, request.TargetDirectory, targetName, overwrite: true, cancellationToken)
                    .ConfigureAwait(false);

                moved.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A sidecar that cannot be moved is not worth failing the whole download over.
                _logger.LogWarning(ex, "Could not move the side file {File}", Path.GetFileName(file));
            }
        }

        return moved;
    }

    private static DownloadError Missing(DownloadErrorKind kind, string key) => new()
    {
        Kind = kind,
        MessageKey = key,
        Action = RecommendedAction.InstallTools,
    };

    /// <summary>
    /// Keeps an unrecognised failure readable in the log without letting a runaway tool dump
    /// megabytes into it. The full text still reaches the diagnostics bundle.
    /// </summary>
    private static string TruncateForLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no output)";
        var trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[..2000] + "... (truncated)";
    }

    private static void Report(Action<DownloadProgress>? onProgress, DownloadProgress progress)
        => onProgress?.Invoke(progress);
}

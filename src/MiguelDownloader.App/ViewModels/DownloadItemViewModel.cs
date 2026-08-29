using MiguelDownloader.App.Converters;
using MiguelDownloader.App.Localization;
using MiguelDownloader.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace MiguelDownloader.App.ViewModels;

/// <summary>
/// One row in the download queue.
/// <para>
/// Wraps a <see cref="DownloadTask"/> and exposes it as bindable properties. The task itself is
/// mutated by the engine on background threads, so this is refreshed from the UI thread whenever
/// the queue reports a change rather than binding to the task directly.
/// </para>
/// </summary>
public sealed partial class DownloadItemViewModel(DownloadTask task) : ObservableObject
{
    public DownloadTask Task { get; } = task;

    public string Id => Task.Id;
    public string Title => Task.Title;
    public string Author => Task.Request.Item.DisplayAuthor;
    public string Url => Task.Request.Url;
    public string? ThumbnailUrl => Task.Request.Item.BestThumbnail?.Url;
    public string? FilePath => Task.ResultPath;

    [ObservableProperty]
    private DownloadStatus _status;

    [ObservableProperty]
    private DownloadStage _stage;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    /// <summary>
    /// The same figure as <see cref="SpeedText"/>, unformatted, so the status bar can add the
    /// running transfers together. Zero when this item is not moving bytes.
    /// </summary>
    [ObservableProperty]
    private double _speedBytesPerSecond;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorAdvice;

    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _hasFile;

    /// <summary>Short description of what was asked for, e.g. "MP4 · 1080p" or "Áudio · MP3".</summary>
    public string FormatSummary
    {
        get
        {
            var request = Task.Request;
            if (request.Mode == DownloadMode.Audio)
            {
                var format = request.AudioFormat == Core.Settings.AudioOutputFormat.KeepOriginal
                    ? Loc.Get("Audio_KeepOriginal")
                    : request.AudioFormat.ToString().ToUpperInvariant();
                return $"{Loc.Get("Mode_Audio")} · {format}";
            }

            var container = request.Container == Core.Formats.ContainerFormat.Auto
                ? Loc.Get("Container_Auto")
                : request.Container.ToString().ToUpperInvariant();

            var quality = request.Selection.MaxHeight is { } height
                ? $"{height}p"
                : Loc.ForEnum("Quality", request.Selection.Tier);

            return $"{container} · {quality}";
        }
    }

    /// <summary>Pulls the current state out of the task. Must run on the UI thread.</summary>
    public void Refresh()
    {
        var task = Task;
        var progress = task.Progress;

        Status = task.Status;
        Stage = task.Stage;

        CanPause = task.CanPause;
        CanResume = task.CanResume;
        CanCancel = task.CanCancel;
        CanRetry = task.CanRetry;
        HasFile = task.Status == DownloadStatus.Completed &&
                  task.ResultPath is { Length: > 0 } && File.Exists(task.ResultPath);

        StatusText = BuildStatusText(task);

        if (task.Status is DownloadStatus.Running)
        {
            var fraction = progress.OverallFraction;
            // An unknown total is shown as an indeterminate bar rather than a fabricated number.
            IsIndeterminate = fraction is null;
            ProgressValue = (fraction ?? 0) * 100;

            SizeText = BuildSizeText(progress);
            SpeedBytesPerSecond = progress.SpeedBytesPerSecond ?? 0;
            SpeedText = progress.SpeedBytesPerSecond is > 0
                ? ByteSizeConverter.Format((long)progress.SpeedBytesPerSecond.Value) + "/s"
                : string.Empty;
            EtaText = progress.Eta is { TotalSeconds: > 0 } eta ? TimeSpanDisplayConverter.Format(eta) : string.Empty;
        }
        else
        {
            IsIndeterminate = false;
            ProgressValue = task.Status == DownloadStatus.Completed ? 100 : ProgressValue;
            SpeedText = string.Empty;
            EtaText = string.Empty;

            SizeText = task.Status == DownloadStatus.Completed && task.ResultSizeBytes is { } size
                ? ByteSizeConverter.Format(size)
                : string.Empty;
        }

        ErrorMessage = task.Error is { } error ? Loc.Get(error.MessageKey) : null;
        ErrorAdvice = task.Error is { Action: not Core.Errors.RecommendedAction.None } advice
            ? Loc.ForEnum("Recommend", advice.Action)
            : null;

        OnPropertyChanged(nameof(FilePath));
    }

    private static string BuildStatusText(DownloadTask task)
    {
        // While running, the stage is the useful information; "running" alone says nothing about
        // whether the app is fetching bytes or re-encoding.
        if (task.Status == DownloadStatus.Running && task.Stage != DownloadStage.None)
            return Loc.ForEnum("Stage", task.Stage);

        return Loc.ForEnum("Status", task.Status);
    }

    private static string BuildSizeText(DownloadProgress progress)
    {
        if (progress.DownloadedBytes is not { } downloaded) return string.Empty;

        var received = ByteSizeConverter.Format(downloaded);
        if (progress.TotalBytes is not { } total || total <= 0) return received;

        var totalText = ByteSizeConverter.Format(total);
        // An estimated total is marked, so the number is not mistaken for a measured one.
        return progress.TotalIsEstimate
            ? $"{received} / ~{totalText}"
            : $"{received} / {totalText}";
    }
}

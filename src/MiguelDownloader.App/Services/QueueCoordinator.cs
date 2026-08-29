using MiguelDownloader.App.Localization;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Data;
using MiguelDownloader.Engine.Downloads;
using MiguelDownloader.Engine.Files;
using Microsoft.Extensions.Logging;
using System.IO;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Connects the download queue to the things that must happen around it: persistence, history,
/// notifications and taskbar progress.
/// <para>
/// Keeping this out of the queue itself means the engine stays free of storage and presentation
/// concerns, and can be tested without either.
/// </para>
/// </summary>
public sealed class QueueCoordinator(
    DownloadQueue queue,
    QueueRepository queueRepository,
    HistoryRepository history,
    NotificationService notifications,
    ToolService tools,
    SettingsService settings,
    WorkspaceManager workspace,
    DialogService dialogs,
    ILogger<QueueCoordinator> logger)
{
    private readonly DownloadQueue _queue = queue;
    private readonly QueueRepository _queueRepository = queueRepository;
    private readonly HistoryRepository _history = history;
    private readonly NotificationService _notifications = notifications;
    private readonly ToolService _tools = tools;
    private readonly SettingsService _settings = settings;
    private readonly WorkspaceManager _workspace = workspace;
    private readonly DialogService _dialogs = dialogs;
    private readonly ILogger<QueueCoordinator> _logger = logger;

    /// <summary>
    /// How often pending changes are written to storage.
    /// <para>
    /// Progress is only needed if the application stops unexpectedly, so writing it as it
    /// arrives would be pure waste: a fast download reports many times a second, and queueing a
    /// thousand-track playlist would otherwise commit a thousand separate transactions before
    /// the interface came back. Changes are collected and flushed together instead.
    /// </para>
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    /// <summary>Tasks changed since the last flush, keyed by id so repeats collapse.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadTask> _dirty = new();

    private Timer? _flushTimer;

    /// <summary>Guards against a slow flush overlapping the next tick.</summary>
    private int _flushing;

    public DownloadQueue Queue => _queue;

    /// <summary>Restores the persisted queue and starts anything still pending.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _queue.Configure(() => _settings.Current, () => _tools.Current);

        _queue.TaskChanged += OnTaskChanged;
        _queue.TaskFinished += OnTaskFinished;

        try
        {
            var restored = await _queueRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            var pending = restored.Where(t => !t.IsFinished).ToList();

            if (pending.Count > 0)
            {
                _queue.Restore(pending);
                _logger.LogInformation("Restored {Count} pending downloads", pending.Count);
            }

            // Clear working folders that no restored task owns; they are left by a crash or a
            // forced close and would otherwise accumulate silently.
            var live = pending.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _workspace.SweepOrphans(live, _settings.Current.Downloads);

            await _queueRepository.RemoveFinishedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A damaged queue must not stop the application from opening.
            _logger.LogError(ex, "Could not restore the persisted queue");
        }

        // Start the batched writer only once the queue is ready, so nothing flushes mid-restore.
        _flushTimer = new Timer(_ => _ = FlushDirtyAsync(), null, FlushInterval, FlushInterval);

        await _queue.StartAsync().ConfigureAwait(false);
    }

    private void OnTaskChanged(object? sender, DownloadTaskEventArgs e)
    {
        UpdateTaskbar();

        // Terminal states are persisted by the finished handler, which also writes history.
        if (e.Task.IsFinished) return;

        // Mark it dirty and let the timer write it with everything else that changed.
        _dirty[e.Task.Id] = e.Task;
    }

    /// <summary>
    /// Writes everything that changed since the last flush, in a single transaction.
    /// </summary>
    private async Task FlushDirtyAsync()
    {
        // A previous flush still running means there is nothing useful to do: the next tick
        // will pick up whatever accumulates meanwhile.
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;

        try
        {
            if (_dirty.IsEmpty) return;

            // Snapshot and clear, so changes arriving during the write are not lost: they simply
            // land in the next batch.
            var batch = new List<DownloadTask>(_dirty.Count);
            foreach (var key in _dirty.Keys)
            {
                if (_dirty.TryRemove(key, out var task)) batch.Add(task);
            }

            if (batch.Count == 0) return;

            await _queueRepository.SaveManyAsync(batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failing to persist costs a restored item after a crash; it must never break a download.
            _logger.LogWarning(ex, "Could not persist queue changes");
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private void OnTaskFinished(object? sender, DownloadTaskEventArgs e)
    {
        _ = FinishAsync(e.Task);
    }

    private async Task FinishAsync(DownloadTask task)
    {
        // A finished task is written here with its final state; any pending dirty copy is stale.
        _dirty.TryRemove(task.Id, out _);

        // A name collision is a question, not a failure. Ask, then resubmit with the answer
        // instead of recording a failed download the user never actually caused.
        if (task.Error?.Kind == DownloadErrorKind.FileAlreadyExists)
        {
            await ResolveFileConflictAsync(task).ConfigureAwait(false);
            return;
        }

        try
        {
            await _queueRepository.RemoveAsync(task.Id).ConfigureAwait(false);

            // Cancelled downloads are not history: nothing was produced and the user chose it.
            if (task.Status is not DownloadStatus.Cancelled)
                await WriteHistoryAsync(task).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the finished task {Id}", task.Id);
        }

        switch (task.Status)
        {
            case DownloadStatus.Completed:
                _notifications.NotifyDownloadCompleted(task.Title);
                break;

            case DownloadStatus.Failed:
                _notifications.NotifyDownloadFailed(
                    task.Title, Loc.Get(task.Error?.MessageKey ?? "Error_Unknown"));
                break;
        }

        UpdateTaskbar();
    }

    private async Task WriteHistoryAsync(DownloadTask task)
    {
        var item = task.Request.Item;

        var entry = new HistoryEntry
        {
            Id = task.Id,
            SourceId = item.Id,
            Url = task.Request.Url,
            Title = item.Title,
            Author = item.DisplayAuthor,
            Kind = item.Kind,
            Mode = task.Request.Mode,
            ThumbnailUrl = item.BestThumbnail?.Url,
            FilePath = task.ResultPath,
            FileSize = task.ResultSizeBytes,
            Container = task.ResultPath is { Length: > 0 }
                ? Path.GetExtension(task.ResultPath).TrimStart('.').ToUpperInvariant()
                : null,
            QualityLabel = DescribeQuality(task),
            Duration = item.Duration,
            Status = task.Status,
            ErrorKind = task.Error?.Kind,
            CompletedAt = task.FinishedAt ?? DateTimeOffset.Now,
            CollectionTitle = task.Request.CollectionTitle,
        };

        await _history.AddAsync(entry).ConfigureAwait(false);
    }

    /// <summary>
    /// Describes what was actually downloaded. Deliberately built from the resolved selection
    /// rather than from what was requested, so history never claims a quality that was not
    /// available at the time.
    /// </summary>
    private static string? DescribeQuality(DownloadTask task)
    {
        if (task.Request.Mode == DownloadMode.Audio)
            return task.Request.AudioFormat.ToString();

        var selection = task.Request.Selection;
        if (selection.MaxHeight is { } height) return $"{height}p";
        return selection.Tier.ToString();
    }

    /// <summary>Aggregates queue progress onto the taskbar button.</summary>
    private void UpdateTaskbar()
    {
        var active = _queue.Tasks.Where(t => t.Status is DownloadStatus.Running).ToList();

        if (active.Count == 0)
        {
            var anyFailed = _queue.Tasks.Any(t => t.Status == DownloadStatus.Failed);
            _notifications.SetTaskbarProgress(null, anyFailed);
            return;
        }

        var fractions = active
            .Select(t => t.Progress.OverallFraction)
            .Where(f => f is not null)
            .Select(f => f!.Value)
            .ToList();

        _notifications.SetTaskbarProgress(fractions.Count > 0 ? fractions.Average() : 0);
    }

    /// <summary>
    /// Asks what to do about an existing file and resubmits the job with the answer.
    /// <para>
    /// The prompt must run on the UI thread, while this is reached from a queue worker, so the
    /// call is marshalled across. Cancelling the prompt cancels that one job and leaves the rest
    /// of the queue alone.
    /// </para>
    /// </summary>
    private async Task ResolveFileConflictAsync(DownloadTask task)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("No dispatcher available to ask about the existing file");
            return;
        }

        var fileName = task.Error?.TechnicalDetails ?? task.Request.TargetFileName;

        var choice = await dispatcher
            .InvokeAsync(() => _dialogs.AskFileConflict(fileName))
            .Task
            .ConfigureAwait(false);

        if (choice == FileConflictChoice.Cancel)
        {
            task.Status = DownloadStatus.Cancelled;
            task.Error = null;
            _queue.Remove(task.Id);
            return;
        }

        var policy = DialogService.ToPolicy(choice);
        _queue.RequeueWith(task.Id, request => request with { ExistingFilePolicy = policy });
    }

    /// <summary>
    /// Persists everything still in flight. Called when the window is closing, so the whole
    /// unfinished queue is written rather than only what happened to be dirty.
    /// </summary>
    public async Task FlushAsync()
    {
        _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        try
        {
            var pending = _queue.Tasks.Where(t => !t.IsFinished).ToList();
            if (pending.Count > 0)
                await _queueRepository.SaveManyAsync(pending).ConfigureAwait(false);

            _dirty.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the queue while closing");
        }
        finally
        {
            // Null when the window closes before initialisation finished, which is exactly when
            // a dereference here would turn a fast exit into a crash.
            if (_flushTimer is { } timer)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
                _flushTimer = null;
            }
        }
    }
}

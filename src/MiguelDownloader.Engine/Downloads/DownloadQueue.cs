using System.Collections.Concurrent;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Downloads;

/// <summary>Raised whenever a task changes, so the UI and the store can react.</summary>
public sealed class DownloadTaskEventArgs(DownloadTask task) : EventArgs
{
    public DownloadTask Task { get; } = task;
}

/// <summary>
/// Runs the queue.
/// <para>
/// Jobs run up to a configured concurrency limit, in the order they were added. Each job owns a
/// cancellation source, so cancelling one never disturbs the others.
/// </para>
/// <para>
/// Pausing works by stopping the tool and keeping the partial data. The downloader has no pause
/// signal, but it resumes from a partial file on its next run, so a paused job continues from
/// roughly where it stopped instead of starting over. That is the honest mechanism, and it is why
/// pausing is offered rather than a suspend that could not actually be delivered.
/// </para>
/// </summary>
public sealed class DownloadQueue : IAsyncDisposable, IDisposable
{
    private readonly DownloadExecutor _executor;
    private readonly ILogger<DownloadQueue> _logger;

    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>Guards the scheduling decision so two pumps cannot start the same task.</summary>
    private readonly SemaphoreSlim _pumpGate = new(1, 1);

    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

    private Func<AppSettings> _settingsProvider = () => new AppSettings();
    private Func<ToolPaths?> _toolsProvider = () => null;

    public DownloadQueue(DownloadExecutor executor, ILogger<DownloadQueue> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>Raised when a task is added, changes state, or finishes.</summary>
    public event EventHandler<DownloadTaskEventArgs>? TaskChanged;

    /// <summary>Raised when a task reaches a terminal state, for history and notifications.</summary>
    public event EventHandler<DownloadTaskEventArgs>? TaskFinished;

    /// <summary>Supplies current settings and tools. Set once during startup.</summary>
    public void Configure(Func<AppSettings> settings, Func<ToolPaths?> tools)
    {
        _settingsProvider = settings;
        _toolsProvider = tools;
    }

    /// <summary>Every task, in queue order.</summary>
    public IReadOnlyList<DownloadTask> Tasks =>
        _tasks.Values.OrderBy(t => t.Order).ThenBy(t => t.CreatedAt).ToList();

    public int ActiveCount => _running.Count;
    public int PendingCount => _tasks.Values.Count(t => t.Status == DownloadStatus.Queued);

    /// <summary>Adds a task and starts it as soon as a slot is free.</summary>
    public void Enqueue(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);

        task.Order = task.Order != 0
            ? task.Order
            : (_tasks.IsEmpty ? 1 : _tasks.Values.Max(t => t.Order) + 1);

        if (!_tasks.TryAdd(task.Id, task))
        {
            _logger.LogWarning("A task with id {Id} is already queued", task.Id);
            return;
        }

        Notify(task);
        _ = PumpAsync();
    }

    /// <summary>Adds several tasks, preserving their order, and pumps once at the end.</summary>
    public void EnqueueRange(IEnumerable<DownloadTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var next = _tasks.IsEmpty ? 1 : _tasks.Values.Max(t => t.Order) + 1;
        foreach (var task in tasks)
        {
            if (task.Order == 0) task.Order = next++;
            if (_tasks.TryAdd(task.Id, task)) Notify(task);
        }

        _ = PumpAsync();
    }

    /// <summary>
    /// Restores tasks read from storage after a restart. Anything that had been running is put
    /// back into the queue, because its process did not survive the restart.
    /// </summary>
    public void Restore(IEnumerable<DownloadTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        foreach (var task in tasks)
        {
            if (task.Status is DownloadStatus.Running)
            {
                task.Status = DownloadStatus.Queued;
                task.Stage = DownloadStage.None;
                task.Progress = new DownloadProgress();
            }
            _tasks.TryAdd(task.Id, task);
        }
    }

    /// <summary>Starts anything that is queued. Call once the tools have been resolved.</summary>
    public Task StartAsync() => PumpAsync();

    public DownloadTask? Find(string id) => _tasks.GetValueOrDefault(id);

    /// <summary>Cancels a running or queued task.</summary>
    public void Cancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var task)) return;

        if (_running.TryGetValue(id, out var cts))
        {
            // The executor treats cancellation as "paused-like" and keeps the partial data, so it
            // is cleared explicitly here: the user asked to cancel, not to pause.
            task.Status = DownloadStatus.Cancelled;
            SafeCancel(cts);
            return;
        }

        if (task.Status is DownloadStatus.Queued or DownloadStatus.Paused)
        {
            task.Status = DownloadStatus.Cancelled;
            task.FinishedAt = DateTimeOffset.Now;
            Notify(task);
            Finish(task);
        }
    }

    /// <summary>Stops a running task, keeping its partial data so it can continue later.</summary>
    public void Pause(string id)
    {
        if (!_tasks.TryGetValue(id, out var task)) return;
        if (task.Status is not (DownloadStatus.Running or DownloadStatus.Queued)) return;

        task.Status = DownloadStatus.Paused;

        if (_running.TryGetValue(id, out var cts)) SafeCancel(cts);
        else Notify(task);
    }

    /// <summary>Puts a paused task back in the queue; the tool resumes from the partial file.</summary>
    public void Resume(string id)
    {
        if (!_tasks.TryGetValue(id, out var task)) return;
        if (task.Status is not DownloadStatus.Paused) return;

        task.Status = DownloadStatus.Queued;
        task.Error = null;
        Notify(task);
        _ = PumpAsync();
    }

    /// <summary>Retries a failed or cancelled task, resetting the automatic attempt counter.</summary>
    public void Retry(string id)
    {
        if (!_tasks.TryGetValue(id, out var task)) return;
        if (!task.CanRetry) return;

        task.Status = DownloadStatus.Queued;
        task.Stage = DownloadStage.None;
        task.Progress = new DownloadProgress();
        task.Error = null;
        task.AttemptCount = 0;
        task.FinishedAt = null;
        Notify(task);
        _ = PumpAsync();
    }

    /// <summary>
    /// Requeues a task with an adjusted request, keeping its identity and position.
    /// <para>
    /// Used when a job needs an answer before it can proceed, such as deciding what to do about an
    /// existing file. The request is immutable, so a new task is substituted in place rather than
    /// the original being edited underneath whatever else may be reading it.
    /// </para>
    /// </summary>
    public void RequeueWith(string id, Func<DownloadRequest, DownloadRequest> adjust)
    {
        ArgumentNullException.ThrowIfNull(adjust);

        if (!_tasks.TryGetValue(id, out var existing)) return;
        if (existing.IsActive) return;

        var replacement = new DownloadTask
        {
            Id = existing.Id,
            Request = adjust(existing.Request),
            Order = existing.Order,
            CreatedAt = existing.CreatedAt,
            Status = DownloadStatus.Queued,
            Stage = DownloadStage.None,
            WorkingDirectory = existing.WorkingDirectory,
            // The attempt counter is deliberately reset: answering a prompt is a fresh start, not
            // another failed attempt against the retry budget.
            AttemptCount = 0,
        };

        _tasks[id] = replacement;
        Notify(replacement);
        _ = PumpAsync();
    }

    /// <summary>Removes a task, cancelling it first when it is still running.</summary>
    public void Remove(string id)
    {
        if (_running.ContainsKey(id)) Cancel(id);

        if (_tasks.TryRemove(id, out var task))
        {
            Notify(task);
            _ = PumpAsync();
        }
    }

    /// <summary>Removes every finished task from the queue view.</summary>
    public void ClearFinished()
    {
        foreach (var task in _tasks.Values.Where(t => t.IsFinished).ToList())
            _tasks.TryRemove(task.Id, out _);
    }

    /// <summary>
    /// Starts as many queued tasks as the concurrency limit allows.
    /// <para>
    /// The gate makes the decision single-threaded, so two concurrent calls cannot both see the
    /// same free slot and start two jobs into it.
    /// </para>
    /// </summary>
    private async Task PumpAsync()
    {
        if (_disposed || _shutdown.IsCancellationRequested) return;

        try
        {
            await _pumpGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and the wait; there is nothing left to schedule.
            return;
        }

        try
        {
            var settings = _settingsProvider();
            var limit = Math.Clamp(settings.Downloads.MaxConcurrentDownloads, 1, 8);

            var tools = _toolsProvider();
            if (tools is null || !tools.CanDownload) return;

            while (_running.Count < limit)
            {
                var next = _tasks.Values
                    .Where(t => t.Status == DownloadStatus.Queued)
                    .OrderBy(t => t.Order)
                    .ThenBy(t => t.CreatedAt)
                    .FirstOrDefault();

                if (next is null) break;

                // Claim the slot before awaiting anything, so the loop condition stays accurate.
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                if (!_running.TryAdd(next.Id, cts))
                {
                    cts.Dispose();
                    break;
                }

                next.Status = DownloadStatus.Running;
                next.StartedAt ??= DateTimeOffset.Now;
                next.AttemptCount++;
                Notify(next);

                _ = RunTaskAsync(next, tools, settings, cts);
            }
        }
        catch (Exception ex)
        {
            // Scheduling is the heart of the queue: letting an exception escape here would leave
            // every pending download stuck with no indication why.
            _logger.LogError(ex, "The download scheduler failed to start pending work");
        }
        finally
        {
            try { _pumpGate.Release(); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
    }

    private async Task RunTaskAsync(
        DownloadTask task, ToolPaths tools, AppSettings settings, CancellationTokenSource cts)
    {
        try
        {
            var outcome = await _executor.ExecuteAsync(
                task, tools, settings,
                onProgress: progress =>
                {
                    task.Progress = progress;
                    task.Stage = progress.Stage;
                    Notify(task);
                },
                cts.Token).ConfigureAwait(false);

            ApplyOutcome(task, outcome, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {Id} failed outside the executor", task.Id);
            task.Status = DownloadStatus.Failed;
            task.Error = ErrorClassifier.Classify(ex);
        }
        finally
        {
            if (_running.TryRemove(task.Id, out var removed)) removed.Dispose();

            if (task.IsFinished)
            {
                task.FinishedAt ??= DateTimeOffset.Now;
                Finish(task);
            }

            Notify(task);

            // A freed slot may let the next queued task start.
            _ = PumpAsync();
        }
    }

    private void ApplyOutcome(DownloadTask task, DownloadOutcome outcome, AppSettings settings)
    {
        // A pause arrives as a cancellation; the status was already set, so it must not be
        // overwritten with Cancelled here.
        if (outcome.Status == DownloadStatus.Cancelled && task.Status == DownloadStatus.Paused)
        {
            task.Stage = DownloadStage.None;
            return;
        }

        task.ResultPath = outcome.FilePath;
        task.ResultSizeBytes = outcome.FileSizeBytes;
        task.Error = outcome.Error;

        if (outcome.Status == DownloadStatus.Failed &&
            outcome.Error is { IsRetryable: true, IsPermanent: false } &&
            task.AttemptCount <= settings.Downloads.RetryCount)
        {
            _logger.LogInformation("Retrying {Title} automatically (attempt {Attempt})",
                task.Title, task.AttemptCount);
            task.Status = DownloadStatus.Queued;
            task.Stage = DownloadStage.None;
            return;
        }

        task.Status = outcome.Status;
        task.Stage = outcome.Status == DownloadStatus.Completed ? DownloadStage.None : task.Stage;
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the task finished on its own */ }
    }

    private void Notify(DownloadTask task)
    {
        try
        {
            TaskChanged?.Invoke(this, new DownloadTaskEventArgs(task));
        }
        catch (Exception ex)
        {
            // A misbehaving subscriber must not take the queue down with it.
            _logger.LogError(ex, "A TaskChanged handler threw");
        }
    }

    private void Finish(DownloadTask task)
    {
        try
        {
            TaskFinished?.Invoke(this, new DownloadTaskEventArgs(task));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A TaskFinished handler threw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already gone */ }

        Cleanup();
    }

    /// <summary>
    /// Synchronous disposal, required because the dependency-injection container disposes
    /// singletons through <see cref="IDisposable"/>. A service that implements only
    /// <see cref="IAsyncDisposable"/> makes the container's synchronous
    /// <c>Dispose</c> throw, which surfaced as a shutdown error on every exit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { /* already gone */ }

        Cleanup();
    }

    private void Cleanup()
    {
        _disposed = true;

        foreach (var cts in _running.Values)
        {
            SafeCancel(cts);
            cts.Dispose();
        }
        _running.Clear();

        _shutdown.Dispose();
        _pumpGate.Dispose();
    }
}

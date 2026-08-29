using System.Collections.ObjectModel;
using System.Windows;
using MiguelDownloader.App.Services;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Engine.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MiguelDownloader.App.ViewModels;

/// <summary>
/// The download queue page.
/// <para>
/// The engine raises change events from worker threads, so every update is marshalled onto the
/// UI thread before touching the observable collection. Doing that here keeps the engine free of
/// any dependency on WPF.
/// </para>
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly QueueCoordinator _coordinator;
    private readonly ShellService _shell;
    private readonly Dictionary<string, DownloadItemViewModel> _lookup = [];

    public DownloadsViewModel(QueueCoordinator coordinator, ShellService shell)
    {
        _coordinator = coordinator;
        _shell = shell;

        _coordinator.Queue.TaskChanged += OnTaskChanged;
        Rebuild();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private bool _hasFinishedItems;

    private void OnTaskChanged(object? sender, DownloadTaskEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) Apply(e.Task);
        else dispatcher.BeginInvoke(() => Apply(e.Task));
    }

    private void Apply(DownloadTask task)
    {
        if (_lookup.TryGetValue(task.Id, out var existing))
        {
            existing.Refresh();
        }
        else
        {
            // A task can also disappear (removed from the queue), in which case a full rebuild is
            // simpler and cheap at these list sizes.
            Rebuild();
            return;
        }

        // The queue can drop items independently of this event, so keep the view in step.
        if (_lookup.Count != _coordinator.Queue.Tasks.Count) Rebuild();
        else UpdateCounts();
    }

    private void Rebuild()
    {
        var tasks = _coordinator.Queue.Tasks;

        Items.Clear();
        _lookup.Clear();

        foreach (var task in tasks)
        {
            var viewModel = new DownloadItemViewModel(task);
            viewModel.Refresh();
            Items.Add(viewModel);
            _lookup[task.Id] = viewModel;
        }

        UpdateCounts();
    }

    private void UpdateCounts()
    {
        ActiveCount = Items.Count(i => i.Status == DownloadStatus.Running);
        PendingCount = Items.Count(i => i.Status == DownloadStatus.Queued);
        HasFinishedItems = Items.Any(i => i.Task.IsFinished);
    }

    [RelayCommand]
    private void Pause(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _coordinator.Queue.Pause(item.Id);
    }

    [RelayCommand]
    private void Resume(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _coordinator.Queue.Resume(item.Id);
    }

    [RelayCommand]
    private void Cancel(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _coordinator.Queue.Cancel(item.Id);
    }

    [RelayCommand]
    private void Retry(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _coordinator.Queue.Retry(item.Id);
    }

    [RelayCommand]
    private void Remove(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _coordinator.Queue.Remove(item.Id);
        Rebuild();
    }

    [RelayCommand]
    private void OpenFile(DownloadItemViewModel? item)
    {
        if (item?.FilePath is null) return;
        _shell.OpenFile(item.FilePath);
    }

    [RelayCommand]
    private void OpenFolder(DownloadItemViewModel? item)
    {
        if (item is null) return;
        _shell.OpenContainingFolder(item.FilePath ?? item.Task.Request.TargetDirectory);
    }

    [RelayCommand]
    private static void CopyUrl(DownloadItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            Clipboard.SetText(item.Url);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process had the clipboard locked; not worth interrupting the user over.
        }
    }

    [RelayCommand]
    private void ClearFinished()
    {
        _coordinator.Queue.ClearFinished();
        Rebuild();
    }
}

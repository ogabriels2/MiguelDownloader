using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MiguelDownloader.App.Converters;
using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.ViewModels;

/// <summary>One row of the history list.</summary>
public sealed partial class HistoryRowViewModel(HistoryEntry entry) : ObservableObject
{
    public HistoryEntry Entry { get; } = entry;

    public string Id => Entry.Id;
    public string Title => Entry.Title;
    public string Author => Entry.Author ?? string.Empty;
    public string Url => Entry.Url;
    public string? ThumbnailUrl => Entry.ThumbnailUrl;
    public string? FilePath => Entry.FilePath;

    public string StatusText => Loc.ForEnum("Status", Entry.Status);
    public DownloadStatus Status => Entry.Status;

    public string SizeText => Entry.FileSize is { } size ? ByteSizeConverter.Format(size) : string.Empty;

    public string DurationText => Entry.Duration is { } duration
        ? TimeSpanDisplayConverter.Format(duration)
        : string.Empty;

    public string DateText => Entry.CompletedAt.LocalDateTime.ToString("g", Loc.Culture);

    public string FormatText => string.Join(" · ", new[]
    {
        Entry.Container,
        Entry.QualityLabel,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// Whether the file is still where it was written. Re-checked on each refresh, because a file
    /// can be moved or deleted at any time by anything on the machine.
    /// </summary>
    [ObservableProperty]
    private bool _fileExists;

    public void RefreshFileState()
        => FileExists = Entry.FilePath is { Length: > 0 } path && File.Exists(path);
}

/// <summary>The history page: search, filter, and act on past downloads.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly HistoryRepository _history;
    private readonly ShellService _shell;
    private readonly DialogService _dialogs;
    private readonly ILogger<HistoryViewModel> _logger;

    private CancellationTokenSource? _searchCancellation;

    public HistoryViewModel(
        HistoryRepository history,
        ShellService shell,
        DialogService dialogs,
        ILogger<HistoryViewModel> logger)
    {
        _history = history;
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private HistorySort _sort = HistorySort.NewestFirst;

    /// <summary>Null means every mode.</summary>
    [ObservableProperty]
    private DownloadMode? _modeFilter;

    public IReadOnlyList<HistorySort> SortChoices { get; } =
        [HistorySort.NewestFirst, HistorySort.OldestFirst, HistorySort.TitleAscending, HistorySort.LargestFirst];

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();
    partial void OnSortChanged(HistorySort value) => _ = RefreshAsync();
    partial void OnModeFilterChanged(DownloadMode? value) => _ = RefreshAsync();

    /// <summary>Reloads the list for the current search, filter and sort.</summary>
    public async Task RefreshAsync()
    {
        // Typing produces a query per keystroke; only the most recent one matters.
        if (_searchCancellation is not null)
        {
            try { await _searchCancellation.CancelAsync().ConfigureAwait(true); }
            catch (ObjectDisposedException) { }
            _searchCancellation.Dispose();
        }

        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        IsLoading = true;
        try
        {
            // A short delay collapses a burst of keystrokes into one query.
            await Task.Delay(150, token).ConfigureAwait(true);

            var query = new HistoryQuery
            {
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                Mode = ModeFilter,
                Sort = Sort,
                Limit = 500,
            };

            var entries = await _history.QueryAsync(query, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            Rows.Clear();
            foreach (var entry in entries)
            {
                var row = new HistoryRowViewModel(entry);
                row.RefreshFileState();
                Rows.Add(row);
            }

            TotalCount = await _history.CountAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenFile(HistoryRowViewModel? row)
    {
        if (row?.FilePath is null) return;

        if (!_shell.OpenFile(row.FilePath))
        {
            row.RefreshFileState();
            _dialogs.ShowMessage(Loc.Get("History_FileMissing"));
        }
    }

    [RelayCommand]
    private void OpenFolder(HistoryRowViewModel? row)
    {
        if (row?.FilePath is null) return;
        _shell.OpenContainingFolder(row.FilePath);
    }

    [RelayCommand]
    private static void CopyUrl(HistoryRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            Clipboard.SetText(row.Url);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard was locked by another process; not worth surfacing.
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(HistoryRowViewModel? row)
    {
        if (row is null) return;

        await _history.RemoveAsync(row.Id).ConfigureAwait(true);
        Rows.Remove(row);
        TotalCount = Math.Max(0, TotalCount - 1);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!_dialogs.Confirm(Loc.Get("History_ClearConfirm"), Loc.Get("History_Clear"))) return;

        await _history.ClearAsync().ConfigureAwait(true);
        Rows.Clear();
        TotalCount = 0;
    }

    /// <summary>Raised when the user wants to download an entry again.</summary>
    public event EventHandler<string>? DownloadAgainRequested;

    [RelayCommand]
    private void DownloadAgain(HistoryRowViewModel? row)
    {
        if (row is null) return;
        DownloadAgainRequested?.Invoke(this, row.Url);
    }
}

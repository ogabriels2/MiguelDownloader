using System.Windows.Controls;
using MiguelDownloader.App.ViewModels;
using MiguelDownloader.Core.Downloads;

namespace MiguelDownloader.App.Views;

/// <summary>The history page.</summary>
public partial class HistoryPage : UserControl
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    private HistoryViewModel? ViewModel => DataContext as HistoryViewModel;

    /// <summary>
    /// Maps the filter combo onto the nullable mode the query understands. Index 0 means every
    /// mode, so it clears the filter rather than selecting one.
    /// </summary>
    private void OnModeFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel || sender is not ComboBox combo) return;

        viewModel.ModeFilter = combo.SelectedIndex switch
        {
            1 => DownloadMode.Video,
            2 => DownloadMode.Audio,
            _ => null,
        };
    }
}

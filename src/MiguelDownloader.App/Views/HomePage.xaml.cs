using System.Windows.Controls;
using MiguelDownloader.App.ViewModels;
using MiguelDownloader.Core.Downloads;

namespace MiguelDownloader.App.Views;

/// <summary>
/// The Home page.
/// <para>
/// The mode radio buttons are handled here rather than through a two-way binding: an enum-to-bool
/// binding needs a converter per value and fires on both the checked and unchecked transitions,
/// which makes the view model see a spurious intermediate state. Two small handlers are clearer
/// and behave correctly.
/// </para>
/// </summary>
public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    private HomeViewModel? ViewModel => DataContext as HomeViewModel;

    /// <summary>Puts the caret in the URL field, used by the Ctrl+L shortcut.</summary>
    public void FocusUrlBox()
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private void OnVideoModeChecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.Mode = DownloadMode.Video;
    }

    private void OnAudioModeChecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel) viewModel.Mode = DownloadMode.Audio;
    }

    private void OnAdvancedClick(object sender, System.Windows.RoutedEventArgs e)
        => OptionsPopup.IsOpen = !OptionsPopup.IsOpen;
}

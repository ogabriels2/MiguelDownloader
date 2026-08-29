using MiguelDownloader.App.ViewModels;
using Wpf.Ui.Controls;

namespace MiguelDownloader.App.Views;

/// <summary>
/// Keeps global preferences out of the primary download workspace. Output choices for one job
/// remain beside that job; this dialog contains only reusable defaults and application behaviour.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

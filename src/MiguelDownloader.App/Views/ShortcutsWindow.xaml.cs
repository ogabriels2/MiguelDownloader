using System.Windows;
using Wpf.Ui.Controls;

namespace MiguelDownloader.App.Views;

/// <summary>The keyboard shortcut reference, opened with F1 or from Ajuda.</summary>
public partial class ShortcutsWindow : FluentWindow
{
    public ShortcutsWindow() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiguelDownloader.App.ViewModels;

namespace MiguelDownloader.App.Views;

/// <summary>The download queue page.</summary>
public partial class DownloadsPage : UserControl
{
    public DownloadsPage()
    {
        InitializeComponent();

        // Delete removes the selected item, which is the behaviour a list invites.
        InputBindings.Add(new KeyBinding(new RemoveSelectedCommand(this), Key.Delete, ModifierKeys.None));
    }

    private DownloadsViewModel? ViewModel => DataContext as DownloadsViewModel;

    private sealed class RemoveSelectedCommand(DownloadsPage page) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (page.ViewModel is not { } viewModel) return;

            // Only act on an actual selection; the shortcut should do nothing otherwise.
            var list = FindListView(page);
            if (list?.SelectedItem is DownloadItemViewModel selected)
                viewModel.RemoveCommand.Execute(selected);
        }

        private static ListView? FindListView(DependencyObject parent)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ListView list) return list;

                var found = FindListView(child);
                if (found is not null) return found;
            }
            return null;
        }
    }
}

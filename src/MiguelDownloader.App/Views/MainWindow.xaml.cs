using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.App.ViewModels;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Core.Urls;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace MiguelDownloader.App.Views;

/// <summary>
/// The application shell.
/// <para>
/// Owns window-level concerns the view models should not know about: keyboard shortcuts, drag and
/// drop, the clipboard prompt, and refusing to close while work is in flight.
/// </para>
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly ClipboardWatcher _clipboard;
    private readonly ThemeService _theme;
    private readonly NotificationService _notifications;
    private readonly SettingsService _settings;
    private readonly ToolService _tools;
    private readonly ToolLocator _toolLocator;
    private readonly ShellService _shell;
    private readonly ILogger<MainWindow> _logger;

    /// <summary>Where "Ajuda > Site do autor" goes.</summary>
    private const string AuthorSite = "https://ogabriels.com";

    private bool _shuttingDown;
    private SettingsWindow? _settingsWindow;
    private ShortcutsWindow? _shortcutsWindow;

    public MainWindow(
        MainViewModel viewModel,
        ClipboardWatcher clipboard,
        ThemeService theme,
        NotificationService notifications,
        SettingsService settings,
        ToolService tools,
        ToolLocator toolLocator,
        ShellService shell,
        ILogger<MainWindow> logger)
    {
        _viewModel = viewModel;
        _clipboard = clipboard;
        _theme = theme;
        _notifications = notifications;
        _settings = settings;
        _tools = tools;
        _toolLocator = toolLocator;
        _shell = shell;
        _logger = logger;

        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;

        _notifications.NotificationRaised += OnNotificationRaised;
        _clipboard.UrlDetected += OnClipboardUrlDetected;

        RegisterShortcuts();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();

        // Following the Windows theme needs a window handle, which only exists once loaded.
        _theme.AttachToShell();
        _clipboard.Attach(this);

        await _viewModel.InitializeAsync();

        // After initialisation, so the guide's last page can show the destination folder the
        // application settled on rather than an empty string.
        await ShowGuideOnFirstRunAsync();
    }

    /// <summary>
    /// Opens the guide the first time the application runs, and records that it was shown.
    /// <para>
    /// The flag is written whether the person read the guide or skipped it. Someone who dismissed
    /// it has answered the question, and asking again on the next launch would be nagging.
    /// </para>
    /// </summary>
    private async Task ShowGuideOnFirstRunAsync()
    {
        if (_settings.Current.General.HasSeenWelcome) return;

        ShowGuide();

        try
        {
            await _settings.UpdateAsync(s => s with { General = s.General with { HasSeenWelcome = true } });
        }
        catch (Exception ex)
        {
            // Failing to persist means the guide appears again next time, which is a small
            // annoyance and not a reason to interrupt anyone on their first launch.
            _logger.LogWarning(ex, "Could not record that the guide was shown");
        }
    }

    /// <summary>
    /// Keeps the window inside the usable part of the screen.
    /// <para>
    /// The default size is chosen for a large display. On a smaller one it used to open taller
    /// than the desktop, which put the status bar behind the taskbar where it could never be read.
    /// </para>
    /// </summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0) return;

        var width = Math.Min(Width, work.Width);
        var height = Math.Min(Height, work.Height);
        if (width.Equals(Width) && height.Equals(Height)) return;

        Width = width;
        Height = height;
        Left = work.Left + ((work.Width - width) / 2);
        Top = work.Top + ((work.Height - height) / 2);
    }

    /// <summary>
    /// Keyboard shortcuts. These are the ones a keyboard-first user reaches for, and every one
    /// has a visible equivalent so nothing is only reachable by shortcut.
    /// </summary>
    private void RegisterShortcuts()
    {
        // Ctrl+L focuses the URL field, Ctrl+V pastes into it, Ctrl+, opens settings.
        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(() => FocusUrlField()), Key.L, ModifierKeys.Control));

        // Ctrl+V pastes and analyses: the link is on the clipboard precisely because the next
        // thing wanted is to see what is behind it.
        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(() =>
            {
                _viewModel.Home.PasteAndAnalyzeCommand.Execute(null);
                FocusUrlField();
            }), Key.V, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(() => _viewModel.Settings.OpenDownloadFolderCommand.Execute(null)),
            Key.O, ModifierKeys.Control | ModifierKeys.Shift));

        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(() => _viewModel.Home.SelectAllItemsCommand.Execute(null)),
            Key.A, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(new RelayCommandAdapter(ShowShortcuts), Key.F1, ModifierKeys.None));

        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(ShowSettingsDialog), Key.OemComma, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(() => ActivityTabs.SelectedIndex = 0), Key.J, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(
            new RelayCommandAdapter(() =>
            {
                ActivityTabs.SelectedIndex = 1;
                _ = _viewModel.History.RefreshAsync();
            }), Key.H, ModifierKeys.Control));
    }

    private void FocusUrlField()
    {
        // Focus after the layout pass so the shortcut also works while a flyout is closing.
        Dispatcher.BeginInvoke(() =>
        {
            var page = FindVisualChild<HomePage>(this);
            page?.FocusUrlBox();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettingsDialog();

    private void ShowSettingsDialog()
    {
        if (_settingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(_viewModel.Settings) { Owner = this };
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        window.ShowDialog();
    }

    private void OnShowQueueClick(object sender, RoutedEventArgs e)
        => ActivityTabs.SelectedIndex = 0;

    private void OnShowHistoryClick(object sender, RoutedEventArgs e)
    {
        ActivityTabs.SelectedIndex = 1;
        _ = _viewModel.History.RefreshAsync();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object sender, RoutedEventArgs e)
        => new AboutWindow(_tools, _toolLocator, _shell) { Owner = this }.ShowDialog();

    private void OnGuideClick(object sender, RoutedEventArgs e) => ShowGuide();

    /// <summary>
    /// Opens the short guide. The destination folder is passed in rather than read inside the
    /// window, so what it shows is the folder this session is actually using.
    /// </summary>
    private void ShowGuide()
    {
        var destination = _viewModel.Home.TargetDirectory;
        if (string.IsNullOrWhiteSpace(destination))
            destination = _settings.ResolveDownloadFolder();

        new WelcomeWindow(destination) { Owner = this }.ShowDialog();
    }

    private void OnLicenceClick(object sender, RoutedEventArgs e)
        => OpenLegalDocument("LICENCA.txt");

    private void OnNoticesClick(object sender, RoutedEventArgs e)
        => OpenLegalDocument("AVISOS-DE-TERCEIROS.txt");

    private void OnSiteClick(object sender, RoutedEventArgs e)
        => _shell.OpenUrl(AuthorSite);

    /// <summary>
    /// Opens one of the documents installed beside the executable. Saying the file is missing
    /// beats a menu item that appears to do nothing when the program has been moved without them.
    /// </summary>
    private void OpenLegalDocument(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
        if (System.IO.File.Exists(path) && _shell.OpenFile(path)) return;

        System.Windows.MessageBox.Show(
            this,
            string.Format(Loc.Get("About_DocumentMissing"), fileName),
            Loc.Get("App_Title"),
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShowShortcuts();

    private void ShowShortcuts()
    {
        if (_shortcutsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var window = new ShortcutsWindow { Owner = this };
        _shortcutsWindow = window;
        window.Closed += (_, _) => _shortcutsWindow = null;
        window.Show();
    }

    /// <summary>Ticks the theme the app is actually using, so the menu never lies about state.</summary>
    private void OnThemeMenuOpened(object sender, RoutedEventArgs e)
    {
        ThemeSystemItem.IsChecked = _theme.Current == AppTheme.System;
        ThemeLightItem.IsChecked = _theme.Current == AppTheme.Light;
        ThemeDarkItem.IsChecked = _theme.Current == AppTheme.Dark;
    }

    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => ApplyTheme(AppTheme.System);
    private void OnThemeLightClick(object sender, RoutedEventArgs e) => ApplyTheme(AppTheme.Light);
    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => ApplyTheme(AppTheme.Dark);

    /// <summary>Applies the theme and remembers it, so the choice survives the next launch.</summary>
    private void ApplyTheme(AppTheme theme)
    {
        _theme.Apply(theme);
        _ = _settings.UpdateAsync(s => s with { General = s.General with { Theme = theme } });
    }
    private void OnActivityTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityTabs.SelectedIndex == 1)
            _ = _viewModel.History.RefreshAsync();
    }

    private void OnNotificationRaised(object? sender, AppNotification notification)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnNotificationRaised(sender, notification));
            return;
        }

        MessageBar.Title = notification.Title;
        MessageBar.Message = notification.Message;
        MessageBar.Severity = notification.Kind switch
        {
            NotificationKind.Success => InfoBarSeverity.Success,
            NotificationKind.Warning => InfoBarSeverity.Warning,
            NotificationKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        MessageBar.IsOpen = true;
    }

    /// <summary>
    /// Offers to analyse a copied link. It only ever asks; nothing is fetched until the user says
    /// so, because reacting silently to the clipboard would be both surprising and intrusive.
    /// </summary>
    private void OnClipboardUrlDetected(object? sender, string url)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnClipboardUrlDetected(sender, url));
            return;
        }

        MessageBar.Title = Loc.Get("Dialog_ClipboardDetected");
        MessageBar.Message = url;
        MessageBar.Severity = InfoBarSeverity.Informational;
        MessageBar.IsOpen = true;

        // Put it in the field ready to go, but do not start analysing on its own.
        _viewModel.Home.SetUrl(url);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);

        e.Effects = ExtractDroppedUrl(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        var url = ExtractDroppedUrl(e);
        if (url is null) return;

        _viewModel.Home.SetUrl(url);
        e.Handled = true;
    }

    /// <summary>Reads a URL out of a drop, whether it arrives as text or as a browser link.</summary>
    private static string? ExtractDroppedUrl(DragEventArgs e)
    {
        foreach (var format in (ReadOnlySpan<string>)[DataFormats.UnicodeText, DataFormats.Text, "UniformResourceLocator"])
        {
            if (!e.Data.GetDataPresent(format)) continue;

            var raw = e.Data.GetData(format) as string;
            var url = MediaUrlParser.ExtractFirstUrl(raw);
            if (url is not null && MediaUrlParser.TryParse(url, out var info) && info.IsDownloadable)
                return url;
        }
        return null;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shuttingDown) return;

        if (!_viewModel.CanClose())
        {
            e.Cancel = true;
            return;
        }

        // Persisting is asynchronous, so the close is deferred until it finishes rather than
        // racing the process exit and losing the queue.
        e.Cancel = true;
        _shuttingDown = true;

        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure while shutting down");
        }

        _clipboard.Dispose();
        Application.Current.Shutdown();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Minimal ICommand so a shortcut can invoke a plain method.</summary>
    private sealed class RelayCommandAdapter(Action execute) : ICommand
    {
        private readonly Action _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { } remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}

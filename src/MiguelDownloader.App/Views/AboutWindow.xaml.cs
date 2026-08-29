using System.IO;
using System.Reflection;
using System.Windows;
using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.Engine.Dependencies;
using Wpf.Ui.Controls;

namespace MiguelDownloader.App.Views;

/// <summary>
/// Who made this, what it is running, and where the licence terms are.
/// <para>
/// The tool versions are read from the copies actually resolved on disk rather than from anything
/// compiled in, so the dialog is useful when someone is trying to work out why a site stopped
/// working. "Copy details" exists for the same reason.
/// </para>
/// </summary>
public partial class AboutWindow : FluentWindow
{
    private readonly ToolService _tools;
    private readonly ToolLocator _locator;
    private readonly ShellService _shell;

    public AboutWindow(ToolService tools, ToolLocator locator, ShellService shell)
    {
        _tools = tools;
        _locator = locator;
        _shell = shell;
        InitializeComponent();

        VersionText.Text = string.Format(Loc.Get("About_Version"), ReadVersion());
        CopyrightText.Text = ReadCopyright();
        EngineVersion.Text = Describe(_tools.Current?.YtDlp.Version);
        MediaVersion.Text = Describe(null);

        Loaded += async (_, _) => await ShowMediaVersionAsync();
    }

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "?";

        // Strip the build metadata suffix ("1.0.0+abc1234"), which means nothing to a reader.
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    /// <summary>
    /// The copyright line comes from the assembly rather than from a string in this file, so the
    /// notice on screen and the one Windows shows in the file properties cannot drift apart.
    /// </summary>
    private static string ReadCopyright()
        => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
           ?? string.Empty;

    /// <summary>
    /// Asks ffmpeg what it is. Only yt-dlp's version is captured at startup, because that is the
    /// one the update check needs; ffmpeg's costs a process launch, so it is paid here, once, and
    /// only if somebody opens this dialog.
    /// </summary>
    private async Task ShowMediaVersionAsync()
    {
        var ffmpeg = _tools.Current?.Ffmpeg;
        if (ffmpeg is not { IsAvailable: true, Path: { Length: > 0 } path }) return;

        // "ffmpeg version 7.1-full_build-www.gyan.dev ..." — the third word is the part that matters.
        var reported = await _locator.TryGetVersionAsync(path, ["-version"]);
        var trimmed = reported?.Split(' ') is { Length: >= 3 } parts && parts[0] == "ffmpeg"
            ? parts[2]
            : reported;

        MediaVersion.Text = Describe(trimmed);
    }

    /// <summary>An absent tool says so plainly rather than showing a blank.</summary>
    private static string Describe(string? version)
        => string.IsNullOrWhiteSpace(version) ? "—" : version;

    private void OnOpenLicenceClick(object sender, RoutedEventArgs e)
        => OpenLegalDocument("LICENCA.txt");

    private void OnOpenNoticesClick(object sender, RoutedEventArgs e)
        => OpenLegalDocument("AVISOS-DE-TERCEIROS.txt");

    /// <summary>
    /// Opens one of the documents installed beside the executable. A missing file says so rather
    /// than doing nothing, because silence here looks like a broken link.
    /// </summary>
    private void OpenLegalDocument(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path) && _shell.OpenFile(path)) return;

        System.Windows.MessageBox.Show(
            this,
            string.Format(Loc.Get("About_DocumentMissing"), fileName),
            Loc.Get("About_Title"),
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var details = string.Join(
            Environment.NewLine,
            $"{Loc.Get("App_Title")} {VersionText.Text}",
            $"{Loc.Get("About_Engine")}: {EngineVersion.Text}",
            $"{Loc.Get("About_Media")}: {MediaVersion.Text}",
            $"Windows {Environment.OSVersion.Version}",
            $".NET {Environment.Version}");

        try
        {
            Clipboard.SetText(details);
            CopyButton.Content = Loc.Get("About_Copied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process owns the clipboard. Nothing here is worth an error dialog.
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

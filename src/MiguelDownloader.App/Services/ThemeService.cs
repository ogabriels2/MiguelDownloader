using System.Windows;
using MiguelDownloader.Core.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Applies the light or dark theme.
/// <para>
/// Following Windows is the default and is genuinely live: the watcher keeps the app in step when
/// the user switches theme, including on the automatic day/night schedule, instead of only reading
/// the setting once at startup.
/// </para>
/// </summary>
public sealed class ThemeService(ILogger<ThemeService> logger)
{
    private readonly ILogger<ThemeService> _logger = logger;
    private bool _watching;

    /// <summary>
    /// The accent the whole application is tinted with: the gold of the mark.
    /// <para>
    /// WPF-UI otherwise follows the accent colour chosen in Windows, which means the program looks
    /// like a different product on every machine. Fixing it here is what makes the icon, the
    /// installer and the interface read as one thing.
    /// </para>
    /// </summary>
    public static readonly System.Windows.Media.Color Accent =
        System.Windows.Media.Color.FromRgb(0xE8, 0xA3, 0x17);

    public AppTheme Current { get; private set; } = AppTheme.System;

    public void Apply(AppTheme theme)
    {
        Current = theme;

        if (Application.Current?.MainWindow is null && theme == AppTheme.System)
        {
            // The watcher needs a window handle; applying the resolved theme now and starting the
            // watcher once the shell exists keeps startup correct either way.
            var early = ResolveSystemTheme();
            ApplicationThemeManager.Apply(early);
            ApplyAccent(early);
            return;
        }

        switch (theme)
        {
            case AppTheme.Light:
                StopWatching();
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                ApplyAccent(ApplicationTheme.Light);
                break;

            case AppTheme.Dark:
                StopWatching();
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                ApplyAccent(ApplicationTheme.Dark);
                break;

            default:
                var resolved = ResolveSystemTheme();
                ApplicationThemeManager.Apply(resolved);
                ApplyAccent(resolved);
                StartWatching();
                break;
        }

        _logger.LogDebug("Applied theme {Theme}", theme);
    }

    /// <summary>
    /// Re-tints the palette with the application accent.
    /// <para>
    /// Applying a theme resets the accent to the one Windows is using, so this has to run after
    /// every theme change rather than once at startup, or switching to dark quietly hands the
    /// interface back to whatever colour the machine prefers.
    /// </para>
    /// </summary>
    private void ApplyAccent(ApplicationTheme theme)
    {
        try
        {
            ApplicationAccentColorManager.Apply(Accent, theme);
        }
        catch (Exception ex)
        {
            // A palette that failed to tint is still a usable palette.
            _logger.LogWarning(ex, "Could not apply the accent colour");
        }
    }

    /// <summary>Called once the shell window exists, so system-theme watching can attach to it.</summary>
    public void AttachToShell()
    {
        if (Current == AppTheme.System) StartWatching();
    }

    private static ApplicationTheme ResolveSystemTheme()
        => ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.CapturedMotion
            or SystemTheme.Glow or SystemTheme.HCBlack
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;

    private void StartWatching()
    {
        if (_watching) return;

        var window = Application.Current?.MainWindow;
        if (window is null) return;

        try
        {
            SystemThemeWatcher.Watch(window);
            _watching = true;
            _logger.LogDebug("Following the Windows theme");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Not being able to follow the system theme is a cosmetic limitation, never fatal.
            _logger.LogWarning(ex, "Could not start the system theme watcher");
        }
    }

    private void StopWatching()
    {
        if (!_watching) return;

        var window = Application.Current?.MainWindow;
        if (window is null) { _watching = false; return; }

        try
        {
            SystemThemeWatcher.UnWatch(window);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            _logger.LogDebug(ex, "Could not stop the system theme watcher");
        }
        finally
        {
            _watching = false;
        }
    }
}

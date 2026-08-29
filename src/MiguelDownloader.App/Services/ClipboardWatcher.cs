using System.Windows;
using System.Windows.Interop;
using MiguelDownloader.Core.Urls;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Watches the clipboard for YouTube links, when the user has turned that on.
/// <para>
/// This uses the Windows clipboard-change notification rather than polling, so it costs nothing
/// while idle. It only ever <em>offers</em> to analyse: nothing is fetched or downloaded without
/// the user acting on the prompt, because silently reacting to whatever someone copies would be
/// both surprising and a privacy problem.
/// </para>
/// </summary>
public sealed class ClipboardWatcher(SettingsService settings, ILogger<ClipboardWatcher> logger) : IDisposable
{
    private readonly SettingsService _settings = settings;
    private readonly ILogger<ClipboardWatcher> _logger = logger;

    private HwndSource? _source;
    private IntPtr _handle = IntPtr.Zero;
    private bool _listening;
    private string? _lastSeenUrl;

    /// <summary>Raised when a YouTube URL is copied. The argument is the detected URL.</summary>
    public event EventHandler<string>? UrlDetected;

    /// <summary>Attaches to the shell window. Safe to call before the setting is enabled.</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        ApplySetting();
        _settings.Changed += (_, _) => ApplySetting();
    }

    private void ApplySetting()
    {
        var wanted = _settings.Current.General.WatchClipboard;
        if (wanted == _listening) return;

        if (wanted) Start();
        else Stop();
    }

    private void Start()
    {
        if (_handle == IntPtr.Zero || _listening) return;

        if (AddClipboardFormatListener(_handle))
        {
            _listening = true;
            _logger.LogDebug("Clipboard monitoring started");
        }
        else
        {
            _logger.LogWarning("Could not register the clipboard listener");
        }
    }

    private void Stop()
    {
        if (_handle == IntPtr.Zero || !_listening) return;

        RemoveClipboardFormatListener(_handle);
        _listening = false;
        _lastSeenUrl = null;
        _logger.LogDebug("Clipboard monitoring stopped");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmClipboardUpdate || !_listening) return IntPtr.Zero;

        try
        {
            // Another application can hold the clipboard open momentarily, which makes this throw.
            if (!Clipboard.ContainsText()) return IntPtr.Zero;

            var text = Clipboard.GetText();
            var url = MediaUrlParser.ExtractFirstUrl(text);

            if (url is null || !MediaUrlParser.TryParse(url, out var info) || !info.IsDownloadable)
                return IntPtr.Zero;

            // Copying the same link twice should not prompt twice.
            if (string.Equals(url, _lastSeenUrl, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            _lastSeenUrl = url;

            UrlDetected?.Invoke(this, url);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read the clipboard");
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private const int WmClipboardUpdate = 0x031D;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

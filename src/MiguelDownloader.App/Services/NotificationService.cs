using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using MiguelDownloader.App.Localization;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.Services;

/// <summary>Severity of an in-app message, which selects its colour and icon.</summary>
public enum NotificationKind
{
    Information = 0,
    Success,
    Warning,
    Error,
}

/// <summary>A message to show in the shell.</summary>
/// <param name="Title">Short headline.</param>
/// <param name="Message">Body text.</param>
/// <param name="Kind">Severity.</param>
public readonly record struct AppNotification(string Title, string Message, NotificationKind Kind);

/// <summary>
/// Reports progress and completion to the user outside the page they are looking at.
/// <para>
/// This deliberately does not use Windows toast notifications. Those require the application to
/// have a registered AppUserModelID, which in practice means shipping it as a packaged (MSIX)
/// app; an unpackaged build either silently shows nothing or throws. Rather than ship a feature
/// that works on some machines and not others, the app uses mechanisms that always work: an
/// in-app message, real progress on the taskbar button, and a taskbar flash when the window is
/// not in the foreground.
/// </para>
/// </summary>
public sealed class NotificationService(SettingsService settings, ILogger<NotificationService> logger)
{
    private readonly SettingsService _settings = settings;
    private readonly ILogger<NotificationService> _logger = logger;

    /// <summary>Raised when a message should appear in the shell.</summary>
    public event EventHandler<AppNotification>? NotificationRaised;

    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Information)
    {
        NotificationRaised?.Invoke(this, new AppNotification(title, message, kind));
    }

    /// <summary>Announces a finished download, honouring the user's notification preference.</summary>
    public void NotifyDownloadCompleted(string title)
    {
        if (!_settings.Current.General.ShowNotifications) return;

        Notify(Loc.Get("Status_Completed"), title, NotificationKind.Success);
        TryShowToast(Loc.Get("Status_Completed"), title);
        FlashIfInactive();
    }

    /// <summary>Announces a failure. Always shown, because a silent failure is worse than a noisy one.</summary>
    public void NotifyDownloadFailed(string title, string reason)
    {
        Notify(Loc.Get("Status_Failed"), $"{title} — {reason}", NotificationKind.Error);
        TryShowToast(Loc.Get("Status_Failed"), $"{title} — {reason}");
        FlashIfInactive();
    }

    /// <summary>
    /// The identity Windows associates the notifications with. Must match the AppUserModelID the
    /// installer stamps on the Start Menu shortcut, or the platform drops everything sent.
    /// </summary>
    public const string AppUserModelId = "MiguelDownloader.App";

    /// <summary>
    /// Whether Windows toasts have been found to work in this installation.
    /// <para>
    /// Null until the first attempt. A packaged or properly-installed app has a shortcut
    /// carrying the AppUserModelID and toasts work; running from a portable folder has no such
    /// shortcut and the platform refuses, so the result is cached and the app stops trying.
    /// </para>
    /// </summary>
    public bool? ToastsAvailable { get; private set; }

    /// <summary>
    /// Registers the process identity. Called once at startup, before any notification.
    /// </summary>
    public void RegisterAppIdentity()
    {
        try
        {
            var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            _logger.LogDebug("AppUserModelID set to {Id} (hr=0x{Result:X8})", AppUserModelId, result);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _logger.LogDebug(ex, "Could not set the AppUserModelID");
        }
    }

    /// <summary>
    /// Sends a Windows toast. Returns false when the platform will not accept one, which is the
    /// normal case for the portable package.
    /// </summary>
    private bool TryShowToast(string title, string message)
    {
        if (ToastsAvailable == false) return false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) { ToastsAvailable = false; return false; }

        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml($"""
                <toast activationType="foreground">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{Escape(title)}</text>
                      <text>{Escape(message)}</text>
                    </binding>
                  </visual>
                </toast>
                """);

            Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(AppUserModelId)
                .Show(new Windows.UI.Notifications.ToastNotification(xml));

            if (ToastsAvailable != true)
            {
                ToastsAvailable = true;
                _logger.LogInformation("Windows notifications are available");
            }
            return true;
        }
        catch (Exception ex)
        {
            // Without a Start Menu shortcut carrying the identity the platform refuses. That is a
            // limitation of how the app was deployed, not a fault, so it is recorded once and the
            // in-app message carries the information instead.
            ToastsAvailable = false;
            _logger.LogInformation(
                "Windows notifications unavailable ({Reason}); using the in-app message instead",
                ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Escapes text for the toast XML payload; a title with an ampersand is common.</summary>
    private static string Escape(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>
    /// Shows overall queue progress on the taskbar button, so a minimised window still reports
    /// how far along it is.
    /// </summary>
    /// <param name="fraction">0..1, or null to clear the indicator.</param>
    /// <param name="hasError">Show the error state instead of normal progress.</param>
    public void SetTaskbarProgress(double? fraction, bool hasError = false)
    {
        // Application.MainWindow is itself thread-affine: reading it off the UI thread throws
        // before any marshalling can happen. Everything that touches the window has to run
        // inside the dispatcher call, not around it.
        RunOnUi(window =>
        {
            window.TaskbarItemInfo ??= new TaskbarItemInfo();

            if (fraction is null)
            {
                window.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                return;
            }

            window.TaskbarItemInfo.ProgressState = hasError
                ? TaskbarItemProgressState.Error
                : TaskbarItemProgressState.Normal;
            window.TaskbarItemInfo.ProgressValue = Math.Clamp(fraction.Value, 0, 1);
        });
    }

    /// <summary>
    /// Runs an action against the main window on the UI thread.
    /// <para>
    /// The queue raises completion from a worker thread, so this is the only safe way to reach
    /// the window. Both the lookup and the use of the window happen inside the dispatcher.
    /// </para>
    /// </summary>
    private void RunOnUi(Action<Window> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Execute()
        {
            try
            {
                var window = Application.Current?.MainWindow;
                if (window is not null) action(window);
            }
            catch (Exception ex)
            {
                // Shell decoration must never take a download down with it.
                _logger.LogDebug(ex, "Could not update the shell window");
            }
        }

        if (dispatcher.CheckAccess()) Execute();
        else dispatcher.BeginInvoke(Execute);
    }

    /// <summary>Flashes the taskbar button when the window is not in the foreground.</summary>
    private void FlashIfInactive() => RunOnUi(window =>
    {
        if (window.IsActive) return;

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Handle = handle,
            Flags = FlashTray | FlashTimerNoForeground,
            Count = 3,
            Timeout = 0,
        };

        FlashWindowEx(ref info);
    });

    private const uint FlashTray = 0x00000002;
    private const uint FlashTimerNoForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Handle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll", EntryPoint = "FlashWindowEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}

using MiguelDownloader.App.Services;
using Velopack;

namespace MiguelDownloader.App;

/// <summary>
/// Process entry point. Velopack must run before WPF creates any application state because the
/// same executable is invoked for short-lived install and update hooks.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // MSIX packages are atomically serviced by Windows and, in production, signed and
        // updated by the Microsoft Store. Velopack remains the updater for the unpackaged GitHub
        // channel only; invoking it inside MSIX would create two competing servicing systems.
        if (!DistributionInfo.UpdatesManagedExternally)
        {
            VelopackApp.Build()
                .SetAppUserModelId(NotificationService.AppUserModelId)
                .Run();
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

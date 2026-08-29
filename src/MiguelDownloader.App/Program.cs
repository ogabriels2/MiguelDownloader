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
        VelopackApp.Build()
            .SetAppUserModelId(NotificationService.AppUserModelId)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

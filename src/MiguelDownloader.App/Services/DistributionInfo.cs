using System.Runtime.InteropServices;
using System.Text;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Describes the deployment boundary the current process is running inside.
/// </summary>
/// <remarks>
/// A packaged desktop application must be serviced by its MSIX source (the Microsoft Store in
/// production). Running an in-process installer beside it would bypass the package signature,
/// conflict with Windows servicing, and can leave two independently-versioned copies installed.
/// Detection is performed at runtime so the exact same executable remains safe when it is run
/// unpackaged, sideloaded for certification, or installed from the Store.
/// </remarks>
public static class DistributionInfo
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<bool> Packaged = new(DetectPackageIdentity);

    /// <summary>Whether Windows assigned an MSIX/AppX package identity to this process.</summary>
    public static bool IsPackaged => Packaged.Value;

    /// <summary>Whether application and bundled-tool updates belong to the package source.</summary>
    public static bool UpdatesManagedExternally => IsPackaged;

    private static bool DetectPackageIdentity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return false;

        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);

            // A packaged process needs a caller-provided buffer, so the size probe normally
            // returns ERROR_INSUFFICIENT_BUFFER. ERROR_SUCCESS is accepted defensively for API
            // implementations that can answer an empty identity without another call.
            return result is ErrorInsufficientBuffer or 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        [Out] StringBuilder? packageFullName);
}

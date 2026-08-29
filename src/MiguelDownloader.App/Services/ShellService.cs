using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Opens files and folders in Windows Explorer.
/// <para>
/// Paths are always passed as separate process arguments and never assembled into a command
/// string, so a file name containing quotes or an ampersand cannot turn into something else.
/// Every path is checked for existence first, because asking the shell to open a missing file
/// produces an error dialog the app has no control over.
/// </para>
/// </summary>
public sealed class ShellService(ILogger<ShellService> logger)
{
    private readonly ILogger<ShellService> _logger = logger;

    /// <summary>Opens a file with its default application.</summary>
    public bool OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("Cannot open a file that does not exist: {Path}", path);
            return false;
        }

        try
        {
            // UseShellExecute is required to honour the user's file associations. The path is the
            // FileName itself, not part of a parsed command line, so it needs no escaping.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open the file {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Opens a web address in the default browser.
    /// <para>
    /// Only http and https are accepted. Handing an arbitrary string to the shell would let any
    /// registered protocol run, and a scheme like <c>file:</c> or a custom handler is not
    /// something a link in this application should be able to reach.
    /// </para>
    /// </summary>
    public bool OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Refused to open a non-web address");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open the browser");
            return false;
        }
    }

    /// <summary>Opens Explorer with the file selected, or the folder when the file is gone.</summary>
    public bool OpenContainingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (File.Exists(path))
            {
                // ArgumentList keeps the path a single argument regardless of what it contains.
                var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add("/select,");
                info.ArgumentList.Add(Path.GetFullPath(path));
                Process.Start(info)?.Dispose();
                return true;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return OpenFolder(directory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not reveal {Path}", path);
            return false;
        }
    }

    /// <summary>Opens a folder, creating it first when it does not exist yet.</summary>
    public bool OpenFolder(string? path, bool createIfMissing = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (!Directory.Exists(path))
            {
                if (!createIfMissing) return false;
                Directory.CreateDirectory(path);
            }

            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not open the folder {Path}", path);
            return false;
        }
    }
}

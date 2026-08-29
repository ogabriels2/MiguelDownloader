using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiguelDownloader.App.Localization;
using MiguelDownloader.Core.Downloads;
using System.IO;

namespace MiguelDownloader.App.Converters;

/// <summary>Base class so converters only implement the direction they actually support.</summary>
public abstract class OneWayConverter : IValueConverter
{
    public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class InverseBoolToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

public sealed class NullToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class StringToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Visible when a collection has items. Used to hide empty sections entirely.</summary>
public sealed class CountToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Count(value) > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal static int Count(object? value) => value switch
    {
        null => 0,
        int i => i,
        ICollection collection => collection.Count,
        IEnumerable enumerable => enumerable.Cast<object>().Count(),
        _ => 0,
    };
}

/// <summary>Visible when a collection is empty. Drives the empty-state panels.</summary>
public sealed class ZeroCountToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CountToVisibilityConverter.Count(value) == 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Formats a byte count. Null becomes an em dash rather than "0 B", because an unknown size and
/// an empty file are different things and the interface should not conflate them.
/// </summary>
public sealed class ByteSizeConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long bytes => Format(bytes, culture),
            int bytes => Format(bytes, culture),
            double bytes => Format((long)bytes, culture),
            _ => "—",
        };

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) return "—";
        if (bytes < 1024) return string.Format(culture, "{0} B", bytes);

        string[] units = ["KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = -1;

        do
        {
            size /= 1024;
            unit++;
        } while (size >= 1024 && unit < units.Length - 1);

        // One decimal below 100 keeps the number informative without becoming noisy.
        return string.Format(culture, size < 100 ? "{0:0.0} {1}" : "{0:0} {1}", size, units[unit]);
    }
}

public sealed class TimeSpanDisplayConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan duration ? Format(duration) : "—";

    public static string Format(TimeSpan duration)
        => duration.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
                (int)duration.TotalHours, duration.Minutes, duration.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", duration.Minutes, duration.Seconds);
}

public sealed class SpeedConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double speed and > 0
            ? ByteSizeConverter.Format((long)speed, culture) + "/s"
            : string.Empty;
}

public sealed class EtaConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan eta and { TotalSeconds: > 0 } ? TimeSpanDisplayConverter.Format(eta) : string.Empty;
}

/// <summary>
/// Turns an enum into its translated name using a prefix, e.g. <c>Status</c> plus
/// <c>Completed</c> resolves <c>Status_Completed</c>.
/// </summary>
public sealed class LocalizedEnumConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Enum enumValue && parameter is string prefix
            ? Loc.ForEnum(prefix, enumValue)
            : value?.ToString() ?? string.Empty;
}

/// <summary>
/// Resolves the explanatory line under an enum's name, by looking up the same key with a
/// "_Hint" suffix. Returns an empty string when no hint exists, so a template can use this
/// unconditionally without every value needing one.
/// </summary>
public sealed class LocalizedEnumHintConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Enum enumValue || parameter is not string prefix) return string.Empty;

        var key = $"{prefix}_{enumValue}_Hint";
        var text = Loc.Get(key);

        // A missing key resolves to the key itself; that is useful in development but must not
        // leak into the interface.
        return text == key ? string.Empty : text;
    }
}

/// <summary>
/// Colour for a download status.
/// <para>
/// Status is always shown with text alongside this colour; colour alone never carries the
/// meaning, so the interface stays readable for anyone who cannot distinguish these hues.
/// </para>
/// </summary>
public sealed class StatusBrushConverter : OneWayConverter
{
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x0F, 0x7B, 0x0F));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0x9D, 0x5D, 0x00));
    private static readonly SolidColorBrush Active = new(Color.FromRgb(0x00, 0x5F, 0xB8));
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(0x60, 0x60, 0x60));

    static StatusBrushConverter()
    {
        // Brushes are shared across every row, so freezing avoids per-element overhead.
        Success.Freeze(); Error.Freeze(); Warning.Freeze(); Active.Freeze(); Neutral.Freeze();
    }

    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DownloadStatus.Completed => Success,
            DownloadStatus.Failed => Error,
            DownloadStatus.Paused or DownloadStatus.Skipped => Warning,
            DownloadStatus.Running => Active,
            _ => Neutral,
        };
}

/// <summary>
/// Shows a page only when the shell is on it.
/// <para>
/// Pages stay constructed and simply hide, so navigating away and back preserves what the user
/// had typed, selected or scrolled to. That costs a little memory and saves re-analysing a URL
/// every time someone glances at the queue.
/// </para>
/// </summary>
public sealed class PageVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string expected &&
           string.Equals(value.ToString(), expected, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
}

/// <summary>
/// Loads a thumbnail from a URL without blocking the UI thread.
/// <para>
/// <see cref="BitmapCacheOption.OnLoad"/> matters here: it reads the whole image up front so the
/// underlying stream can close, which stops a long queue from holding hundreds of open handles.
/// A failed load yields null and the template shows its placeholder instead.
/// </para>
/// </summary>
public sealed class ThumbnailConverter : OneWayConverter
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "file")) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            // Thumbnails render small; decoding at display size saves a lot of memory in long lists.
            image.DecodePixelWidth = 320;
            image.EndInit();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException
                                       or System.Net.WebException)
        {
            return null;
        }
    }
}

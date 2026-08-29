using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Windows.Markup;

namespace MiguelDownloader.App.Localization;

/// <summary>
/// Resolves user-facing text.
/// <para>
/// Every string the user reads comes from here, keyed by name, so nothing needs hunting through
/// code to translate. The engine layers deliberately return resource keys rather than sentences,
/// which keeps them free of presentation concerns and means the same failure reads correctly in
/// whichever language the user has chosen.
/// </para>
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources = new(
        "MiguelDownloader.App.Localization.Strings", Assembly.GetExecutingAssembly());

    /// <summary>Culture used for lookups. Set once at startup from the settings.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>Cultures the application ships translations for.</summary>
    public static IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages { get; } =
    [
        ("pt-BR", "Português (Brasil)"),
        ("en", "English"),
    ];

    /// <summary>
    /// Applies a language. An empty code follows the operating system.
    /// </summary>
    public static void SetLanguage(string? languageCode)
    {
        Culture = string.IsNullOrWhiteSpace(languageCode)
            ? CultureInfo.CurrentUICulture
            : TryCreate(languageCode) ?? CultureInfo.CurrentUICulture;

        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    private static CultureInfo? TryCreate(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks up a key. A missing key returns the key itself rather than throwing or showing an
    /// empty control, which makes the gap obvious during development without breaking the app.
    /// </summary>
    public static string Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        try
        {
            return Resources.GetString(key, Culture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    /// <summary>Looks up a key and fills in its placeholders.</summary>
    public static string Format(string key, params object?[] arguments)
    {
        var template = Get(key);
        try
        {
            return string.Format(Culture, template, arguments);
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder should not crash the window.
            return template;
        }
    }

    /// <summary>Builds the key for an enum value, e.g. <c>Status_Completed</c>.</summary>
    public static string ForEnum(string prefix, Enum value) => Get($"{prefix}_{value}");
}

/// <summary>
/// Resolves localized text in XAML: <c>Text="{loc:Str Nav_Home}"</c>.
/// <para>
/// Values are resolved once when the element is created. The language is chosen at startup and
/// applying a change reloads the shell, which avoids the cost and complexity of making every
/// binding observe a culture change for something users do rarely.
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public StrExtension() { }

    public StrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}

using System.Globalization;

namespace GamePulse.Desktop.Services;

/// <summary>Explicit, session-only UI language; numeric/date formatting keeps the OS culture.</summary>
public static class UiLanguage
{
    private static CultureInfo? _sessionCulture;
    public static CultureInfo Culture => _sessionCulture ?? CultureInfo.CurrentUICulture;
    public static bool IsUkrainian => Culture.TwoLetterISOLanguageName == "uk";
    public static event EventHandler? Changed;

    public static void ApplyEnvironment(string? fallback = null)
    {
        var requested = Environment.GetEnvironmentVariable("GAMEPULSE_UI_LANGUAGE") ?? fallback;
        var culture = CultureInfo.GetCultureInfo(string.Equals(requested, "uk", StringComparison.OrdinalIgnoreCase) ? "uk" : "en");
        _sessionCulture = null;
        ApplyCulture(culture);
    }

    /// <summary>Changes display resources only; OS number/date formatting and loaded data are preserved.</summary>
    public static void SetLanguage(string language)
    {
        var culture = CultureInfo.GetCultureInfo(string.Equals(language, "uk", StringComparison.OrdinalIgnoreCase) ? "uk" : "en");
        if (Culture.Name == culture.Name && _sessionCulture is not null) return;
        _sessionCulture = culture;
        ApplyCulture(culture);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}

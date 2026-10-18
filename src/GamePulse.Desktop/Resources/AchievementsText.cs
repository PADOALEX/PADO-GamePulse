using System.Globalization;
using System.Resources;

namespace GamePulse.Desktop.Resources;

/// <summary>Achievement UI text with English and Ukrainian resources.</summary>
public static class AchievementsText
{
    private static readonly ResourceManager Manager = new("GamePulse.Desktop.Resources.Achievements", typeof(AchievementsText).Assembly);
    public static string Get(string key) => Manager.GetString(key, GamePulse.Desktop.Services.UiLanguage.Culture) ?? key;
    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public static string Title => Get(nameof(Title));
    public static string Subtitle => Get(nameof(Subtitle));
    public static string WeightedLabel => Get(nameof(WeightedLabel));
    public static string AverageLabel => Get(nameof(AverageLabel));
    public static string CoverageLabel => Get(nameof(CoverageLabel));
    public static string LibraryLabel => Get(nameof(LibraryLabel));
    public static string GameSearch => Get(nameof(GameSearch));
    public static string Search => Get(nameof(Search));
    public static string Sort => Get(nameof(Sort));
    public static string UnlockFilter => Get(nameof(UnlockFilter));
    public static string Refresh => Get(nameof(Refresh));
    public static string Cancel => Get(nameof(Cancel));
    public static string UnlockedLabel => Get(nameof(UnlockedLabel));
    public static string TotalLabel => Get(nameof(TotalLabel));
    public static string DetailLabel => Get(nameof(DetailLabel));
    public static string SelectDetail => Get(nameof(SelectDetail));
    public static string ApiNameLabel => Get(nameof(ApiNameLabel));
    public static string DescriptionLabel => Get(nameof(DescriptionLabel));
    public static string HiddenLabel => Get(nameof(HiddenLabel));
    public static string UnlockedAtLabel => Get(nameof(UnlockedAtLabel));
    public static string StatusLabel => Get(nameof(StatusLabel));
    public static string SourcesNote => Get(nameof(SourcesNote));
    public static string Close => Get(nameof(Close));
}

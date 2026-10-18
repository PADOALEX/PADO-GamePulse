using System.Globalization;
using System.Resources;

namespace GamePulse.Desktop.Resources;

/// <summary>Library UI text with English and Ukrainian resources.</summary>
public static class GamesText
{
    private static readonly ResourceManager Manager = new("GamePulse.Desktop.Resources.Games", typeof(GamesText).Assembly);
    public static string Get(string key) => Manager.GetString(key, GamePulse.Desktop.Services.UiLanguage.Culture) ?? key;
    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public static string Title => Get(nameof(Title));
    public static string Subtitle => Get(nameof(Subtitle));
    public static string Library => Get(nameof(Library));
    public static string Recent => Get(nameof(Recent));
    public static string Search => Get(nameof(Search));
    public static string Minimum => Get(nameof(Minimum));
    public static string Maximum => Get(nameof(Maximum));
    public static string NeverPlayed => Get(nameof(NeverPlayed));
    public static string Reset => Get(nameof(Reset));
    public static string Game => Get(nameof(Game));
    public static string Lifetime => Get(nameof(Lifetime));
    public static string TwoWeeks => Get(nameof(TwoWeeks));
    public static string LastPlayed => Get(nameof(LastPlayed));
    public static string Store => Get(nameof(Store));
    public static string Copy => Get(nameof(Copy));
    public static string SelectHint => Get(nameof(SelectHint));
    public static string Returned => Get(nameof(Returned));
    public static string TotalHours => Get(nameof(TotalHours));
    public static string Unplayed => Get(nameof(Unplayed));
    public static string OwnedStatusLabel => Get(nameof(OwnedStatusLabel));
    public static string RecentStatusLabel => Get(nameof(RecentStatusLabel));
    public static string RecentNote => Get(nameof(RecentNote));
    public static string Sort => Get(nameof(Sort));
}

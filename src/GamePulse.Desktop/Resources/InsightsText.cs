using System.Globalization;
using System.Resources;

namespace GamePulse.Desktop.Resources;

public static class InsightsText
{
    private static readonly ResourceManager Manager = new("GamePulse.Desktop.Resources.Insights", typeof(InsightsText).Assembly);
    public static string Get(string key) => Manager.GetString(key, GamePulse.Desktop.Services.UiLanguage.Culture) ?? key;
    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public static string Title => Get(nameof(Title));
    public static string Subtitle => Get(nameof(Subtitle));
    public static string Available => Get(nameof(Available));
    public static string Total => Get(nameof(Total));
    public static string Unplayed => Get(nameof(Unplayed));
    public static string Concentration => Get(nameof(Concentration));
    public static string Distribution => Get(nameof(Distribution));
    public static string DistributionNote => Get(nameof(DistributionNote));
    public static string TopTitle => Get(nameof(TopTitle));
    public static string TopNote => Get(nameof(TopNote));
    public static string SnapshotNote => Get(nameof(SnapshotNote));
    public static string KnownTotal => Get(nameof(KnownTotal));
    public static string Store => Get(nameof(Store));
    public static string Copy => Get(nameof(Copy));
    public static string SelectHint => Get(nameof(SelectHint));
    public static string CoverageHint => Get(nameof(CoverageHint));
}

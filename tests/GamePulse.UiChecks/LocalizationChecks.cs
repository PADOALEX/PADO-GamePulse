using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using GamePulse.Desktop.Resources;

namespace GamePulse.UiChecks;

/// <summary>Checks the built satellite resources, including culture fallback used by the real views.</summary>
internal static class LocalizationChecks
{
    private static readonly Regex FormatItems = new(
        @"(?<!\{)\{\d+(?:,[^{}:]+)?(?::[^{}]+)?\}(?!\})",
        RegexOptions.CultureInvariant);

    public static void Run()
    {
        var assembly = typeof(UiText).Assembly;
        var culture = CultureInfo.GetCultureInfo("uk");
        // A missing satellite must fail instead of silently passing with English fallback.
        _ = assembly.GetSatelliteAssembly(culture);
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("GamePulse.Desktop.Resources.", StringComparison.Ordinal)
                && name.EndsWith(".resources", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Check(resourceNames.Length >= 6, "All UI resource modules must be checked.");
        var entries = 0;
        foreach (var resourceName in resourceNames)
        {
            var manager = new ResourceManager(resourceName[..^".resources".Length], assembly);
            try
            {
                var neutral = Read(manager.GetResourceSet(CultureInfo.InvariantCulture, true, false), resourceName);
                var ukrainian = Read(manager.GetResourceSet(culture, true, false), resourceName + ".uk");
                Check(neutral.Keys.Order(StringComparer.Ordinal).SequenceEqual(ukrainian.Keys.Order(StringComparer.Ordinal)),
                    $"Ukrainian resource keys must exactly match English: {resourceName}.");
                foreach (var (key, source) in neutral)
                {
                    var translated = ukrainian[key];
                    Check(!string.IsNullOrWhiteSpace(translated), $"Translation is empty: {resourceName}.{key}.");
                    Check(Placeholders(source).SequenceEqual(Placeholders(translated)),
                        $"Format placeholders or numeric/date format tokens changed: {resourceName}.{key}.");
                    entries++;
                }
            }
            finally { manager.ReleaseAllResources(); }
        }

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("uk-UA");
            Check(UiText.Explorer == "Огляд профілів", "Ukrainian regional culture must fall back to uk resources.");
            Check(GamesText.Title == "Огляд бібліотеки", "Games resources must use the active UI culture.");
            Check(ComparisonText.Title == "Порівняння профілів", "Comparison resources must use the active UI culture.");
            Check(FriendsText.Title == "Друзі", "Friends resources must use the active UI culture.");
            Check(AchievementsText.Title == "Досягнення", "Achievements resources must use the active UI culture.");
            Check(InventoryText.Title == "Інвентар", "Inventory resources must use the active UI culture.");
            Check(UiText.ProductName == "PADO GamePulse" && UiText.KeyName == "GAMEPULSE_STEAM_API_KEY",
                "Branding and the environment variable name must remain unchanged.");
            var states = new[] { "NoAchievements", "Private", "Unavailable", "Failed", "Loading", "Ready" }
                .Select(AchievementsText.Get).ToArray();
            Check(states.Distinct(StringComparer.Ordinal).Count() == states.Length,
                "Distinct achievement availability states must remain distinct in Ukrainian.");
            Check(UiText.Get("NotFound") != UiText.Get("Private") && UiText.Get("Private") != UiText.Get("Unavailable"),
                "Not-found, private and unavailable outcomes must keep separate explanations.");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Check(UiText.Explorer == "Profile Explorer" && InventoryText.Title == "Inventory",
                "Returning to English must restore the neutral resources.");
        }
        finally { CultureInfo.CurrentUICulture = previous; }
        Console.WriteLine($"Localization: {resourceNames.Length} modules, {entries} Ukrainian entries; exact keys and placeholders verified.");
    }

    private static Dictionary<string, string> Read(ResourceSet? set, string name)
    {
        Check(set is not null, $"Compiled resource set is missing: {name}.");
        return set!.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key,
            entry => entry.Value as string ?? throw new InvalidOperationException($"Non-text UI resource in {name}."),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> Placeholders(string value) => FormatItems.Matches(value)
        .Select(match => match.Value).Order(StringComparer.Ordinal);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

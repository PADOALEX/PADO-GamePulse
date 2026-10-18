using System.Globalization;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;
using Xunit;

namespace GamePulse.UnitTests;

// Environment variables and DefaultThreadCurrentUICulture are process-wide. xUnit runs
// this collection without overlapping other collections, and every test restores both.
[CollectionDefinition(UiLanguageCollection.Name, DisableParallelization = true)]
public sealed class UiLanguageCollection
{
    public const string Name = "UI language process state";
}

[Collection(UiLanguageCollection.Name)]
public sealed class UiLanguageTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("en", "en")]
    [InlineData("uk", "uk")]
    [InlineData("UK", "uk")]
    [InlineData("unsupported", "en")]
    public void Explicit_language_selects_resources_without_changing_number_or_date_culture(string? requested, string expected)
    {
        const string variable = "GAMEPULSE_UI_LANGUAGE";
        var previousVariable = Environment.GetEnvironmentVariable(variable);
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;
        var previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            var formatting = CultureInfo.GetCultureInfo("uk-UA");
            CultureInfo.CurrentCulture = formatting;
            Environment.SetEnvironmentVariable(variable, requested);

            UiLanguage.ApplyEnvironment();

            Assert.Equal(expected, CultureInfo.CurrentUICulture.Name);
            Assert.Equal(expected, CultureInfo.DefaultThreadCurrentUICulture?.Name);
            Assert.Same(formatting, CultureInfo.CurrentCulture);
            Assert.Same(previousDefaultCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.Equal(expected == "uk" ? "Огляд профілів" : "Profile Explorer", UiText.Explorer);
            Assert.Equal(expected == "uk" ? "Інвентар" : "Inventory", InventoryText.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousVariable);
            UiLanguage.ApplyEnvironment();
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUiCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Session_language_notifies_resources_and_translates_only_owned_status_snapshots()
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var formatting = CultureInfo.CurrentCulture;
        var changes = 0;
        void Changed(object? sender, EventArgs args) => changes++;
        try
        {
            UiLanguage.SetLanguage("en");
            var englishStatus = UiText.Get("Copied");
            var numericStatus = "47";
            var date = new DateTimeOffset(2026, 10, 18, 10, 15, 0, TimeSpan.Zero).ToLocalTime().ToString("g", formatting);
            var englishStamp = UiText.Updated + " " + date + " · " + UiText.Get("Cached");
            var comparisonStamp = string.Format(formatting, ComparisonText.UpdatedFormat,
                new DateTimeOffset(2026, 10, 18, 10, 15, 0, TimeSpan.Zero).ToLocalTime()) + " · " + ComparisonText.Cached;
            UiLanguage.Changed += Changed;

            UiLanguage.SetLanguage("uk");

            Assert.Equal(1, changes);
            Assert.Equal("Огляд профілів", LocalizedResources.For("Strings")["Explorer"]);
            Assert.Equal(UiText.Get("Copied"), LocalizedStatus.Read(englishStatus, "Strings"));
            Assert.Equal(numericStatus, LocalizedStatus.Read(numericStatus, "Strings"));
            Assert.Equal(UiText.Updated + " " + date + " · " + UiText.Get("Cached"), LocalizedStatus.Read(englishStamp, "Strings"));
            Assert.StartsWith(ComparisonText.UpdatedFormat.Split('{')[0], LocalizedStatus.Read(comparisonStamp, "Comparison"));
            Assert.EndsWith(" · " + ComparisonText.Cached, LocalizedStatus.Read(comparisonStamp, "Comparison"));
            Assert.Same(formatting, CultureInfo.CurrentCulture);
            UiLanguage.SetLanguage("uk");
            Assert.Equal(1, changes);
        }
        finally
        {
            UiLanguage.Changed -= Changed;
            UiLanguage.ApplyEnvironment();
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
        }
    }

    [Fact]
    public async Task Old_async_context_reads_the_selected_session_language()
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            UiLanguage.SetLanguage("en");
            var oldRequest = Task.Run(async () =>
            {
                await release.Task;
                return UiText.Explorer;
            });
            UiLanguage.SetLanguage("uk");
            release.SetResult();
            Assert.Equal("Огляд профілів", await oldRequest);
        }
        finally
        {
            release.TrySetResult();
            UiLanguage.ApplyEnvironment();
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
        }
    }
}

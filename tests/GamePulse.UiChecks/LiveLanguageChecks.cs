using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GamePulse.Desktop;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.UiChecks;

/// <summary>Exercises the real sidebar button without reconstructing the Window or its source models.</summary>
internal static class LiveLanguageChecks
{
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model)
    {
        var previousLanguage = UiLanguage.Culture.TwoLetterISOLanguageName;
        var formatting = CultureInfo.CurrentCulture;
        var originalInput = model.SearchInput;
        var content = window.Content;
        var profileId = model.SteamId;
        var comparison = model.Comparison ?? throw new InvalidOperationException("Comparison fixture is required.");
        var games = model.Games ?? throw new InvalidOperationException("Games fixture is required.");
        var friends = model.Friends ?? throw new InvalidOperationException("Friends fixture is required.");
        var achievements = model.Achievements ?? throw new InvalidOperationException("Achievement fixture is required.");
        var inventory = model.Inventory ?? throw new InvalidOperationException("Inventory fixture is required.");
        var insights = model.Insights ?? throw new InvalidOperationException("Library Pulse fixture is required.");
        await insights.RefreshAsync();
        insights.SelectedGame = insights.TopGames.FirstOrDefault();
        Check(insights.SelectedGame is not null, "A populated Library Pulse fixture is required.");
        var insightRows = insights.TopGames;
        var insightBuckets = insights.Buckets;
        var selectedInsight = insights.SelectedGame;
        var inputA = comparison.SideA.Input;
        var inputB = comparison.SideB.Input;
        var libraryResultA = comparison.SideA.LibraryResult;
        var libraryResultB = comparison.SideB.LibraryResult;
        var gameRows = games.Rows;
        var friendRows = friends.Rows;
        var achievementRows = achievements.AchievementRows;
        var achievementGames = achievements.LibraryRows;
        var inventoryRows = inventory.Rows;
        var mutualRows = comparison.MutualGames;
        var selectedGame = games.SelectedGame;
        var selectedFriend = friends.SelectedFriend;
        var selectedAchievementGame = achievements.SelectedGame;
        var selectedAchievement = achievements.SelectedAchievement;
        var selectedInventory = inventory.SelectedItem;
        var sorts = new[] { games.SelectedSortIndex, friends.SelectedSortIndex, inventory.SelectedSortIndex,
            achievements.SelectedGameSortIndex, achievements.SelectedUnlockFilterIndex,
            achievements.SelectedSortIndex, comparison.SortIndex };
        try
        {
            UiLanguage.SetLanguage("en");
            model.RefreshLocalization();
            model.ShowFriendsCommand.Execute(null);
            model.SearchInput = "Online"; // A user-entered word must not be translated into its UI-label equivalent.
            model.ShowLocalMessage("Copied");
            await SettleAsync(window);
            var button = window.FindControl<Button>("LanguageButton") ?? throw new InvalidOperationException("Language button missing.");
            var search = window.FindControl<TextBox>("ProfileInput")!;
            var friendFilter = window.GetVisualDescendants().OfType<TextBox>().First(x => x.Name == "FriendsFilterInput");
            friendFilter.Focus();
            var filterText = friendFilter.Text;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync(window);

            Check(UiLanguage.IsUkrainian && UiText.Explorer == "Огляд профілів", "The real language button must select Ukrainian.");
            Check(ReferenceEquals(content, window.Content) && ReferenceEquals(search, window.FindControl<TextBox>("ProfileInput")),
                "Changing language must preserve the existing control tree and editor state.");
            Check(model.IsFriends && model.SearchInput == "Online" && model.SteamId == profileId && friendFilter.Text == filterText,
                "Page, arbitrary user input and loaded profile must survive a language change.");
            Check(button.Content?.ToString() == "Мова · Українська" && (AutomationProperties.GetName(button) ?? "").Contains("українська", StringComparison.Ordinal),
                $"Button text and its accessible current-language explanation must refresh in place. Actual content={button.Content}; accessible name={AutomationProperties.GetName(button)}; resource={LocalizedResources.For("Strings")["LanguageButton"]}.");
            Check(window.FindControl<Button>("NavOverview")!.Content?.ToString() == "Огляд профілів" &&
                window.FindControl<Button>("NavFriends")!.Content?.ToString() == "Друзі", "Existing navigation labels must refresh.");
            Check(model.StatusMessage == UiText.Get("Copied") && comparison.SideA.Label == ComparisonText.ProfileA,
                "Stored application status and side labels must use the new language.");
            Check(games.SortOptions[0].Value == GamesText.Get("MostPlayed") && friends.SortOptions[0].Value == FriendsText.Get("SortStatus") &&
                inventory.SortOptions[0].Value == InventoryText.Get("SortName") && achievements.SortOptions[0].Value == AchievementsText.Get("SortName"),
                "Sort/filter option labels must refresh in the existing collections.");
            Check(sorts.SequenceEqual(new[] { games.SelectedSortIndex, friends.SelectedSortIndex, inventory.SelectedSortIndex,
                achievements.SelectedGameSortIndex, achievements.SelectedUnlockFilterIndex, achievements.SelectedSortIndex, comparison.SortIndex }),
                "Changing option labels must not reset selected sort or filter indices.");
            Check(ReferenceEquals(libraryResultA, comparison.SideA.LibraryResult) && ReferenceEquals(libraryResultB, comparison.SideB.LibraryResult)
                && comparison.SideA.Input == inputA && comparison.SideB.Input == inputB, "Both independent comparison profiles must stay loaded.");
            Check(ReferenceEquals(gameRows, games.Rows) && ReferenceEquals(friendRows, friends.Rows) &&
                ReferenceEquals(achievementRows, achievements.AchievementRows) && ReferenceEquals(achievementGames, achievements.LibraryRows) &&
                ReferenceEquals(inventoryRows, inventory.Rows) && ReferenceEquals(mutualRows, comparison.MutualGames),
                "Language changes must not rebuild/requery/filter the source lists.");
            Check(ReferenceEquals(selectedGame, games.SelectedGame) && ReferenceEquals(selectedFriend, friends.SelectedFriend) &&
                ReferenceEquals(selectedAchievementGame, achievements.SelectedGame) && ReferenceEquals(selectedAchievement, achievements.SelectedAchievement) &&
                ReferenceEquals(selectedInventory, inventory.SelectedItem), "Selections must survive live translation.");
            Check(ReferenceEquals(formatting, CultureInfo.CurrentCulture), "Number and date formatting must retain the OS culture.");

            model.ShowInsightsCommand.Execute(null);
            await SettleAsync(window);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync(window);
            Check(!UiLanguage.IsUkrainian && window.FindControl<Button>("NavOverview")!.Content?.ToString() == "Profile Explorer" &&
                button.Content?.ToString() == "Language · English", "The same mounted button must switch back to English.");
            Check(model.IsInsights && ReferenceEquals(insightRows, insights.TopGames) && ReferenceEquals(insightBuckets, insights.Buckets) &&
                ReferenceEquals(selectedInsight, insights.SelectedGame), "Visible Library Pulse lists and selection must survive translation in place.");
            Check(insights.Buckets[0].Label == InsightsText.Get("Range" + insights.Buckets[0].Bucket.Range),
                "Library Pulse bucket labels must follow the selected language.");
        }
        finally
        {
            UiLanguage.SetLanguage(previousLanguage);
            model.RefreshLocalization();
            model.SearchInput = originalInput;
            model.ShowOverviewCommand.Execute(null);
        }
        Console.WriteLine("Live language button: mounted controls, all source lists, selections, filters, independent comparison profiles and formatting preserved.");
    }

    private static async Task SettleAsync(MainWindow window)
    {
        // Wait beyond debounced filter delays: a transient ComboBox index reset must not
        // pass before it later replaces the user's source rows or selection.
        await Task.Delay(350);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

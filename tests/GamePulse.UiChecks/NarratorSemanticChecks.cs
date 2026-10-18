using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GamePulse.Desktop;
using GamePulse.Desktop.Controls;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.UiChecks;

/// <summary>
/// Checks real-view automation semantics and the peer events consumed by the Win32 live-region bridge.
/// These deterministic checks do not claim to capture Narrator's spoken audio.
/// </summary>
internal static class NarratorSemanticChecks
{
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model)
    {
        model.ShowOverviewCommand.Execute(null);
        await SettleAsync(window);
        var profileStatus = FindStatus(window, "ProfileStatus");
        await CheckNameChangesAsync(window, profileStatus, () =>
        {
            model.ShowLocalMessage("Copied");
            return Task.CompletedTask;
        }, () => UiText.Get("Copied"));

        var comparison = model.Comparison ?? throw new InvalidOperationException("Comparison fixture is required.");
        model.ShowComparisonCommand.Execute(null);
        await SettleAsync(window);
        Check(ControlAutomationPeer.CreatePeerForElement(profileStatus).GetLiveSetting() == AutomationLiveSetting.Off,
            "A cached overview peer must stop making live announcements when another page is active.");
        var comparisonStatus = FindStatus(window, "ComparisonStatus");
        await CheckNameChangesAsync(window, comparisonStatus, comparison.RefreshComparisonAsync, () => comparison.Status);

        var previousMinimum = comparison.MinimumHours;
        var filterError = FindStatus(window, "ComparisonFilterError");
        try
        {
            await CheckNameChangesAsync(window, filterError, async () =>
            {
                comparison.MinimumHours = "invalid hours for accessibility test";
                await comparison.ApplyFiltersAsync();
            }, () => comparison.FilterError);
            Check(comparison.HasFilterError && filterError.IsEffectivelyVisible,
                "An invalid comparison filter must expose its error through a visible live region.");
        }
        finally
        {
            comparison.MinimumHours = previousMinimum;
            await comparison.ApplyFiltersAsync();
        }

        var achievements = model.Achievements ?? throw new InvalidOperationException("Achievement fixture is required.");
        model.ShowAchievementsCommand.Execute(null);
        await SettleAsync(window);
        Check(ControlAutomationPeer.CreatePeerForElement(comparisonStatus).GetLiveSetting() == AutomationLiveSetting.Off,
            "A cached comparison peer must stop making live announcements when its page is hidden.");
        var libraryStatus = FindStatus(window, "AchievementLibraryStatus");
        var sort = window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "AchievementGameSort");
        var sortBounds = sort.TranslatePoint(default, window)!.Value;
        var statusBounds = libraryStatus.TranslatePoint(default, window)!.Value;
        Check(sortBounds.Y + sort.Bounds.Height <= statusBounds.Y + 0.5,
            "The achievement game sort control and library message must occupy separate rows.");

        var previousQuery = achievements.GameQuery;
        var previousSelection = achievements.SelectedGame;
        try
        {
            await CheckNameChangesAsync(window, libraryStatus, async () =>
            {
                achievements.GameQuery = "__no_matching_game_for_accessibility_check__";
                await achievements.GameFilteringTask;
            }, () => achievements.LibraryMessage);
            Check(achievements.LibraryRows.Count == 0, "The library live-region check must exercise the empty filter state.");
        }
        finally
        {
            achievements.GameQuery = previousQuery;
            await achievements.GameFilteringTask;
            if (previousSelection is not null)
            {
                achievements.SelectedGame = previousSelection;
                await achievements.SelectionLoadingTask;
            }
        }

        foreach (var status in window.GetVisualDescendants().OfType<LiveStatusTextBlock>())
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(status);
            Check(ReferenceEquals(peer, ControlAutomationPeer.CreatePeerForElement(status)),
                "Status peers must be cached per control rather than recreated for each update.");
            var expected = status.IsEffectivelyVisible ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off;
            Check(peer.GetLiveSetting() == expected,
                $"Live announcements must match effective page visibility for {status.Name}.");
        }
        foreach (var progress in window.GetVisualDescendants().OfType<ProgressBar>())
            Check(AutomationProperties.GetLiveSetting(progress) == AutomationLiveSetting.Off,
                "Indeterminate progress must not flood the screen reader with live announcements.");

        // Detached controls cannot represent an announcement in a displayed window.
        var detached = new LiveStatusTextBlock { Text = "Detached test status" };
        AutomationProperties.SetLiveSetting(detached, AutomationLiveSetting.Polite);
        Check(ControlAutomationPeer.CreatePeerForElement(detached).GetLiveSetting() == AutomationLiveSetting.Off,
            "Detached status peers must not expose an active live region.");
        foreach (var navigate in new[] { model.ShowOverviewCommand, model.ShowGamesCommand, model.ShowComparisonCommand,
                     model.ShowFriendsCommand, model.ShowAchievementsCommand, model.ShowInventoryCommand, model.ShowInsightsCommand })
        {
            navigate.Execute(null);
            await SettleAsync(window);
            var headings = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(control => control.IsEffectivelyVisible && AutomationProperties.GetHeadingLevel(control) == 1).ToArray();
            Check(headings.Length == 1 && !string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(headings[0]).GetName()),
                "Every active page needs one named level-one heading for screen-reader heading navigation.");
        }
        Console.WriteLine("Real-view live-region peer events, hidden-page suppression, page headings and achievement control separation passed.");
    }

    private static LiveStatusTextBlock FindStatus(Window window, string name)
    {
        var status = window.GetVisualDescendants().OfType<LiveStatusTextBlock>().Single(control => control.Name == name);
        Check(AutomationProperties.GetAutomationId(status) == name,
            $"The {name} live region needs a stable native automation identity.");
        return status;
    }

    private static async Task CheckNameChangesAsync(Window window, LiveStatusTextBlock status, Func<Task> change,
        Func<string> expectedText)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(status);
        Check(peer.GetLiveSetting() == AutomationLiveSetting.Polite,
            $"The active {status.Name} status must expose polite live semantics.");
        var nameEvents = new List<string>();
        void Changed(object? sender, AutomationPropertyChangedEventArgs args)
        {
            if (args.Property == AutomationElementIdentifiers.NameProperty && args.NewValue is string text)
                nameEvents.Add(text);
        }
        peer.PropertyChanged += Changed;
        try
        {
            await change();
            await SettleAsync(window);
            var expected = expectedText();
            Check(nameEvents.Count > 0 && nameEvents.Contains(expected, StringComparer.Ordinal) && peer.GetName() == expected,
                $"The {status.Name} bound update must raise a NameProperty event containing the actual status.");
            Check(!nameEvents.Zip(nameEvents.Skip(1)).Any(pair => pair.First == pair.Second),
                $"The {status.Name} update must not emit duplicate consecutive name events.");
        }
        finally { peer.PropertyChanged -= Changed; }
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(25);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

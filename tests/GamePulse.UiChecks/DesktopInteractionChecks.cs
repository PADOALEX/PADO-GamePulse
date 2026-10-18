using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GamePulse.Desktop;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.UiChecks;

/// <summary>Real view routing and automation properties; fixtures remain confined to this test assembly.</summary>
internal static class DesktopInteractionChecks
{
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model)
    {
        var profileInput = window.FindControl<TextBox>("ProfileInput")!;
        var destinations = new[]
        {
            (model.ShowOverviewCommand, "ProfileInput"),
            (model.ShowGamesCommand, "GamesFilterInput"),
            (model.ShowComparisonCommand, "MutualFilterInput"),
            (model.ShowFriendsCommand, "FriendsFilterInput"),
            (model.ShowAchievementsCommand, "AchievementGameFilterInput"),
            (model.ShowInventoryCommand, "InventoryFilterInput"),
            (model.ShowInsightsCommand, "ProfileInput")
        };
        foreach (var (navigate, filterName) in destinations)
        {
            navigate.Execute(null);
            await SettleAsync(window);
            var exportButton = window.FindControl<Button>("ExportButton")!;
            Check(exportButton.IsEnabled == (model.IsGames || model.IsComparison || model.IsInventory),
                "CSV export must be available only on supported table pages.");
            profileInput.Focus();
            Press(window, Key.F, RawInputModifiers.Control);
            var filter = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == filterName);
            Check(filter.IsFocused, $"Ctrl+F must focus the visible {filterName}.");
            Press(window, Key.L, RawInputModifiers.Control);
            Check(profileInput.IsFocused && profileInput.SelectedText == profileInput.Text,
                "Ctrl+L must focus and select the profile input from every page.");
        }

        model.ShowOverviewCommand.Execute(null);
        await SettleAsync(window);
        var profileBeforeExport = model.SteamId;
        Press(window, Key.E, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync(window);
        Check(model.HasActionMessage && model.ActionMessage == GamePulse.Desktop.Resources.UiText.Get("ExportUnavailable")
            && !model.IsExportBusy && model.SteamId == profileBeforeExport,
            "Export shortcut without a supported table must explain availability without launching a picker or changing the profile.");

        model.ShowGamesCommand.Execute(null);
        await SettleAsync(window);
        var gamesFilter = window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "GamesFilterInput");
        var escapeReachedControl = false;
        void HandleLocalEscape(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            escapeReachedControl = true;
            e.Handled = true;
        }
        gamesFilter.AddHandler(InputElement.KeyDownEvent, HandleLocalEscape, RoutingStrategies.Bubble);
        try
        {
            gamesFilter.Focus();
            Press(window, Key.Escape);
            Check(escapeReachedControl, "A focused control must receive Escape before global cancellation handles it.");
        }
        finally { gamesFilter.RemoveHandler(InputElement.KeyDownEvent, HandleLocalEscape); }

        var games = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "GamesList");
        games.SelectedIndex = 0;
        games.ScrollIntoView(0);
        await SettleAsync(window);
        Check(games.ContainerFromIndex(0)?.Focus() == true, "The selected virtualized game row must accept keyboard focus.");
        await SettleAsync(window);
        Press(window, Key.C, RawInputModifiers.Control);
        await SettleAsync(window);
        var clipboardText = window.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        var expectedAppId = ((GameRow)games.SelectedItem!).AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check(clipboardText == expectedAppId,
            "Ctrl+C on the game list must copy the selected AppID.");
        CheckAccessibleRows(games);

        // With a loaded profile, Settings can sit below the visible overview. Its action must reveal it.
        var settingsButton = window.FindControl<Button>("SettingsButton")!;
        settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(window);
        await SettleAsync(window);
        var settingsCard = window.FindControl<Border>("SettingsCard")!;
        var position = settingsCard.TranslatePoint(default, window);
        Check(model.IsOverview && model.ShowSettings && settingsCard.IsEffectivelyVisible && position is { } origin &&
              origin.Y >= 0 && origin.Y + settingsCard.Bounds.Height <= window.ClientSize.Height + 1,
            "Settings must reveal its complete setup/about card in the overview viewport.");
        settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        model.ShowGamesCommand.Execute(null);
        await SettleAsync(window);
    }

    public static void CheckAccessibleRows(ListBox list)
    {
        var containers = list.GetRealizedContainers().ToArray();
        Check(containers.Length > 0, $"{list.Name} must realize rows for automation validation.");
        foreach (var container in containers)
        {
            var (label, identity) = container.DataContext switch
            {
                GameRow row => (row.Name, row.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                MutualGameRow row => (row.Name, row.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                FriendRow row => (row.Name, row.SteamId),
                AchievementGameRow row => (row.Name, row.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                AchievementRow row => (row.Name, row.Status),
                InventoryRow row => (row.Name, row.AssetId),
                _ => throw new InvalidOperationException("Unexpected realized row type.")
            };
            var name = AutomationProperties.GetName(container);
            Check(!string.IsNullOrWhiteSpace(name) && name.Contains(label, StringComparison.Ordinal) && name.Contains(identity, StringComparison.Ordinal),
                $"{list.Name} must expose each realized row's name and identity/status through automation properties.");
        }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(80);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}

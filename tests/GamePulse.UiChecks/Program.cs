using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GamePulse.Core;
using GamePulse.Desktop;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GamePulse.UiChecks;

/// <summary>Test-only data and rendering. This assembly is never referenced by the desktop application.</summary>
internal static class Program
{
    private const string IdA = "76561198000000001";
    private const string IdB = "76561198000000002";
    private static int result = 1;

    [STAThread]
    public static int Main(string[] args)
    {
        AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);
        Environment.SetEnvironmentVariable("GAMEPULSE_DISABLE_DOTENV", "1");
        Environment.SetEnvironmentVariable("GAMEPULSE_UI_LANGUAGE", "en");
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var destination = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-checks");
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(destination); result = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { lifetime.Cancel(); }
        });
        try { Dispatcher.UIThread.MainLoop(lifetime.Token); }
        catch (OperationCanceledException) { }
        return result;
    }

    private static async Task RunAsync(string destination)
    {
        LocalizationChecks.Run();
        await AsyncThumbnailChecks.RunAsync();
        Directory.CreateDirectory(destination);
        var fixtures = new Fixtures();
        using var avatars = new AvatarService();
        using var games = new GamesViewModel(fixtures);
        using var comparison = new ComparisonViewModel(fixtures, fixtures, new ProfileComparisonService());
        var friendAvatars = new FixtureAvatarLoader();
        using var friends = new FriendsViewModel(fixtures, friendAvatars);
        using var achievements = new AchievementsViewModel(fixtures, fixtures);
        var inventoryIcons = new FixtureInventoryIconLoader();
        using var inventory = new InventoryViewModel(fixtures, inventoryIcons);
        using var vm = new MainWindowViewModel(fixtures, avatars, NullLogger<MainWindowViewModel>.Instance, games, comparison, friends, achievements, inventory);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 940 };
        var content = (Control)window.Content!;
        window.Content = null;
        var frame = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = new SolidColorBrush(Color.Parse("#101721")) };
        frame.Children.Add(new Border
        {
            Padding = new Thickness(18, 10), Background = new SolidColorBrush(Color.Parse("#71521B")),
            Child = new TextBlock { Text = "TEST FIXTURES · SYNTHETIC STEAM DATA · NOT LIVE PROFILES OR INVENTORIES", Foreground = Brushes.White, FontWeight = FontWeight.Bold }
        });
        Grid.SetRow(content, 1);
        frame.Children.Add(content);
        window.Content = frame;
        window.Show();
        vm.ShowGamesCommand.Execute(null);
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        await games.ApplyFiltersAsync();
        await SettleAsync(window);
        Check(games.Rows.Count == 12_000, "All 12,000 games must remain available.");
        await AvatarImageChecks.RunAsync();
        await DesktopInteractionChecks.RunAsync(window, vm);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "GamesList");
        Check(list.IsEffectivelyVisible, "Games navigation must show the Games page.");
        var hiddenMutual = window.GetVisualDescendants().OfType<GamePulse.Desktop.Views.ComparisonView>().Single();
        Check(!hiddenMutual.IsEffectivelyVisible, "Compare must not overlay the Games page.");
        var realizedStart = list.GetRealizedContainers().Count();
        Check(realizedStart is > 0 and < 200, "Games list must realize a bounded visible range.");
        Save(frame, Path.Combine(destination, "games-test-fixtures.png"));
        list.ScrollIntoView(11_999);
        await SettleAsync(window);
        var realizedEnd = list.GetRealizedContainers().Count();
        Check(list.ContainerFromIndex(11_999) is not null, "The final game must be reachable by scrolling.");
        Check(realizedEnd is > 0 and < 200, "Games list must recycle after scrolling.");

        games.Query = "100123";
        await games.ApplyFiltersAsync();
        Check(games.Rows.Count == 1 && games.Rows[0].AppId == 100123, "AppID search must find the exact fixture.");
        games.Query = "";
        games.MinimumHours = "10";
        games.MaximumHours = "20";
        await games.ApplyFiltersAsync();
        Check(games.Rows.Count > 0 && games.Rows.All(row => row.Game.PlaytimeMinutes is >= 600 and <= 1200), "Hour filters must apply to every returned game.");
        games.ResetFiltersCommand.Execute(null);
        games.ShowRecent = true;
        await games.ApplyFiltersAsync();
        Check(games.Rows.Count == 6, "Recent games must have their own complete source.");
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "recent-test-fixtures.png"));

        vm.ShowComparisonCommand.Execute(null);
        comparison.SideA.Input = IdA;
        comparison.SideB.Input = "test-profile-b";
        await comparison.CompareAsync(false);
        await comparison.ApplyFiltersAsync();
        await SettleAsync(window);
        Check(comparison.MutualGames.Count == 6000, "Intersection must contain all 6,000 mutual AppIDs.");
        var mutual = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "MutualGamesList");
        Check(mutual.IsEffectivelyVisible && !list.IsEffectivelyVisible, "Compare navigation must replace, rather than overlay, Games.");
        var mutualRealized = mutual.GetRealizedContainers().Count();
        Check(mutualRealized is > 0 and < 200, "Mutual list must virtualize its full source.");
        DesktopInteractionChecks.CheckAccessibleRows(mutual);
        Save(frame, Path.Combine(destination, "compare-test-fixtures.png"));
        mutual.ScrollIntoView(5999);
        await SettleAsync(window);
        Check(mutual.ContainerFromIndex(5999) is not null, "Last mutual game must be reachable.");
        Check(mutual.GetRealizedContainers().Count() < 200, "Mutual list must recycle after scrolling.");

        // Validate the actual view at the declared minimum size, without claiming a DPI matrix.
        window.Width = 980;
        window.Height = 680 + frame.Children[0].Bounds.Height; // Keep test banner outside the production viewport budget.
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "compare-minimum-test-fixtures.png"));
        Check(mutual.Bounds.Height > 70, "Minimum-size comparison must keep a usable list viewport.");
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        await SettleAsync(window);
        Check(mutual.Bounds.Height > 70, "Expanded facts must not consume the mutual games viewport.");
        Save(frame, Path.Combine(destination, "compare-minimum-expanded-test-fixtures.png"));

        foreach (var expander in window.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = false;
        window.Width = 1360;
        window.Height = 940;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "compare-light-test-fixtures.png"));
        fixtures.HideB = true;
        await comparison.SideB.SearchAsync(true);
        await comparison.RefreshComparisonAsync();
        await SettleAsync(window);
        Check(!comparison.HasComparison && comparison.VisibleSummary == "N/A", "A private library cannot yield a numeric mutual count.");
        Save(frame, Path.Combine(destination, "compare-unavailable-test-fixtures.png"));

        vm.ShowGamesCommand.Execute(null);
        games.ShowRecent = false;
        await games.ApplyFiltersAsync();
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "games-light-test-fixtures.png"));

        // Exercise the real Friends view with no HTTP and no production fixture dependency.
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        vm.ShowFriendsCommand.Execute(null);
        await friends.ApplyFiltersAsync();
        await SettleAsync(window);
        var friendList = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "FriendsList");
        Check(friendList.IsEffectivelyVisible && !list.IsEffectivelyVisible && !mutual.IsEffectivelyVisible,
            "Friends navigation must show only its own view.");
        Check(friends.Rows.Count == 12_000, "Every returned friend ID must remain available, including missing summaries.");
        var friendsRealizedStart = friendList.GetRealizedContainers().Count();
        Check(friendsRealizedStart is > 0 and < 200, "Friends must realize only a bounded visible range.");
        DesktopInteractionChecks.CheckAccessibleRows(friendList);
        Check(friendAvatars.Calls is > 0 and < 200,
            "Friend avatars must start before selection, for a bounded visible range only.");
        Check(friendList.GetVisualDescendants().OfType<GamePulse.Desktop.Controls.AsyncThumbnail>().Any(row => row.CurrentImage is not null),
            "Visible friend rows must display loaded avatars before any friend is selected.");
        friendList.ScrollIntoView(11_999);
        await SettleAsync(window);
        var friendsRealizedEnd = friendList.GetRealizedContainers().Count();
        Check(friendList.ContainerFromIndex(11_999) is not null && friendsRealizedEnd is > 0 and < 200,
            "The final friend must be reachable without disabling virtualization.");
        Check(friendAvatars.Calls < 400, "Scrolling to the final friend must not load every intermediate avatar.");

        friends.Query = "Test friend 00042";
        await friends.ApplyFiltersAsync();
        Check(friends.Rows.Count == 1 && friends.Rows[0].Name == "Test friend 00042", "Friend name search must filter the entire directory.");
        var selectedId = friends.Rows[0].SteamId;
        friends.Query = selectedId;
        await friends.ApplyFiltersAsync();
        Check(friends.Rows.Count == 1 && friends.Rows[0].SteamId == selectedId, "Friend SteamID search must find the exact ID.");
        friends.Query = "";
        friends.OnlineOnly = true;
        await friends.ApplyFiltersAsync();
        Check(friends.Rows.Count > 0 && friends.Rows.All(row => row.HasSummary && row.IsOnline), "Online filter must exclude offline and unavailable states.");
        friends.OnlineOnly = false;
        friends.SelectedSortIndex = (int)FriendSort.Name;
        await friends.ApplyFiltersAsync();
        var callsBeforeFriendSelection = friendAvatars.Calls;
        friends.SelectedFriend = friends.Rows.Single(row => row.SteamId == selectedId);
        await friends.AvatarLoadingTask;
        Check(friendAvatars.Calls == callsBeforeFriendSelection + 1, "The selected mini-profile must request its independently owned avatar.");
        var callsAfterFriendSelection = friendAvatars.Calls;
        friends.SelectedFriend = friends.SelectedFriend;
        await friends.AvatarLoadingTask;
        Check(friendAvatars.Calls == callsAfterFriendSelection, "Selecting the same friend must not repeat avatar work.");
        friendList.ScrollIntoView(friends.SelectedFriend);
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "friends-test-fixtures.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "friends-light-test-fixtures.png"));
        window.Width = 980;
        window.Height = 680 + frame.Children[0].Bounds.Height;
        await SettleAsync(window);
        Check(friendList.Bounds.Height > 100, "Minimum-size Friends must retain a usable list viewport.");
        Save(frame, Path.Combine(destination, "friends-minimum-test-fixtures.png"));
        var miniScroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "MiniProfileScroll");
        Check(miniScroll.Viewport.Height > 0 && miniScroll.Extent.Height > miniScroll.Viewport.Height,
            "Compact mini-profile details must have their own working scroll viewport.");
        miniScroll.Offset = new Vector(0, miniScroll.Extent.Height);
        await SettleAsync(window);
        Check(miniScroll.Offset.Y > 0, "The bottom of a compact mini-profile must remain reachable.");
        Save(frame, Path.Combine(destination, "friends-minimum-details-test-fixtures.png"));
        miniScroll.Offset = default;
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "friends-minimum-dark-test-fixtures.png"));

        // A missing summary must remain selectable without inventing a status or profile fields.
        friends.SelectedFriend = friends.Rows.First(row => !row.HasSummary);
        await friends.AvatarLoadingTask;
        Check(!friends.SelectedFriend.IsOnline && friends.SelectedFriend.Presence != "Offline" && friends.SelectedAvatar is null,
            "Missing summary must show unknown presence and clear the old avatar.");
        Check(friends.SelectedFriend.CreatedAt == "N/A", "A missing profile cannot have an invented account age.");
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "friends-unavailable-summary-test-fixtures.png"));

        // Raise real view button events. Navigation must preserve the independent opposite side.
        fixtures.HideB = false;
        comparison.SideB.Input = IdB;
        await comparison.SideB.SearchAsync(true);
        var untouchedB = comparison.SideB.LibraryResult;
        friends.SelectedFriend = friends.Rows.Single(row => row.SteamId == selectedId);
        window.Width = 1360;
        window.Height = 940;
        await SettleAsync(window);
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CompareFriendAButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => vm.IsComparison && comparison.SideA.SteamId == selectedId && !comparison.SideA.IsBusy, window);
        Check(comparison.SideB.SteamId == IdB && ReferenceEquals(untouchedB, comparison.SideB.LibraryResult),
            "Friend to Compare A must preserve Profile B and its already-loaded library.");
        vm.ShowFriendsCommand.Execute(null);
        var secondId = friends.Rows.First(row => row.HasSummary && row.SteamId != selectedId).SteamId;
        friends.SelectedFriend = friends.Rows.Single(row => row.SteamId == secondId);
        var untouchedA = comparison.SideA.LibraryResult;
        await SettleAsync(window);
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CompareFriendBButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => vm.IsComparison && comparison.SideB.SteamId == secondId && !comparison.SideB.IsBusy, window);
        Check(comparison.SideA.SteamId == selectedId && ReferenceEquals(untouchedA, comparison.SideA.LibraryResult),
            "Friend to Compare B must preserve Profile A and its already-loaded library.");
        vm.ShowFriendsCommand.Execute(null);
        await SettleAsync(window);
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenFriendButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => vm.IsOverview && vm.SteamId == secondId && !vm.IsBusy, window);
        Check(comparison.SideA.SteamId == selectedId && comparison.SideB.SteamId == secondId,
            "Opening a friend in Explorer must keep both comparison sides intact.");

        vm.ShowFriendsCommand.Execute(null);
        fixtures.FriendsState = DataState.Empty;
        await friends.LoadAsync(IdA, true, CancellationToken.None);
        Check(friends.State == DataState.Empty && friends.DirectoryCount == "0", "An explicitly accessible empty directory must display zero.");
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "friends-empty-test-fixtures.png"));
        fixtures.FriendsState = DataState.Unavailable;
        await friends.LoadAsync(IdA, true, CancellationToken.None);
        Check(friends.State == DataState.Unavailable && friends.DirectoryCount == "N/A", "A denied/missing directory must never display zero friends.");
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "friends-unavailable-test-fixtures.png"));

        // Loading profiles, games, friends or navigating must never initiate an achievements sweep.
        Check(fixtures.AchievementRequests == 0, "No achievements may be requested before an explicit game selection.");
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        vm.ShowAchievementsCommand.Execute(null);
        await achievements.ApplyGameFiltersAsync();
        await SettleAsync(window);
        Check(fixtures.AchievementRequests == 0 && achievements.SelectedGame is null,
            "Opening Achievements must keep the choice explicit and remain lazy.");
        var achievementGames = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "AchievementGamesList");
        var achievementList = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "AchievementsList");
        Check(achievementGames.IsEffectivelyVisible && !friendList.IsEffectivelyVisible && !mutual.IsEffectivelyVisible,
            "Achievements navigation must show only its own page.");
        Check(achievements.LibraryRows.Count == 12_000, "The achievement game picker must retain the complete library source.");
        var achievementGamesRealized = achievementGames.GetRealizedContainers().Count();
        Check(achievementGamesRealized is > 0 and < 200, "The game picker must virtualize its full source.");
        DesktopInteractionChecks.CheckAccessibleRows(achievementGames);
        achievementGames.ScrollIntoView(11_999);
        await SettleAsync(window);
        Check(achievementGames.ContainerFromIndex(11_999) is not null, "The final game must remain reachable in the achievement picker.");
        achievements.GameQuery = "100001";
        await achievements.ApplyGameFiltersAsync();
        Check(achievements.LibraryRows.Count == 1, "Achievement game picker must support AppID search.");
        achievements.GameQuery = "";
        await achievements.ApplyGameFiltersAsync();
        achievements.SelectedGame = achievements.LibraryRows.Single(row => row.AppId == 100001);
        await achievements.SelectionLoadingTask;
        await achievements.ApplyFiltersAsync();
        Check(fixtures.AchievementRequests == 1 && achievements.SelectedState == DataState.Ready,
            "Selecting one game must request only that game's achievements.");
        Check(achievements.AchievementRows.Count == 12_000, "Achievement rows must not be silently truncated.");
        Check(achievements.HasSelectedProgress && achievements.ProgressValue == 50,
            "A complete half-unlocked game must show its real numeric progress.");
        achievements.SelectedGameSortIndex = 2;
        await achievements.ApplyGameFiltersAsync();
        Check(achievements.SelectedGame?.AppId == 100001 && fixtures.AchievementRequests == 1,
            "Sorting the game picker must keep the current selection without another achievement query.");
        await SettleAsync(window);
        var achievementsRealized = achievementList.GetRealizedContainers().Count();
        Check(achievementsRealized is > 0 and < 200, "Achievement rows must virtualize.");
        DesktopInteractionChecks.CheckAccessibleRows(achievementList);
        achievementList.ScrollIntoView(11_999);
        await SettleAsync(window);
        Check(achievementList.ContainerFromIndex(11_999) is not null, "The final achievement must be reachable.");
        Check(achievementList.GetRealizedContainers().Count() < 200, "Achievement virtualization must survive last-row scrolling.");
        achievements.Query = "TEST_00123";
        await achievements.ApplyFiltersAsync();
        Check(achievements.AchievementRows.Count == 1, "API-name search must find an achievement beyond the initial viewport.");
        achievements.Query = "";
        achievements.SelectedUnlockFilterIndex = (int)AchievementUnlockFilter.Unlocked;
        await achievements.ApplyFiltersAsync();
        Check(achievements.AchievementRows.Count == 6000, "Unlocked filter must cover the entire selected game.");
        achievements.SelectedUnlockFilterIndex = (int)AchievementUnlockFilter.Locked;
        await achievements.ApplyFiltersAsync();
        Check(achievements.AchievementRows.Count == 6000, "Locked filter must cover the entire selected game.");
        achievements.SelectedUnlockFilterIndex = (int)AchievementUnlockFilter.All;
        await achievements.ApplyFiltersAsync();
        achievements.SelectedAchievement = achievements.AchievementRows.Single(row => row.ApiName == "TEST_00002");
        achievementList.ScrollIntoView(achievements.SelectedAchievement);
        await SettleAsync(window);
        var inlineDetails = window.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "AchievementDetailsExpander");
        Check(inlineDetails.IsEffectivelyVisible && inlineDetails.IsExpanded, "A selected achievement must expose its inline details at full size.");
        var inlineDetailScroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "AchievementDetailScroll");
        Check(inlineDetailScroll.Extent.Height > inlineDetailScroll.Viewport.Height, "Long inline details must remain scrollable.");
        Save(frame, Path.Combine(destination, "achievements-test-fixtures.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "achievements-light-test-fixtures.png"));
        window.Width = 980;
        window.Height = 680 + frame.Children[0].Bounds.Height;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "achievements-minimum-test-fixtures.png"));
        Check(achievementList.Bounds.Height > 70 && achievementGames.Bounds.Height > 70,
            $"Compact Achievements must retain usable finite list viewports: games={achievementGames.Bounds.Height}, achievements={achievementList.Bounds.Height}.");
        var detailsButton = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "OpenAchievementDetailsButton");
        Check(detailsButton.IsEffectivelyVisible && detailsButton.IsEnabled && !inlineDetails.IsEffectivelyVisible,
            "Compact details must be accessible without consuming the achievement list viewport.");
        detailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(window);
        var dialog = window.OwnedWindows.Single(control => control.Name == "AchievementDetailsDialog");
        Check(dialog.IsVisible, "The compact details button must open an owned readable window.");
        var dialogContent = (Control)dialog.Content!;
        dialog.Content = null;
        var dialogFrame = new Grid { RowDefinitions = new("Auto,*"), Background = dialog.Background };
        dialogFrame.Children.Add(new Border
        {
            Padding = new Thickness(12, 8), Background = new SolidColorBrush(Color.Parse("#71521B")),
            Child = new TextBlock { Text = "SYNTHETIC ACHIEVEMENT · TEST FIXTURE · NOT LIVE STEAM DATA", Foreground = Brushes.White,
                FontWeight = FontWeight.Bold, FontSize = 10, TextWrapping = TextWrapping.Wrap }
        });
        Grid.SetRow(dialogContent, 1); dialogFrame.Children.Add(dialogContent); dialog.Content = dialogFrame;
        await SettleAsync(dialog);
        Save(dialogFrame, Path.Combine(destination, "achievements-detail-dialog-test-fixtures.png"));
        var dialogScroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "AchievementDetailScroll");
        Check(dialogScroll.Extent.Height > dialogScroll.Viewport.Height && dialogScroll.Viewport.Height > 100,
            "A long description and all fields must be readable in the compact detail dialog.");
        dialogScroll.Offset = new Vector(0, dialogScroll.Extent.Height);
        await SettleAsync(dialog);
        Check(dialogScroll.Offset.Y > 0, "The final detail fields must be reachable by scrolling.");
        Save(dialogFrame, Path.Combine(destination, "achievements-detail-dialog-bottom-test-fixtures.png"));
        dialog.Close();
        await SettleAsync(window);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "achievements-minimum-dark-test-fixtures.png"));
        window.Width = 1360;
        window.Height = 940;

        foreach (var (appId, expected) in new (uint, DataState)[]
        {
            (100002, DataState.Ready), (100003, DataState.Ready), (100004, DataState.NoAchievements),
            (100005, DataState.Private), (100006, DataState.Unavailable), (100007, DataState.Failed),
            (100008, DataState.RateLimited), (100009, DataState.MissingKey), (100010, DataState.Ready)
        })
        {
            achievements.SelectedGame = achievements.LibraryRows.Single(row => row.AppId == appId);
            await achievements.SelectionLoadingTask;
            Check(achievements.SelectedState == expected, $"Selected app {appId} must preserve its explicit {expected} outcome.");
            if (appId is 100002 or 100003)
                Check(achievements.HasSelectedProgress && achievements.ProgressValue == (appId == 100002 ? 0 : 100),
                    "Zero and complete progress must remain valid numeric states.");
            else if (appId == 100010)
                Check(!achievements.HasSelectedProgress && achievements.SelectedProgress == "N/A",
                    "A partial achievement source must never manufacture a completion percentage.");
            await SettleAsync(window);
            if (appId == 100004) Save(frame, Path.Combine(destination, "achievements-none-test-fixtures.png"));
            if (appId == 100005) Save(frame, Path.Combine(destination, "achievements-private-test-fixtures.png"));
            if (appId == 100010) Save(frame, Path.Combine(destination, "achievements-partial-test-fixtures.png"));
        }
        Check(fixtures.AchievementRequests == 10, "Only the ten explicitly chosen games may have been requested.");
        var achievementFilter = new AchievementFilter("Test achievement", AchievementUnlockFilter.Unlocked, AchievementSort.Name);
        _ = AchievementQuery.Apply(fixtures.LargeAchievements.Achievements, achievementFilter);
        var achievementAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var achievementWatch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) _ = AchievementQuery.Apply(fixtures.LargeAchievements.Achievements, achievementFilter);
        achievementWatch.Stop();
        var achievementAllocation = GC.GetAllocatedBytesForCurrentThread() - achievementAllocationBefore;

        Check(fixtures.InventoryRequests == 0, "Opening profiles and other tools must not fetch inventories.");
        vm.ShowInventoryCommand.Execute(null);
        await SettleAsync(window);
        var inventoryList = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "InventoryList");
        Check(inventoryList.IsEffectivelyVisible && !achievementList.IsEffectivelyVisible && !friendList.IsEffectivelyVisible,
            "Inventory navigation must display one feature page.");
        Check(fixtures.InventoryRequests == 0 && inventory.Rows.Count == 0, "Inventory must wait for explicit loading.");
        window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "LoadInventoryButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => fixtures.InventoryRequests == 1 && !inventory.IsBusy && inventory.Rows.Count == 12_000, window);
        Check(inventory.IsComplete && inventory.LoadedAssetCount == 12_000.ToString("N0"), "The entire complete inventory must remain available.");
        Check(inventory.QuantityCount == 36_000.ToString("N0"), "Quantity totals must count amount, not just asset rows.");
        var inventoryRealized = inventoryList.GetRealizedContainers().Count();
        Check(inventoryRealized is > 0 and < 200, "Inventory must realize only a bounded visible range.");
        DesktopInteractionChecks.CheckAccessibleRows(inventoryList);
        Check(inventoryIcons.Calls is > 0 and < 200,
            "Inventory icons must start before selection, for a bounded visible range only.");
        Check(inventoryList.GetVisualDescendants().OfType<GamePulse.Desktop.Controls.AsyncThumbnail>().Any(row => row.CurrentImage is not null),
            "Visible inventory rows must display their icons before any item is selected.");
        inventoryList.ScrollIntoView(11_999);
        await SettleAsync(window);
        Check(inventoryList.ContainerFromIndex(11_999) is not null, "The final inventory asset must remain reachable.");
        Check(inventoryList.GetRealizedContainers().Count() < 200 && inventoryIcons.Calls < 400,
            "Inventory scrolling must keep virtualization and load only the displayed ranges.");
        inventory.Query = "Test item 00042";
        await inventory.ApplyFiltersAsync();
        Check(inventory.Rows.Count == 1, "Inventory search must cover the full source.");
        var chosenAsset = inventory.Rows[0].AssetId;
        inventory.Query = chosenAsset;
        await inventory.ApplyFiltersAsync();
        Check(inventory.Rows.Count == 1 && inventory.Rows[0].AssetId == chosenAsset, "AssetID search must find the exact item.");
        inventory.Query = ""; inventory.MarketableOnly = true; inventory.TradableOnly = true;
        await inventory.ApplyFiltersAsync();
        Check(inventory.Rows.Count > 0 && inventory.Rows.All(row => row.Item.Marketable == true && row.Item.Tradable == true),
            "Known-true inventory filters must exclude unknown flags.");
        inventory.MarketableOnly = false; inventory.TradableOnly = false;
        await inventory.ApplyFiltersAsync();
        var callsBeforeItemSelection = inventoryIcons.Calls;
        inventory.SelectedItem = inventory.Rows.Single(row => row.AssetId == chosenAsset);
        await inventory.IconLoadingTask;
        Check(inventoryIcons.Calls == callsBeforeItemSelection + 1, "The selected item must request its independently owned detail image.");
        var selectedDetailIcon = inventory.SelectedIcon;
        inventory.SelectedSortIndex = (int)InventorySort.AmountDescending;
        await inventory.ApplyFiltersAsync();
        Check(inventory.SelectedItem?.AssetId == chosenAsset && ReferenceEquals(inventory.SelectedIcon, selectedDetailIcon),
            "Sorting must keep the selected item and must not repeat its icon request.");
        Check(inventory.Rows[0].Item.Amount == 5, "Amount sorting must use numeric quantities.");
        inventory.SelectedSortIndex = (int)InventorySort.Name;
        await inventory.ApplyFiltersAsync();
        inventoryList.ScrollIntoView(inventory.SelectedItem!);
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "inventory-test-fixtures.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "inventory-light-test-fixtures.png"));
        window.Width = 980;
        window.Height = 680 + frame.Children[0].Bounds.Height;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "inventory-minimum-test-fixtures.png"));
        Check(inventoryList.Bounds.Height > 100, $"Compact inventory needs a usable list viewport; actual {inventoryList.Bounds.Height}.");
        var inventoryDetails = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "InventoryDetailScroll");
        Check(inventoryDetails.Viewport.Height > 0 && inventoryDetails.Extent.Height > inventoryDetails.Viewport.Height,
            "Compact item details must have an independent scroll viewport.");
        inventoryDetails.Offset = new Vector(0, inventoryDetails.Extent.Height);
        await SettleAsync(window);
        Check(inventoryDetails.Offset.Y > 0, "The final inventory detail fields must be reachable.");
        Save(frame, Path.Combine(destination, "inventory-minimum-details-test-fixtures.png"));
        inventoryDetails.Offset = default;
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "inventory-minimum-dark-test-fixtures.png"));
        window.Width = 1360; window.Height = 940;
        inventory.SelectedItem = inventory.Rows.First(row => !row.Item.HasDescription);
        await inventory.IconLoadingTask;
        Check(inventory.SelectedIcon is null && inventory.SelectedItem.Marketable == "Unknown" && inventory.SelectedItem.Tradable == "Unknown",
            "Missing descriptions must leave flags and artwork unknown, with the asset still selectable.");
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "inventory-missing-metadata-test-fixtures.png"));
        var requestsBeforeSourceChange = fixtures.InventoryRequests;
        inventory.SelectedAppIndex = 5;
        Check(inventory.ContextId == "6" && inventory.Rows.Count == 0 && fixtures.InventoryRequests == requestsBeforeSourceChange,
            "Changing to Steam Community must choose its editable context without automatic network calls.");
        inventory.ContextId = "7";
        Check(fixtures.InventoryRequests == requestsBeforeSourceChange, "Editing a context must not start a request.");
        await inventory.LoadAsync(false);
        Check(fixtures.LastInventoryRequest is { AppId: 753, ContextId: "7" }, "The configured context must reach the service unchanged.");
        inventory.SelectedAppIndex = 0;
        fixtures.InventoryPartial = true;
        await inventory.LoadAsync(true);
        Check(inventory.State == DataState.Ready && !inventory.IsComplete && inventory.Rows.Count == 2345 && inventory.TotalAssetCount == "N/A",
            "An interrupted inventory must keep loaded assets and leave global totals unavailable.");
        await SettleAsync(window);
        Save(frame, Path.Combine(destination, "inventory-partial-test-fixtures.png"));
        fixtures.InventoryPartial = false;
        foreach (var expected in new[] { DataState.Empty, DataState.Unavailable, DataState.RateLimited, DataState.Failed })
        {
            fixtures.InventoryState = expected;
            await inventory.LoadAsync(true);
            Check(inventory.State == expected, "Inventory must preserve its source availability outcome.");
            Check(inventory.TotalAssetCount == (expected == DataState.Empty ? "0" : "N/A"), "Only an explicitly empty inventory establishes zero assets.");
            await SettleAsync(window);
            if (expected == DataState.Empty) Save(frame, Path.Combine(destination, "inventory-empty-test-fixtures.png"));
            if (expected == DataState.Unavailable) Save(frame, Path.Combine(destination, "inventory-unavailable-test-fixtures.png"));
        }
        fixtures.InventoryState = DataState.Ready;
        fixtures.HoldInventory = true;
        var loadingInventory = inventory.LoadAsync(true);
        await SettleAsync(window);
        Check(inventory.IsBusy && inventory.ProgressText.Contains("1"), "Loading must expose source-page progress.");
        window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "CancelInventoryButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await loadingInventory.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!inventory.IsBusy && inventory.Rows.Count == 0, "The real cancel button must finish loading without stale rows.");
        fixtures.HoldInventory = false;
        var iconTransportPassed = await CheckInventoryIconTransportAsync();
        var inventoryFilter = new InventoryFilter("Test item", InventorySort.AmountDescending, MarketableOnly: true);
        _ = InventoryQuery.Apply(fixtures.InventoryItems, inventoryFilter);
        var inventoryAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var inventoryWatch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) _ = InventoryQuery.Apply(fixtures.InventoryItems, inventoryFilter);
        inventoryWatch.Stop();
        var inventoryAllocation = GC.GetAllocatedBytesForCurrentThread() - inventoryAllocatedBefore;

        var filter = new LibraryFilter("Test game", 0, 1000, Sort: LibrarySort.MostPlayed);
        _ = LibraryQuery.Apply(fixtures.LibraryA.Games, filter);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) _ = LibraryQuery.Apply(fixtures.LibraryA.Games, filter);
        watch.Stop();
        var allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var compareWatch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) _ = new ProfileComparisonService().Compare(Outcome<OwnedLibrary>.Ready(fixtures.LibraryA), Outcome<OwnedLibrary>.Ready(fixtures.LibraryB));
        compareWatch.Stop();
        var friendFilter = new FriendFilter("Test friend", FriendSort.Name, true);
        _ = FriendQuery.Apply(fixtures.Directory.Friends, friendFilter);
        var friendAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var friendWatch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) _ = FriendQuery.Apply(fixtures.Directory.Friends, friendFilter);
        friendWatch.Stop();
        var friendAllocation = GC.GetAllocatedBytesForCurrentThread() - friendAllocatedBefore;
        // DPI scenarios use populated sources rather than passing against empty placeholder lists.
        fixtures.FriendsState = DataState.Ready;
        fixtures.HideB = false;
        fixtures.InventoryState = DataState.Ready;
        fixtures.InventoryPartial = false;
        vm.SearchInput = IdA;
        await vm.SearchAsync(false);
        games.SelectedModeIndex = 0;
        games.ResetFiltersCommand.Execute(null);
        await games.ApplyFiltersAsync();
        comparison.SideA.Input = IdA; comparison.SideB.Input = IdB;
        comparison.Query = ""; comparison.MinimumHours = "";
        await comparison.CompareAsync(false);
        achievements.GameQuery = ""; achievements.Query = "";
        await achievements.ApplyGameFiltersAsync();
        achievements.SelectedGame = achievements.LibraryRows.First(row => row.AppId == 100001);
        await achievements.SelectionLoadingTask;
        await inventory.LoadAsync(true);
        await NarratorSemanticChecks.RunAsync(window, vm);
        await LiveLanguageChecks.RunAsync(window, vm);
        await DpiAccessibilityChecks.RunAsync(window, vm, destination);
        await LocalizedLayoutChecks.RunAsync(fixtures, fixtures, fixtures, fixtures, fixtures, destination);
        var metrics = new
        {
            Kind = "Synthetic test data; local observations, not Steam or frame-rate benchmarks",
            Games = 12_000, MutualGames = 6000, RealizedGamesStart = realizedStart, RealizedGamesEnd = realizedEnd,
            RealizedMutual = mutualRealized, FilterIterations = 10, FilterTotalMilliseconds = watch.Elapsed.TotalMilliseconds,
            FilterAllocatedBytes = allocation, ComparisonIterations = 10, ComparisonTotalMilliseconds = compareWatch.Elapsed.TotalMilliseconds,
            Friends = 12_000, RealizedFriendsStart = friendsRealizedStart, RealizedFriendsEnd = friendsRealizedEnd,
            FriendsFilterIterations = 10, FriendsFilterTotalMilliseconds = friendWatch.Elapsed.TotalMilliseconds,
            FriendsFilterAllocatedBytes = friendAllocation, FriendsNavigationButtonsPassed = true,
            AchievementGames = 12_000, Achievements = 12_000, RealizedAchievementGames = achievementGamesRealized,
            RealizedAchievements = achievementsRealized, AchievementRequestsBeforeSelection = 0,
            ExplicitlySelectedGames = fixtures.AchievementRequests, AchievementFilterIterations = 10,
            AchievementFilterTotalMilliseconds = achievementWatch.Elapsed.TotalMilliseconds,
            AchievementFilterAllocatedBytes = achievementAllocation, AchievementAvailabilityStatesPassed = true,
            InventoryAssets = 12_000, InventoryUnits = 36_000, RealizedInventory = inventoryRealized,
            InventoryRequestsBeforeExplicitLoad = 0, InventoryAvailabilityStatesPassed = true,
            InventoryIconTransportPassed = iconTransportPassed, InventoryFilterIterations = 10,
            InventoryFilterTotalMilliseconds = inventoryWatch.Elapsed.TotalMilliseconds,
            InventoryFilterAllocatedBytes = inventoryAllocation,
            DesktopKeyboardAndSettingsPassed = true, AccessibleVirtualizedRowsPassed = true, AvatarImageDecodeChecksPassed = true,
            Runtime = Environment.Version.ToString(), Passed = true
        };
        File.WriteAllText(Path.Combine(destination, "ui-check-results.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(metrics));
        window.Close();
    }

    private static async Task<bool> CheckInventoryIconTransportAsync()
    {
        byte[] png;
        using (var bitmap = new RenderTargetBitmap(new PixelSize(4, 4), new Vector(96, 96)))
        using (var bytes = new MemoryStream())
        {
            var sample = new Border { Width = 4, Height = 4, Background = Brushes.MediumAquamarine };
            sample.Measure(new Size(4, 4)); sample.Arrange(new Rect(0, 0, 4, 4)); bitmap.Render(sample);
            bitmap.Save(bytes, PngBitmapEncoderOptions.Default); png = bytes.ToArray();
        }
        var transport = new ImageResponses(png);
        var clock = new ImageClock();
        using var icons = new InventoryIconService(new HttpClient(transport), clock);
        const string url = "https://community.fastly.steamstatic.com/economy/image/test-image/256fx256f";
        using (var first = await icons.LoadAsync(url, default))
        using (var second = await icons.LoadAsync(url, default))
        {
            Check(first is not null && second is not null && !ReferenceEquals(first, second), "Cached icons must return caller-owned independent bitmaps.");
            Check(transport.Calls == 1, "Repeated selected icons must hit the encoded-byte cache.");
        }
        clock.Advance(TimeSpan.FromMinutes(31));
        using (var expired = await icons.LoadAsync(url, default)) Check(expired is not null && transport.Calls == 2, "Icon TTL expiry must perform a new key-free request.");
        for (var i = 0; i < 50; i++)
        { using var item = await icons.LoadAsync($"https://community.fastly.steamstatic.com/economy/image/test-{i}/256fx256f", default); Check(item is not null, "Valid tiny test icons must decode."); }
        var calls = transport.Calls;
        using (var evicted = await icons.LoadAsync(url, default)) Check(evicted is not null && transport.Calls == calls + 1, "Icon entry limits must evict old images.");
        using (var wide = new RenderTargetBitmap(new PixelSize(10000, 1), new Vector(96, 96)))
        using (var bytes = new MemoryStream())
        {
            var sample = new Border { Width = 10000, Height = 1, Background = Brushes.MediumAquamarine };
            sample.Measure(new Size(10000, 1)); sample.Arrange(new Rect(0, 0, 10000, 1)); wide.Render(sample);
            wide.Save(bytes, PngBitmapEncoderOptions.Default); transport.Bytes = bytes.ToArray();
        }
        using var rejected = await icons.LoadAsync("https://community.fastly.steamstatic.com/economy/image/oversized-dimensions/256fx256f", default);
        Check(rejected is null, "Image dimensions must be validated before decoding a bitmap.");
        return true;
    }

    private sealed class ImageClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class ImageResponses(byte[] bytes) : HttpMessageHandler
    {
        public byte[] Bytes { get; set; } = bytes;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Check(request.RequestUri?.Scheme == "https" && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Icon requests must remain HTTPS without credentials.");
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
            response.Content.Headers.ContentType = new("image/png");
            return Task.FromResult(response);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> completed, Window window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!completed()) { timeout.Token.ThrowIfCancellationRequested(); await SettleAsync(window); }
        await SettleAsync(window);
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(120);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Save(Control content, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(content.Bounds.Width), (int)Math.Ceiling(content.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private sealed class FixtureAvatarLoader : IAvatarLoader
    {
        public int Calls { get; private set; }
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult<Bitmap?>(CreateThumbnailFixture(false)); }
    }

    private sealed class FixtureInventoryIconLoader : IInventoryIconLoader
    {
        public int Calls { get; private set; }
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult<Bitmap?>(CreateThumbnailFixture(true)); }
    }

    private static Bitmap CreateThumbnailFixture(bool inventory)
    {
        var sample = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse(inventory ? "#233F54" : "#31504C")),
            Child = new TextBlock
            {
                Text = inventory ? "◆" : "●", Foreground = Brushes.MediumAquamarine,
                FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        sample.Measure(new Size(48, 48)); sample.Arrange(new Rect(0, 0, 48, 48));
        var bitmap = new RenderTargetBitmap(new PixelSize(48, 48), new Vector(96, 96));
        bitmap.Render(sample);
        return bitmap;
    }

    private sealed class Fixtures : IProfileLookupService, ILibraryService, IFriendsService, IAchievementService, IInventoryService
    {
        public OwnedLibrary LibraryA { get; } = Build(1);
        public OwnedLibrary LibraryB { get; } = Build(6001);
        public bool HideB { get; set; }
        public DataState FriendsState { get; set; } = DataState.Ready;
        public int InventoryRequests { get; private set; }
        public InventoryRequest? LastInventoryRequest { get; private set; }
        public DataState InventoryState { get; set; } = DataState.Ready;
        public bool InventoryPartial { get; set; }
        public bool HoldInventory { get; set; }
        public IReadOnlyList<InventoryItem> InventoryItems { get; } = Enumerable.Range(1, 12_000).Select(i =>
        {
            var missing = i % 17 == 0;
            return new InventoryItem((80_000_000_000_000_000UL + (ulong)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (100_000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), (i % 2).ToString(), (ulong)(i % 5 + 1),
                missing ? null : $"Test item {i:D5}", missing ? null : $"Test type {i % 4}",
                missing ? null : $"https://community.fastly.steamstatic.com/economy/image/test-fixture-{i}/256fx256f",
                missing ? null : i % 3 == 0, missing ? null : i % 2 == 0, !missing);
        }).ToArray();
        public async Task<Outcome<InventorySnapshot>> GetInventoryAsync(InventoryRequest request, bool force,
            IProgress<InventoryProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); InventoryRequests++; LastInventoryRequest = request;
            progress?.Report(new(1, 2000, 12_000));
            if (HoldInventory) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            await Task.Delay(15, token);
            if (InventoryState is not (DataState.Ready or DataState.Empty))
                return Outcome<InventorySnapshot>.Failure(InventoryState, "Synthetic availability outcome");
            var items = InventoryState == DataState.Empty ? Array.Empty<InventoryItem>() : InventoryPartial ? InventoryItems.Take(2345).ToArray() : InventoryItems;
            return new(InventoryState, new(request, items, InventoryState == DataState.Empty ? 0 : 12_000,
                !InventoryPartial, InventoryPartial ? 2 : 6, InventoryPartial ? DataState.RateLimited : null), UpdatedAt: DateTimeOffset.UtcNow);
        }
        public int AchievementRequests { get; private set; }
        public GameAchievements LargeAchievements { get; } = new(100001, "Test game 00001",
            Enumerable.Range(1, 12_000).Select(i => new AchievementDetail($"TEST_{i:D5}", $"Test achievement {i:D5}",
                i == 2 ? string.Join(" ", Enumerable.Repeat("Synthetic detail description for testing readable wrapping and scrolling.", 12)) : $"Complete test objective {i} (synthetic fixture).",
                i % 7 == 0, i % 2 == 0, i % 2 == 0 ? DateTimeOffset.FromUnixTimeSeconds(1_780_000_000 + i * 100) : null)).ToArray(), true);
        public Task<Outcome<GameAchievements>> GetAchievementsAsync(string id, uint app, bool force, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); AchievementRequests++;
            var state = app switch
            {
                100004 => DataState.NoAchievements, 100005 => DataState.Private, 100006 => DataState.Unavailable,
                100007 => DataState.Failed, 100008 => DataState.RateLimited, 100009 => DataState.MissingKey,
                _ => DataState.Ready
            };
            if (state == DataState.NoAchievements) return Task.FromResult(new Outcome<GameAchievements>(state, new(app, "No achievements fixture", [], true)));
            if (state != DataState.Ready) return Task.FromResult(Outcome<GameAchievements>.Failure(state, "Synthetic availability outcome"));
            var data = app == 100001 ? LargeAchievements : new GameAchievements(app, $"Test game {app}",
                Enumerable.Range(1, 6).Select(i => new AchievementDetail($"TEST_{i:D5}", $"Test achievement {i:D5}",
                    "Synthetic objective", false, app == 100010 ? null : app == 100003, null)).ToArray(), app != 100010);
            return Task.FromResult(Outcome<GameAchievements>.Ready(data));
        }
        public FriendDirectory Directory { get; } = new(Enumerable.Range(1, 12_000).Select(i =>
        {
            var id = (76561198010000000L + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var missing = i % 17 == 0;
            SteamProfile? profile = missing ? null : new(id, $"Test friend {i:D5}",
                "https://steamcommunity.com/profiles/" + id,
                $"https://avatars.steamstatic.com/test-fixture-{i}.jpg", 3, i % 8 == 0 ? null : i % 7,
                DateTimeOffset.FromUnixTimeSeconds(1_400_000_000 + i * 100),
                DateTimeOffset.FromUnixTimeSeconds(1_780_000_000 + i * 100), i % 3 == 0 ? "Test game (fixture)" : null);
            return new SteamFriend(id, i % 5 == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(1_600_000_000 + i * 100),
                profile, missing ? DataState.Unavailable : DataState.Ready);
        }).ToArray(), false, 1);
        public Task<Outcome<FriendDirectory>> GetFriendsAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(FriendsState switch
            {
                DataState.Ready => Outcome<FriendDirectory>.Ready(Directory),
                DataState.Empty => new Outcome<FriendDirectory>(DataState.Empty, new FriendDirectory([], true, 0)),
                _ => Outcome<FriendDirectory>.Failure(FriendsState, "Synthetic directory outcome")
            });
        private static OwnedLibrary Build(int start) => new(Enumerable.Range(start, 12_000).Select(i =>
            new LibraryGame((uint)(100_000 + i), $"Test game {i:D5}", i % 400 * 15L,
                i % 7 == 0 ? 120L : null, DateTimeOffset.FromUnixTimeSeconds(1_780_000_000 + i * 30))).ToArray(), 12_000, true);
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken)
        {
            var isA = input == IdA;
            var id = ProfileInput.Parse(input).Data is { Kind: ProfileInputKind.SteamId } parsed ? parsed.Value : IdB;
            var name = Directory.Friends.FirstOrDefault(friend => friend.SteamId == id)?.Profile?.DisplayName;
            return Task.FromResult(Outcome<SteamProfile>.Ready(new(id, name ?? (isA ? "Test profile A" : "Test profile B"),
                "https://steamcommunity.com/profiles/" + id, null, 3, isA ? 1 : 3,
                DateTimeOffset.FromUnixTimeSeconds(isA ? 1_400_000_000 : 1_500_000_000), null, null)));
        }
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(Outcome<ProfileLevel>.Ready(new(steamId == IdA ? 42 : 28)));
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(Outcome<SteamBans>.Ready(new(false, false, 0, 0, null, "none")));
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(
            steamId != IdA && HideB ? Outcome<OwnedLibrary>.Failure(DataState.Private, "Private test fixture") : Outcome<OwnedLibrary>.Ready(steamId == IdA ? LibraryA : LibraryB));
        public Task<Outcome<RecentGames>> GetRecentAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(Outcome<RecentGames>.Ready(new(LibraryA.Games.Take(6).ToArray(), 6)));
    }
}

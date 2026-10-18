using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GamePulse.Core;
using GamePulse.Desktop;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace GamePulse.UiChecks;

/// <summary>Fresh Ukrainian views with test-only sources; this is not native monitor or live Steam evidence.</summary>
internal static class LocalizedLayoutChecks
{
    public static async Task RunAsync(IProfileLookupService profiles, ILibraryService libraries,
        IFriendsService friendSource, IAchievementService achievementSource, IInventoryService inventorySource,
        string destination)
    {
        var application = Application.Current ?? throw new InvalidOperationException("A headless application is required.");
        var previousCulture = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        var previousTheme = application.RequestedThemeVariant;
        MainWindow? window = null;
        MainWindowViewModel? model = null;
        try
        {
            // Fresh Ukrainian layout complements the separate in-place language-button check.
            UiLanguage.SetLanguage("uk");
            var ukrainian = UiLanguage.Culture;
            using var avatars = new AvatarService();
            var images = new EmptyFixtureImages();
            var games = new GamesViewModel(libraries);
            var comparison = new ComparisonViewModel(profiles, libraries, new ProfileComparisonService());
            var friends = new FriendsViewModel(friendSource, images);
            var achievements = new AchievementsViewModel(libraries, achievementSource);
            var inventory = new InventoryViewModel(inventorySource, images);
            model = new MainWindowViewModel(profiles, avatars, NullLogger<MainWindowViewModel>.Instance,
                games, comparison, friends, achievements, inventory);
            window = new MainWindow { DataContext = model, Width = 1260, Height = 900 };
            window.SetRenderScaling(1);
            window.Show();
            model.SearchInput = "76561198000000001";
            await model.SearchAsync(false);
            await games.ApplyFiltersAsync();
            await friends.ApplyFiltersAsync();
            comparison.SideA.Input = "76561198000000001";
            comparison.SideB.Input = "76561198000000002";
            await comparison.CompareAsync(false);
            await comparison.ApplyFiltersAsync();
            await achievements.ApplyGameFiltersAsync();
            achievements.SelectedGame = achievements.LibraryRows.First(row => row.AppId == 100001);
            await achievements.SelectionLoadingTask;
            await achievements.ApplyFiltersAsync();
            await inventory.LoadAsync(false);
            await inventory.ApplyFiltersAsync();
            friends.SelectedFriend = friends.Rows.First();
            inventory.SelectedItem = inventory.Rows.First(row => row.Item.HasDescription);
            await inventory.IconLoadingTask;
            Check(games.Rows.Count == 12_000 && friends.Rows.Count == 12_000 && inventory.Rows.Count == 12_000,
                "The localized matrix must exercise complete large populated sources.");
            Check(comparison.MutualGames.Count == 6000 && achievements.AchievementRows.Count == 12_000,
                "The localized matrix must exercise populated mutual games and selected-game achievements.");
            Check(Equals(window.FindControl<Button>("NavOverview")?.Content, UiText.Explorer),
                "Fresh navigation must use the active UI resources.");

            var pages = new (string Page, ICommand Navigate, string Filter)[]
            {
                ("overview", model.ShowOverviewCommand, "ProfileInput"),
                ("games", model.ShowGamesCommand, "GamesFilterInput"),
                ("comparison", model.ShowComparisonCommand, "MutualFilterInput"),
                ("friends", model.ShowFriendsCommand, "FriendsFilterInput"),
                ("achievements", model.ShowAchievementsCommand, "AchievementGameFilterInput"),
                ("inventory", model.ShowInventoryCommand, "InventoryFilterInput"),
                ("insights", model.ShowInsightsCommand, "ProfileInput")
            };
            var results = new List<object>();
            foreach (var viewport in new[] { (Width: 1260, Height: 900), (Width: 840, Height: 480) })
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeManager.HighContrast })
            {
                application.RequestedThemeVariant = theme;
                window.Width = viewport.Width;
                window.Height = viewport.Height;
                await SettleAsync(window);
                foreach (var page in pages)
                {
                    page.Navigate.Execute(null);
                    await SettleAsync(window);
                    window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
                    window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
                    await SettleAsync(window);
                    var focused = window.FocusManager?.GetFocusedElement() as TextBox;
                    Check(focused is not null && focused.Name == page.Filter && focused.IsEffectivelyVisible,
                        $"Localized {page.Page} filter must receive keyboard focus at {viewport}/{theme}.");
                    CheckReachable(focused!, window, $"Localized {page.Page} filter");
                    var visibleLists = window.GetVisualDescendants().OfType<ListBox>()
                        .Where(list => list.IsEffectivelyVisible).ToArray();
                    foreach (var list in visibleLists)
                    {
                        Check(double.IsFinite(list.Bounds.Height) && list.Bounds.Height >= 24 && list.Bounds.Width > 0,
                            $"Localized {list.Name} lost its finite viewport at {viewport}/{theme}: {list.Bounds}.");
                        var realized = list.GetRealizedContainers().Count();
                        Check(realized < 200 && (list.ItemCount == 0 || realized > 0),
                            $"Localized {list.Name} must realize a bounded populated range at {viewport}/{theme}.");
                    }
                    var globalInput = window.FindControl<TextBox>("ProfileInput")!;
                    Check(globalInput.Bounds.Width >= 100, "Global profile lookup must remain usable with Ukrainian labels.");
                    CheckReachable(globalInput, window, "Localized global profile lookup");
                    results.Add(new
                    {
                        Culture = ukrainian.Name, viewport.Width, viewport.Height, Theme = theme.Key.ToString(), page.Page,
                        FocusedFilter = focused!.Name,
                        Lists = visibleLists.Select(list => new { list.Name, list.ItemCount,
                            Height = list.Bounds.Height, Realized = list.GetRealizedContainers().Count() }).ToArray(),
                        Passed = true
                    });
                    if (viewport.Width == 840 && (theme == ThemeVariant.Dark || page.Page == "inventory"))
                        SaveLabeled(window, Path.Combine(destination,
                            $"uk-minimum-{theme.Key.ToString()!.ToLowerInvariant()}-{page.Page}-fixtures.png"));
                    if (viewport.Width == 1260 && theme == ThemeVariant.Dark && page.Page is "insights" or "comparison" or "games")
                        SaveLabeled(window, Path.Combine(destination, $"uk-dark-{page.Page}-fixtures.png"));
                }
                var settings = window.FindControl<Button>("SettingsButton")!;
                settings.BringIntoView();
                settings.Focus(NavigationMethod.Tab);
                await SettleAsync(window);
                CheckReachable(settings, window, "Localized sidebar settings");
            }
            File.WriteAllText(Path.Combine(destination, "localized-layout-results.json"), JsonSerializer.Serialize(new
            {
                Kind = "Synthetic real-view Ukrainian layout checks; not native display, Narrator audio or live Steam evidence",
                Culture = ukrainian.Name, Configurations = 6, PagesPerConfiguration = 7, PageChecks = results.Count,
                Results = results, Passed = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Ukrainian layout: 6 size/theme configurations and 42 populated page checks passed.");
        }
        finally
        {
            window?.Close();
            model?.Dispose();
            UiLanguage.SetLanguage(previousCulture.TwoLetterISOLanguageName);
            CultureInfo.CurrentUICulture = previousCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
            application.RequestedThemeVariant = previousTheme;
        }
    }

    private static void CheckReachable(Control control, Window window, string description)
    {
        var point = control.TranslatePoint(default, window);
        Check(point.HasValue && point.Value.X >= -1 && point.Value.Y >= -1 &&
            point.Value.X + control.Bounds.Width <= window.ClientSize.Width + 1 &&
            point.Value.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
            $"{description} must remain inside the logical viewport: {point}, {control.Bounds.Size}, {window.ClientSize}.");
    }

    private static void SaveLabeled(Window window, string destination)
    {
        // Keep the test label outside the measured app viewport, so a production 840x480
        // layout is checked at exactly that size rather than losing height to the label.
        using var content = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.ClientSize.Width),
            (int)Math.Ceiling(window.ClientSize.Height)), new Vector(96, 96));
        content.Render((Visual)window.Content!);
        var frame = new Grid { RowDefinitions = new("Auto,*"), Width = window.ClientSize.Width,
            Background = Brushes.Black };
        frame.Children.Add(new Border
        {
            Padding = new Thickness(12, 8), Background = new SolidColorBrush(Color.Parse("#71521B")),
            Child = new TextBlock { Text = "TEST FIXTURES · СИНТЕТИЧНІ ДАНІ · НЕ РЕАЛЬНІ STEAM-ПРОФІЛІ", FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontWeight = FontWeight.Bold }
        });
        var image = new Image { Source = content, Width = window.ClientSize.Width, Height = window.ClientSize.Height };
        Grid.SetRow(image, 1); frame.Children.Add(image);
        frame.Measure(new Size(window.ClientSize.Width, double.PositiveInfinity));
        frame.Arrange(new Rect(frame.DesiredSize));
        using var labeled = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(frame.Bounds.Width),
            (int)Math.Ceiling(frame.Bounds.Height)), new Vector(96, 96));
        labeled.Render(frame);
        labeled.Save(destination, PngBitmapEncoderOptions.Default);
    }

    private static async Task SettleAsync(Window window)
    { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(25); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class EmptyFixtureImages : IAvatarLoader, IInventoryIconLoader
    {
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<Bitmap?>(null); }
    }
}

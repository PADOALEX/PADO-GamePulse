using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GamePulse.Desktop;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.UiChecks;

internal static class DpiAccessibilityChecks
{
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, string destination)
    {
        var results = new List<object>();
        foreach (var display in new[] { (Width: 1920, Height: 1080), (Width: 2560, Height: 1440) })
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeManager.HighContrast })
        {
            Application.Current!.RequestedThemeVariant = theme;
            window.SetRenderScaling(scale);
            window.Width = Math.Min(1260, (display.Width - 48) / scale);
            window.Height = Math.Min(900, (display.Height - 64) / scale);
            await Settle(window);
            var input = window.FindControl<TextBox>("ProfileInput")!;
            Check(input.Bounds.Width >= 100, "Global profile input must remain usable at every logical viewport.");
            Check(Math.Abs(window.RenderScaling - scale) < 0.001, "The actual headless render scale must change.");
            foreach (var navigate in new[] { model.ShowOverviewCommand, model.ShowGamesCommand, model.ShowComparisonCommand,
                         model.ShowFriendsCommand, model.ShowAchievementsCommand, model.ShowInventoryCommand, model.ShowInsightsCommand })
            {
                navigate.Execute(null); await Settle(window);
                window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
                window.KeyRelease(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
                await Settle(window);
                var focused = window.FocusManager?.GetFocusedElement() as TextBox
                    ?? throw new InvalidOperationException("Every page filter must be keyboard reachable.");
                Check(focused.IsEffectivelyVisible, "Focused filter must be visible.");
                var origin = focused.TranslatePoint(default, window)!.Value;
                Check(origin.Y >= 0 && origin.Y + focused.Bounds.Height <= window.ClientSize.Height + 1,
                    $"Focused filter clipped at {display.Width}/{scale}/{theme}: {origin.Y} + {focused.Bounds.Height} / {window.ClientSize.Height}");
                foreach (var list in window.GetVisualDescendants().OfType<ListBox>().Where(x => x.IsEffectivelyVisible))
                {
                    Check(double.IsFinite(list.Bounds.Height) && list.Bounds.Height >= 24,
                        $"Virtualized list {list.Name} lost its viewport at {display.Width}/{scale}/{theme}: {list.Bounds.Height}");
                    Check(list.GetRealizedContainers().Count() < 100, "DPI changes must preserve virtualization.");
                    if (list.ItemCount > 0)
                        Check(list.GetRealizedContainers().Any(), "Populated DPI lists must realize visible rows.");
                }
            }
            var settings = window.FindControl<Button>("SettingsButton")!;
            settings.BringIntoView(); settings.Focus(NavigationMethod.Tab); await Settle(window);
            var point = settings.TranslatePoint(default, window)!.Value;
            Check(point.Y >= 0 && point.Y + settings.Bounds.Height <= window.ClientSize.Height + 1, "Settings must be reachable in sidebar scroll.");
            results.Add(new { display.Width, display.Height, Scale = scale, Theme = theme.Key.ToString(),
                LogicalWidth = window.Width, LogicalHeight = window.Height, Passed = true });
            if (display.Width == 1920 && scale == 2)
            {
                model.ShowInventoryCommand.Execute(null); await Settle(window);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width * scale),
                    (int)(window.ClientSize.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render((Visual)window.Content!);
                bitmap.Save(Path.Combine(destination, $"dpi-200-{theme.Key.ToString()!.ToLowerInvariant()}-fixtures.png"), PngBitmapEncoderOptions.Default);
            }
        }
        File.WriteAllText(Path.Combine(destination, "dpi-accessibility-results.json"), JsonSerializer.Serialize(new {
            Kind = "Synthetic real-view headless DPI changes, not a physical monitor or Narrator certification",
            Configurations = results.Count, PagesPerConfiguration = 7, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        window.SetRenderScaling(1);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Console.WriteLine("24 DPI/theme configurations and 168 page checks passed.");
    }
    private static async Task Settle(Window window)
    { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(25); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

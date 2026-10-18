using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.Desktop.Views;

public partial class InsightsView : UserControl
{
    public InsightsView() => AvaloniaXamlLoader.Load(this);
    private void TopGames_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control || DataContext is not InsightsViewModel { HasSelection: true }) return;
        CopyAppId_OnClick(sender, e);
        e.Handled = true;
    }
    private async void CopyAppId_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InsightsViewModel { SelectedGame: { AppId: > 0 } game } model) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { model.ShowLocalMessage("ClipboardFailed"); return; }
            await clipboard.SetTextAsync(game.AppId.ToString(CultureInfo.InvariantCulture));
            model.ShowLocalMessage("Copied");
        }
        catch (Exception) { model.ShowLocalMessage("ClipboardFailed"); }
    }
    private async void OpenStore_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InsightsViewModel { SelectedGame: { AppId: > 0 } game } model) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher ||
                !await launcher.LaunchUriAsync(new Uri($"https://store.steampowered.com/app/{game.AppId.ToString(CultureInfo.InvariantCulture)}/")))
                model.ShowLocalMessage("ExternalFailed");
        }
        catch (Exception) { model.ShowLocalMessage("ExternalFailed"); }
    }
}

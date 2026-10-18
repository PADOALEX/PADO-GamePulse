using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePulse.Core;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.Desktop.Views;

public partial class InventoryView : UserControl
{
    public InventoryView() => AvaloniaXamlLoader.Load(this);
    private async void Load_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryViewModel model) await model.LoadAsync(false);
    }
    private async void Refresh_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryViewModel model) await model.LoadAsync(true);
    }
    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryViewModel model) model.Cancel();
    }
    private void InventoryList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control || DataContext is not InventoryViewModel { HasSelection: true }) return;
        CopyAsset_OnClick(sender, e); e.Handled = true;
    }
    private async void CopyAsset_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryViewModel { SelectedItem: { } item } model || !InventoryRequestValidation.IsDecimalId(item.AssetId)) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { model.ShowLocalMessage("ClipboardFailed"); return; }
            await clipboard.SetTextAsync(item.AssetId); model.ShowLocalMessage("Copied");
        }
        catch (Exception) { model.ShowLocalMessage("ClipboardFailed"); }
    }
    private async void OpenSteam_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryViewModel { HasProfile: true } model ||
            !InventoryRequestValidation.Validate(new(model.SteamId, model.SelectedApp.AppId, model.ContextId)).IsReady) return;
        try
        {
            var uri = new Uri($"https://steamcommunity.com/profiles/{model.SteamId}/inventory/#{model.SelectedApp.AppId}_{model.ContextId}");
            if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher || !await launcher.LaunchUriAsync(uri)) model.ShowLocalMessage("ExternalFailed");
        }
        catch (Exception) { model.ShowLocalMessage("ExternalFailed"); }
    }
}

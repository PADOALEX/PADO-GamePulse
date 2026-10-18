using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePulse.Core;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.Desktop.Views;

public partial class ComparisonView : UserControl
{
    private bool? _compactLayout;

    public ComparisonView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, _) => AdaptToViewport();
    }

    private void AdaptToViewport()
    {
        var compact = Bounds.Height < 600;
        if (_compactLayout == compact) return;
        _compactLayout = compact;
        if (this.FindControl<Grid>("ComparisonLayout") is not { } layout) return;
        // Profile panels retain their own scroll viewers; short windows reserve more
        // room for the mutual game's finite viewport rather than clipping its rows.
        // The adaptive header owns its scrolling budget; the results always keep the star row.
        layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
    }

    private void Input_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: ComparisonSideViewModel side }) return;
        side.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private async void CopyAppId_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: uint appId } || appId == 0) return;
        await CopyAsync(appId.ToString(CultureInfo.InvariantCulture));
    }

    private async void CopySteamId_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id } || !IsSteamId(id)) return;
        await CopyAsync(id);
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { Message("ClipboardFailed"); return; }
            await clipboard.SetTextAsync(text);
            Message("Copied");
        }
        catch (Exception) { Message("ClipboardFailed"); }
    }

    private async void OpenStore_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: uint appId } || appId == 0) return;
        await OpenAsync(new Uri("https://store.steampowered.com/app/" + appId.ToString(CultureInfo.InvariantCulture) + "/"));
    }

    private async void OpenProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string id } || !IsSteamId(id)) return;
        await OpenAsync(new Uri("https://steamcommunity.com/profiles/" + id));
    }

    private async Task OpenAsync(Uri uri)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(uri)) Message("ExternalFailed");
        }
        catch (Exception) { Message("ExternalFailed"); }
    }

    private static bool IsSteamId(string id) => ProfileInput.Parse(id) is { IsReady: true, Data.Kind: ProfileInputKind.SteamId };
    private void Message(string key) => (DataContext as ComparisonViewModel)?.ShowLocalMessage(key);
}

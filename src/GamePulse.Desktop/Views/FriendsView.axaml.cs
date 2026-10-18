using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePulse.Core;
using GamePulse.Desktop.ViewModels;

namespace GamePulse.Desktop.Views;

public partial class FriendsView : UserControl
{
    public FriendsView() => AvaloniaXamlLoader.Load(this);
    private void OpenExplorer_OnClick(object? sender, RoutedEventArgs e) => Request(FriendDestination.Explorer);
    private void CompareA_OnClick(object? sender, RoutedEventArgs e) => Request(FriendDestination.CompareA);
    private void CompareB_OnClick(object? sender, RoutedEventArgs e) => Request(FriendDestination.CompareB);
    private void FriendsList_OnDoubleTapped(object? sender, TappedEventArgs e) => Request(FriendDestination.Explorer);
    private void FriendsList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && DataContext is FriendsViewModel { HasSelection: true })
        {
            CopyId_OnClick(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        Request(FriendDestination.Explorer);
        e.Handled = true;
    }
    private void Request(FriendDestination destination)
    {
        if (DataContext is FriendsViewModel model) model.RequestAction(destination);
    }
    private async void CopyId_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FriendsViewModel { SelectedFriend: { } friend } model || !Valid(friend.SteamId)) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            { model.ShowLocalMessage("ClipboardFailed"); return; }
            await clipboard.SetTextAsync(friend.SteamId);
            model.ShowLocalMessage("Copied");
        }
        catch (Exception) { model.ShowLocalMessage("ClipboardFailed"); }
    }
    private async void OpenSteam_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FriendsViewModel { SelectedFriend: { } friend } model || !Valid(friend.SteamId)) return;
        try
        {
            // Build a known HTTPS endpoint from the validated numeric ID, never from untrusted API URLs.
            if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher ||
                !await launcher.LaunchUriAsync(new Uri($"https://steamcommunity.com/profiles/{friend.SteamId}/")))
                model.ShowLocalMessage("ExternalFailed");
        }
        catch (Exception) { model.ShowLocalMessage("ExternalFailed"); }
    }
    private static bool Valid(string id) => ProfileInput.Parse(id).Data is { Kind: ProfileInputKind.SteamId } parsed && parsed.Value == id;
}

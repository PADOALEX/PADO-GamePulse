using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using GamePulse.Desktop.ViewModels;
using GamePulse.Desktop.Services;

namespace GamePulse.Desktop;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _exportLifetime = new();
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => AdaptToViewport();
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        // Bubble Escape so a dropdown or other focused control can dismiss itself first.
        AddHandler(KeyDownEvent, OnUnhandledKeyDown, RoutingStrategies.Bubble);
        Opened += (_, _) => this.FindControl<TextBox>("ProfileInput")?.Focus();
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.Cancel();
        Closed += (_, _) => { _exportLifetime.Cancel(); _exportLifetime.Dispose(); };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void AdaptToViewport()
    {
        var compact = Bounds.Height < 720;
        Classes.Set("compact", compact);
        if (this.FindControl<Grid>("ShellLayout") is { } shell)
            shell.ColumnDefinitions[0].Width = new GridLength(Bounds.Width < 1100 ? 188 : 216);
        if (this.FindControl<Border>("BrandMark") is { } mark) mark.IsVisible = !compact;
        if (this.FindControl<TextBlock>("BrandSubtitle") is { } subtitle) subtitle.IsVisible = !compact;
        if (this.FindControl<TextBlock>("SearchExplanation") is { } hint) hint.IsVisible = !compact;
        if (this.FindControl<Border>("SearchHeader") is { } header) header.Padding = compact ? new Thickness(18, 10) : new Thickness(30, 24, 30, 18);
        if (this.FindControl<Border>("SidebarBorder") is { } sidebar) sidebar.Padding = compact ? new Thickness(12, 12) : new Thickness(20, 26, 20, 30);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.E && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            Export_OnClick(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control)
        {
            var input = this.FindControl<TextBox>("ProfileInput");
            input?.Focus();
            input?.SelectAll();
            input?.BringIntoView();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            var name = DataContext is MainWindowViewModel model ? model switch
            {
                { IsGames: true } => "GamesFilterInput",
                { IsComparison: true } => "MutualFilterInput",
                { IsFriends: true } => "FriendsFilterInput",
                { IsAchievements: true } => "AchievementGameFilterInput",
                { IsInventory: true } => "InventoryFilterInput",
                _ => "ProfileInput"
            } : "ProfileInput";
            var input = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);
            input?.Focus();
            input?.SelectAll();
            input?.BringIntoView();
            e.Handled = true;
        }
    }

    private void OnUnhandledKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        (DataContext as MainWindowViewModel)?.Cancel();
        e.Handled = true;
    }

    private void Settings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model) return;
        model.ToggleSettingsCommand.Execute(null);
        if (!model.ShowSettings) return;
        Dispatcher.UIThread.Post(() => this.FindControl<Border>("SettingsCard")?.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void Language_OnClick(object? sender, RoutedEventArgs e)
    {
        UiLanguage.SetLanguage(UiLanguage.IsUkrainian ? "en" : "uk");
        (DataContext as MainWindowViewModel)?.RefreshLocalization();
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || model.IsExportBusy) return;
        var table = AnalysisExport.Capture(model);
        if (table is null) { model.ShowActionMessage("ExportUnavailable"); return; }
        model.IsExportBusy = true;
        model.ShowActionMessage("ExportWorking");
        var token = _exportLifetime.Token;
        try
        {
            var result = await CsvExportService.SaveAsync(this, table, token);
            if (token.IsCancellationRequested) return;
            if (result == ExportSaveResult.Saved) model.ShowActionMessage("ExportSaved", table.Rows.Count);
            else model.ShowActionMessage(result == ExportSaveResult.Cancelled ? "ExportCancelled" : "ExportFailed");
        }
        finally { if (!token.IsCancellationRequested) model.IsExportBusy = false; }
    }

    private void ProfileInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel model) return;
        model.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private async void CopyId_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { HasProfile: true } model) return;
        try
        {
            if (Clipboard is null) { model.ShowLocalMessage("ClipboardFailed"); return; }
            await Clipboard.SetTextAsync(model.SteamId);
            model.ShowLocalMessage("Copied");
        }
        catch (Exception) { model.ShowLocalMessage("ClipboardFailed"); }
    }

    private async void OpenSteam_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { HasProfile: true } model) return;
        // Generated from a validated API SteamID, never an arbitrary API-returned link or input URL.
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(model.ProfileLink))) model.ShowLocalMessage("ExternalFailed");
        }
        catch (Exception) { model.ShowLocalMessage("ExternalFailed"); }
    }
}

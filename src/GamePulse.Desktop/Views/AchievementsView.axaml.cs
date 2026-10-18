using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.ViewModels;
using System.ComponentModel;

namespace GamePulse.Desktop.Views;

public partial class AchievementsView : UserControl
{
    private AchievementsViewModel? _model;
    private Window? _detailWindow;
    public AchievementsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= ModelOnPropertyChanged;
            _detailWindow?.Close();
            _model = DataContext as AchievementsViewModel;
            if (_model is not null) _model.PropertyChanged += ModelOnPropertyChanged;
            UpdateLayoutMode();
        };
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateLayoutMode(); };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= ModelOnPropertyChanged;
            _detailWindow?.Close();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_model is not null)
            { _model.PropertyChanged -= ModelOnPropertyChanged; _model.PropertyChanged += ModelOnPropertyChanged; }
            UpdateLayoutMode();
        };
    }
    private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AchievementsViewModel.HasSelection)) UpdateLayoutMode();
        if (e.PropertyName == nameof(AchievementsViewModel.SelectedAchievement))
        {
            if (_model?.HasAchievementSelection != true) _detailWindow?.Close();
            var expander = this.FindControl<Expander>("AchievementDetailsExpander");
            if (expander is not null) expander.IsExpanded = _model?.HasAchievementSelection == true && !Classes.Contains("compact");
        }
    }
    private void UpdateLayoutMode()
    {
        var compact = Bounds.Height is > 0 and < 580;
        var changed = compact != Classes.Contains("compact");
        Classes.Set("compact", compact);
        var layout = this.FindControl<Grid>("AchievementsLayout");
        if (layout is not null) layout.RowSpacing = compact ? 6 : 10;
        var resultsLayout = this.FindControl<Grid>("AchievementResultsLayout");
        if (resultsLayout is not null) resultsLayout.RowSpacing = compact ? 4 : 6;
        var resultsCard = this.FindControl<Border>("AchievementResultsCard");
        if (resultsCard is not null) resultsCard.Padding = new Avalonia.Thickness(compact ? 8 : 12);
        var metrics = this.FindControl<Grid>("SelectedAchievementMetrics");
        if (metrics is not null) metrics.IsVisible = !compact && _model?.HasSelection == true;
        var compactProgress = this.FindControl<TextBlock>("CompactAchievementProgress");
        if (compactProgress is not null) compactProgress.IsVisible = compact && _model?.HasSelection == true;
        var button = this.FindControl<Button>("OpenAchievementDetailsButton");
        if (button is not null) button.IsVisible = compact;
        var expander = this.FindControl<Expander>("AchievementDetailsExpander");
        if (expander is not null)
        {
            expander.IsVisible = !compact;
            if (changed) expander.IsExpanded = !compact && _model?.HasAchievementSelection == true;
        }
    }
    private async void Details_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_model?.HasAchievementSelection != true || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_detailWindow is not null) { _detailWindow.Activate(); return; }
        var scroll = new ScrollViewer
        {
            Name = "AchievementDetailScroll", Content = new AchievementDetailsView(),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var close = new Button { Content = AchievementsText.Close, IsCancel = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var content = new Grid { RowDefinitions = new("*,Auto"), Margin = new(22) };
        content.Children.Add(scroll); Grid.SetRow(close, 1); content.Children.Add(close);
        var detail = new Window
        {
            Name = "AchievementDetailsDialog", Title = AchievementsText.DetailLabel, Width = 520, Height = 440,
            MinWidth = 360, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = _model, Content = content, Background = owner.Background,
            RequestedThemeVariant = owner.ActualThemeVariant
        };
        _detailWindow = detail;
        close.Click += (_, _) => detail.Close();
        try { await detail.ShowDialog(owner); }
        finally { if (ReferenceEquals(_detailWindow, detail)) _detailWindow = null; }
    }
    private async void Refresh_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AchievementsViewModel model) await model.RefreshSelectedAsync();
    }
    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AchievementsViewModel model) model.CancelSelected();
    }
}

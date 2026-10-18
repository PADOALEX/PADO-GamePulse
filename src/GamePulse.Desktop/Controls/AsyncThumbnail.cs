using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GamePulse.Desktop.Services;

namespace GamePulse.Desktop.Controls;

/// <summary>
/// An optional thumbnail owned by its visible, realized row. Transport, decoding and the
/// bounded byte cache belong to the shared loader; recycled rows never share bitmaps.
/// </summary>
public sealed class AsyncThumbnail : Grid
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<AsyncThumbnail, string?>(nameof(Url));
    public static readonly StyledProperty<IThumbnailLoader?> LoaderProperty =
        AvaloniaProperty.Register<AsyncThumbnail, IThumbnailLoader?>(nameof(Loader));
    public static readonly StyledProperty<string?> FallbackTextProperty =
        AvaloniaProperty.Register<AsyncThumbnail, string?>(nameof(FallbackText), "◇");
    public static readonly StyledProperty<Stretch> ThumbnailStretchProperty =
        AvaloniaProperty.Register<AsyncThumbnail, Stretch>(nameof(ThumbnailStretch), Stretch.UniformToFill);

    private readonly Image _image;
    private readonly TextBlock _fallback;
    private readonly List<Visual> _visibilityAncestors = [];
    private CancellationTokenSource? _cancellation;
    private Bitmap? _bitmap;
    private Rect _viewport;
    private bool _attached, _hasViewport, _attempted;
    private long _generation;

    public AsyncThumbnail()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        _fallback = new TextBlock
        {
            Text = FallbackText, FontSize = 16, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _fallback.Classes.Add("muted");
        _image = new Image { Stretch = ThumbnailStretch };
        // The containing ListBoxItem already announces the person's/item's full name.
        AutomationProperties.SetAccessibilityView(_fallback, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_image, AccessibilityView.Raw);
        Children.Add(_fallback);
        Children.Add(_image);
        EffectiveViewportChanged += OnViewportChanged;
    }

    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public IThumbnailLoader? Loader { get => GetValue(LoaderProperty); set => SetValue(LoaderProperty, value); }
    public string? FallbackText { get => GetValue(FallbackTextProperty); set => SetValue(FallbackTextProperty, value); }
    public Stretch ThumbnailStretch { get => GetValue(ThumbnailStretchProperty); set => SetValue(ThumbnailStretchProperty, value); }

    // Exposes current ownership for headless lifecycle verification without a production fixture.
    public Bitmap? CurrentImage => _bitmap;
    public Task LoadingTask { get; private set; } = Task.CompletedTask;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _visibilityAncestors.Add(this);
        _visibilityAncestors.AddRange(this.GetVisualAncestors());
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged += OnAncestorPropertyChanged;
        UpdateActivity();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _hasViewport = false;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors.Clear();
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_image is null) return; // Base initialization can set styled properties.
        if (change.Property == FallbackTextProperty) _fallback.Text = FallbackText;
        else if (change.Property == ThumbnailStretchProperty) _image.Stretch = ThumbnailStretch;
        else if (change.Property == UrlProperty || change.Property == LoaderProperty || change.Property == DataContextProperty)
        {
            Stop();
            UpdateActivity();
        }
        else if (change.Property == BoundsProperty) UpdateActivity();
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        _hasViewport = true;
        UpdateActivity();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        // This public event also observes a page/window being hidden. The framework's
        // effective-visibility event is internal, and descendants need immediate cancellation.
        UpdateActivity();
        if (_attached) Dispatcher.UIThread.Post(UpdateActivity, DispatcherPriority.Loaded);
    }

    private bool CanShow => _attached && IsEffectivelyVisible &&
        _visibilityAncestors.All(ancestor => ancestor.IsVisible) &&
        TopLevel.GetTopLevel(this) is { IsEffectivelyVisible: true } && _hasViewport &&
        Bounds.Width > 0 && Bounds.Height > 0 &&
        _viewport.Intersects(new Rect(Bounds.Size));

    private void UpdateActivity()
    {
        if (!CanShow)
        {
            if (_attempted || _cancellation is not null || _bitmap is not null) Stop();
            return;
        }
        if (_attempted || Loader is not { } loader || string.IsNullOrWhiteSpace(Url)) return;
        _attempted = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        LoadingTask = LoadAsync(loader, Url, ++_generation, cancellation);
    }

    private async Task LoadAsync(IThumbnailLoader loader, string url, long generation, CancellationTokenSource cancellation)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await loader.LoadAsync(url, cancellation.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _generation || cancellation.IsCancellationRequested || !CanShow) return;
                _bitmap = bitmap;
                _image.Source = bitmap;
                _fallback.IsVisible = bitmap is null;
                bitmap = null; // The control now owns this bitmap until hidden/recycled/detached.
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // Optional media failures never expose a URL or replace available text.
        finally
        {
            bitmap?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            });
        }
    }

    private void Stop()
    {
        ++_generation;
        _cancellation?.Cancel();
        _cancellation = null; // Each asynchronous operation disposes its own cancellation source.
        _attempted = false;
        _image.Source = null;
        _fallback.IsVisible = true;
        var previous = _bitmap;
        _bitmap = null;
        previous?.Dispose();
    }
}

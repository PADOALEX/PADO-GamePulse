using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace GamePulse.Desktop.Services;

/// <summary>Session-only theme choice with automatic response to platform contrast preference.</summary>
public sealed class ThemeManager : IDisposable
{
    public static ThemeVariant HighContrast { get; } = new("HighContrast", ThemeVariant.Dark);
    private readonly Application app;
    private readonly IPlatformSettings? settings;
    private ThemeVariant? previous;
    private bool platformContrast;
    private int disposed;

    public ThemeManager(Application application)
    {
        app = application;
        settings = app.PlatformSettings;
        if (settings is not null)
        {
            Apply(settings.GetColorValues());
            settings.ColorValuesChanged += Changed;
        }
    }
    public static void Cycle(Application app) => app.RequestedThemeVariant =
        app.RequestedThemeVariant == HighContrast ? ThemeVariant.Dark :
        app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : HighContrast;

    private void Changed(object? sender, PlatformColorValues values)
    {
        if (Volatile.Read(ref disposed) == 0) Dispatcher.UIThread.Post(() => Apply(values));
    }
    private void Apply(PlatformColorValues values)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (values.ContrastPreference == ColorContrastPreference.High)
        {
            if (!platformContrast) previous = app.RequestedThemeVariant;
            platformContrast = true;
            app.RequestedThemeVariant = HighContrast;
        }
        else if (platformContrast)
        {
            platformContrast = false;
            app.RequestedThemeVariant = previous ?? ThemeVariant.Dark;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && settings is not null)
            settings.ColorValuesChanged -= Changed;
    }
}

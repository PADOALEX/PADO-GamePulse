using Avalonia;

namespace GamePulse.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // HttpClient diagnostics otherwise publish complete Steam query-key URLs to listeners.
        AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace(Avalonia.Logging.LogEventLevel.Error);
}

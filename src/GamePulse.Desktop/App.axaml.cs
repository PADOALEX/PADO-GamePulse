using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using GamePulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GamePulse.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ThemeManager? _themes;
    private DesktopConfiguration? _configuration;
    public override void Initialize()
    {
        _configuration = DesktopConfiguration.Load();
        UiLanguage.ApplyEnvironment(_configuration.UiLanguage);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            // No HTTP logger/provider: request URIs containing a Steam query key are never logged.
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
            services.AddGamePulseInfrastructure((_configuration ?? DesktopConfiguration.Load()).Steam);
            services.AddSingleton<AvatarService>();
            services.AddSingleton<IAvatarLoader>(provider => provider.GetRequiredService<AvatarService>());
            services.AddSingleton<GamesViewModel>();
            services.AddSingleton<IProfileComparisonService, ProfileComparisonService>();
            services.AddSingleton<ComparisonViewModel>();
            services.AddSingleton<FriendsViewModel>();
            services.AddSingleton<AchievementsViewModel>();
            services.AddSingleton<IInventoryIconLoader>(_ => new InventoryIconService());
            services.AddSingleton<InventoryViewModel>();
            services.AddSingleton<InsightsViewModel>();
            services.AddSingleton(provider => new MainWindowViewModel(
                provider.GetRequiredService<IProfileLookupService>(), provider.GetRequiredService<AvatarService>(),
                provider.GetRequiredService<ILogger<MainWindowViewModel>>(), provider.GetRequiredService<GamesViewModel>(),
                provider.GetRequiredService<ComparisonViewModel>(), provider.GetRequiredService<FriendsViewModel>(),
                provider.GetRequiredService<AchievementsViewModel>(), provider.GetRequiredService<InventoryViewModel>(),
                provider.GetRequiredService<InsightsViewModel>(),
                apiKeyConfigured: provider.GetRequiredService<SteamOptions>().HasApiKey));
            _services = services.BuildServiceProvider();
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            window.Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://GamePulse.Desktop/Assets/GamePulse.ico")));
            if (window.Screens.Primary is { } screen)
            {
                // WorkingArea is physical pixels; Window sizes are device-independent units.
                var width = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 24);
                var height = Math.Max(300, screen.WorkingArea.Height / screen.Scaling - 48);
                window.MinWidth = Math.Min(window.MinWidth, width);
                window.MinHeight = Math.Min(window.MinHeight, height);
                window.Width = Math.Min(window.Width, width);
                window.Height = Math.Min(window.Height, height);
            }
            window.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
            _themes = new ThemeManager(this);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                _services.Dispose();
                _themes.Dispose();
            };
            if (desktop.Args?.Contains("--smoke-test", StringComparer.Ordinal) == true)
            {
                if (desktop.Args.Contains("--smoke-light", StringComparer.Ordinal))
                    RequestedThemeVariant = ThemeVariant.Light;
                if (desktop.Args.Contains("--smoke-contrast", StringComparer.Ordinal))
                    RequestedThemeVariant = ThemeManager.HighContrast;
                if (desktop.Args.Contains("--smoke-minimum", StringComparer.Ordinal))
                {
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                }
                // Opens the actual native window, exercises its initial setup view, then exits.
                // This is a startup smoke check, not a claim about live Steam integration.
                window.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        var arguments = desktop.Args ?? [];
                        var outputIndex = Array.IndexOf(arguments, "--smoke-output");
                        if (outputIndex >= 0 && outputIndex + 1 < arguments.Length)
                        {
                            var path = Path.GetFullPath(arguments[outputIndex + 1]);
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            var scale = window.RenderScaling;
                            var content = (Avalonia.Visual)window.Content!;
                            using var bitmap = new RenderTargetBitmap(new PixelSize(
                                (int)Math.Ceiling(content.Bounds.Width * scale),
                                (int)Math.Ceiling(content.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                            // Render the opaque content tree; a native Window's background is external to it.
                            bitmap.Render(content);
                            bitmap.Save(path, PngBitmapEncoderOptions.Default);
                        }
                        var reportIndex = Array.IndexOf(arguments, "--smoke-report");
                        if (reportIndex >= 0 && reportIndex + 1 < arguments.Length)
                        {
                            var reportPath = Path.GetFullPath(arguments[reportIndex + 1]);
                            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                            File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new
                            {
                                Version = typeof(App).Assembly.GetName().Version!.ToString(3),
                                ConfiguredApiKey = _configuration?.Steam.HasApiKey == true,
                                SetupRequired = viewModel.IsSetupRequired,
                                UiLanguage = System.Globalization.CultureInfo.CurrentUICulture.Name,
                                StartupPassed = true
                            }));
                        }
                        Console.WriteLine("GamePulse native window startup smoke check passed.");
                        window.Close();
                    }
                    catch (Exception)
                    {
                        Console.Error.WriteLine("GamePulse startup smoke check failed.");
                        desktop.Shutdown(1);
                    }
                }, TimeSpan.FromMilliseconds(900));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

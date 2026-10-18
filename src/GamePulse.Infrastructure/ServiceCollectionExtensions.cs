using GamePulse.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GamePulse.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGamePulseInfrastructure(this IServiceCollection services,
        SteamOptions? options = null, TimeProvider? timeProvider = null)
    {
        SteamHttpPrivacy.ConfigureBeforeHttpClients();
        services.AddSingleton(options ?? SteamOptions.FromEnvironment());
        services.TryAddSingleton<TimeProvider>(timeProvider ?? TimeProvider.System);
        services.TryAddSingleton<IRetryDelay, RetryDelay>();
        services.TryAddSingleton<SteamRequestMetrics>();
        // Default factory loggers include full request URIs (Steam requires a query key). Remove them entirely.
        services.AddHttpClient("SteamApi", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PADO.GamePulse/" + typeof(SteamApiClient).Assembly.GetName().Version!.ToString(3));
        }).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
        services.TryAddSingleton<SteamApiClient>(sp => new SteamApiClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("SteamApi"),
            sp.GetRequiredService<SteamOptions>(), sp.GetRequiredService<ILogger<SteamApiClient>>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IRetryDelay>(),
            sp.GetRequiredService<SteamRequestMetrics>()));
        services.TryAddSingleton<ISteamApiClient>(sp => sp.GetRequiredService<SteamApiClient>());
        services.TryAddSingleton<ISteamLibraryClient>(sp => sp.GetRequiredService<SteamApiClient>());
        services.TryAddSingleton<ISteamFriendsClient>(sp => sp.GetRequiredService<SteamApiClient>());
        services.TryAddSingleton<ISteamAchievementClient>(sp => sp.GetRequiredService<SteamApiClient>());
        services.TryAddSingleton<IProfileLookupService, ProfileLookupService>();
        services.TryAddSingleton<ILibraryService, SteamLibraryService>();
        services.TryAddSingleton<IFriendsService, SteamFriendsService>();
        services.TryAddSingleton<IAchievementService, SteamAchievementService>();
        // A separate key-free transport prevents Web API credentials or session cookies reaching Community inventory.
        services.AddHttpClient("SteamInventory", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PADO.GamePulse/" + typeof(SteamApiClient).Assembly.GetName().Version!.ToString(3));
        }).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
        services.TryAddSingleton<SteamInventoryClient>(sp => new SteamInventoryClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("SteamInventory"),
            sp.GetRequiredService<SteamOptions>(), sp.GetRequiredService<ILogger<SteamInventoryClient>>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IRetryDelay>(),
            sp.GetRequiredService<SteamRequestMetrics>()));
        services.TryAddSingleton<ISteamInventoryClient>(sp => sp.GetRequiredService<SteamInventoryClient>());
        services.TryAddSingleton<IInventoryService, SteamInventoryService>();
        return services;
    }
}

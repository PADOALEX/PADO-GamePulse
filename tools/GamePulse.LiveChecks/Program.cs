using System.Globalization;
using System.Text.Json;
using GamePulse.Core;
using GamePulse.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

// Opt-in diagnostics only. This executable is neither referenced nor shipped by Desktop.
// Only aggregate facts are written: no key, profile name, SteamID, asset ID, or image URL.
SteamHttpPrivacy.ConfigureBeforeHttpClients();
return args.Contains("--profile", StringComparer.Ordinal)
    ? await LiveProfileChecks.RunAsync(args)
    : await LiveInventoryChecks.RunAsync(args);

internal static class LiveInventoryChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("Opt-in public Steam inventory checks using the production client and service.\n" +
                "Usage: --steam-id <SteamID64> [--apps 730,440,570,578080,252490,753] " +
                "[--page-size 1000] [--spacing-seconds 3] [--check-images] [--out <report.json>]\n" +
                "Credentialed profile checks: --profile <SteamID64> [--achievement-app <AppID>] " +
                "[--compare-profile <SteamID64>] [--spacing-seconds 3] [--out <report.json>]\n" +
                "Inventory mode uses no login, cookie, API key or persisted profile data. Stops on HTTP429; no retry. " +
                "Choose a public profile you are allowed to inspect. Network outcomes may change.");
            return 0;
        }
        if (!TryOptions(args, out var settings))
        {
            Console.Error.WriteLine("Invalid arguments. Use --help. Do not pass API keys to this tool.");
            return 1;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var options = new SteamOptions { InventoryPageSize = settings.PageSize, MaxAttempts = 1, MaxInventoryPages = 25 };
        var metrics = new SteamRequestMetrics();
        using var transport = new ObservationHandler(TimeSpan.FromSeconds(settings.SpacingSeconds));
        using var http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PADO.GamePulse.LiveChecks/1.0");
        using var client = new SteamInventoryClient(http, options, NullLogger<SteamInventoryClient>.Instance,
            TimeProvider.System, new RetryDelay(TimeProvider.System), metrics);
        var service = new SteamInventoryService(client, options, TimeProvider.System);
        var results = new List<InventoryCheck>();
        var started = DateTimeOffset.UtcNow;
        var exitCode = 0;
        foreach (var appId in settings.Apps)
        {
            var app = InventoryCatalog.Apps.Single(x => x.AppId == appId);
            transport.Statuses.Clear();
            try
            {
                var result = await service.GetInventoryAsync(new(settings.SteamId, appId, app.DefaultContextId),
                    true, null, cancellation.Token);
                var snapshot = result.Data;
                var inventoryStatuses = transport.Statuses.ToArray();
                ImageCheck? image = null;
                if (settings.CheckImages && !inventoryStatuses.Contains(429) && result.State == DataState.Ready &&
                    snapshot?.Items.FirstOrDefault(x => x.IconUrl is not null)?.IconUrl is { } icon)
                    image = await CheckImageAsync(http, icon, cancellation.Token);
                var check = new InventoryCheck(appId, app.DefaultContextId, result.State.ToString(), inventoryStatuses,
                    snapshot?.IsComplete, snapshot?.PagesLoaded, snapshot?.ReportedAssetCount,
                    snapshot?.Items.Count, snapshot?.Items.Sum(x => (decimal)x.Amount),
                    snapshot?.Items.Count(x => x.HasDescription), snapshot?.Items.Count(x => x.IconUrl is not null),
                    snapshot?.Items.Count(x => x.Marketable is not null && x.Tradable is not null),
                    snapshot?.InterruptionState?.ToString(), snapshot?.StopReason?.ToString(), image);
                results.Add(check);
                Console.WriteLine(JsonSerializer.Serialize(check));
                if (result.State == DataState.RateLimited || snapshot?.InterruptionState == DataState.RateLimited || image?.HttpStatus == 429)
                { exitCode = 2; break; }
                if (result.State == DataState.Failed || snapshot is { IsComplete: false } || image is { ValidImage: false }) exitCode = 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Live check canceled or its total time budget expired. No further request was made.");
                exitCode = 1; break;
            }
            catch (Exception)
            {
                // Exception messages can contain URIs. Do not print them, or serialize exception objects.
                Console.Error.WriteLine("Live check could not complete. No raw response or exception was recorded.");
                exitCode = 1; break;
            }
        }
        var report = new { StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow, ExitCode = exitCode,
            KeyUsed = false, RequestSpacingSeconds = settings.SpacingSeconds, PageSize = settings.PageSize,
            InventoryRequests = metrics.Requests, InventoryRetries = metrics.Retries, Results = results };
        if (settings.Output is { } path)
        {
            try
            {
                await File.WriteAllTextAsync(Path.GetFullPath(path), JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { Console.Error.WriteLine("The aggregate report could not be written."); return 1; }
        }
        return exitCode;
    }

    private static async Task<ImageCheck> CheckImageAsync(HttpClient http, string icon, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        token = deadline.Token;
        using var response = await http.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead, token);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, contentType, null, false);
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return new((int)response.StatusCode, contentType, null, false);
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        var webp = bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        return new((int)response.StatusCode, contentType, bytes.Length,
            contentType is "image/png" or "image/jpeg" or "image/webp" && (png || jpeg || webp));
    }

    private static bool TryOptions(string[] args, out Settings settings)
    {
        settings = new("", InventoryCatalog.Apps.Select(x => x.AppId).ToArray(), 1000, 3, false, null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!seen.Add(arg)) return false;
            if (arg == "--check-images") { settings = settings with { CheckImages = true }; continue; }
            if (++index >= args.Length) return false;
            var value = args[index];
            switch (arg)
            {
                case "--steam-id": settings = settings with { SteamId = value }; break;
                case "--out": settings = settings with { Output = value }; break;
                case "--page-size" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 2000:
                    settings = settings with { PageSize = size }; break;
                case "--spacing-seconds" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 3 and <= 60:
                    settings = settings with { SpacingSeconds = seconds }; break;
                case "--apps":
                    var appIds = new List<uint>();
                    foreach (var item in value.Split(','))
                    {
                        if (!uint.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var app) ||
                            !InventoryCatalog.Apps.Any(x => x.AppId == app) || appIds.Contains(app)) return false;
                        appIds.Add(app);
                    }
                    if (appIds.Count == 0) return false;
                    settings = settings with { Apps = appIds.ToArray() }; break;
                default: return false;
            }
        }
        return ProfileInput.Parse(settings.SteamId).Data is { Kind: ProfileInputKind.SteamId } parsed && parsed.Value == settings.SteamId;
    }

    private sealed record Settings(string SteamId, uint[] Apps, int PageSize, int SpacingSeconds, bool CheckImages, string? Output);
    private sealed record ImageCheck(int HttpStatus, string? ContentType, int? Bytes, bool ValidImage);
    private sealed record InventoryCheck(uint AppId, string ContextId, string State, int[] HttpStatuses,
        bool? Complete, int? Pages, long? ReportedAssets, int? LoadedAssets, decimal? LoadedUnits,
        int? DescriptionsJoined, int? IconsAvailable, int? TradeFlagsAvailable, string? Interruption, string? StopReason, ImageCheck? Image);

    private sealed class ObservationHandler(TimeSpan spacing) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        private DateTimeOffset? previous;
        public List<int> Statuses { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (previous is { } prior && prior + spacing - DateTimeOffset.UtcNow is { } remaining && remaining > TimeSpan.Zero)
                await Task.Delay(remaining, token);
            previous = DateTimeOffset.UtcNow;
            var response = await base.SendAsync(request, token);
            Statuses.Add((int)response.StatusCode);
            return response;
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GamePulse.Core;
using GamePulse.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Explicitly requested production-service checks. Reports contain aggregates only;
/// identifiers, player/game names, URLs, response bodies and exception text stay out.
/// </summary>
internal static class LiveProfileChecks
{
    private const int MaximumRequests = 80;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Opt-in Steam Web API profile checks using the production services.\n" +
                "Usage: --profile <SteamID64> [--achievement-app <AppID>] [--compare-profile <SteamID64>] " +
                "[--spacing-seconds 3] [--out <report.json>]\n" +
                "Set your authorized key locally in GAMEPULSE_STEAM_API_KEY. Never pass a key as an argument. " +
                "Only the explicitly selected game is checked; achievements are otherwise skipped. " +
                "At most 80 sequential HTTP requests, one attempt each, at least 3 seconds apart; " +
                "stops on HTTP 429. No raw profile data is reported.");
            return 0;
        }
        if (!TryOptions(args, out var settings))
        {
            Console.Error.WriteLine("Invalid profile-check arguments. Use --profile <SteamID64> --help. Never pass API keys as arguments.");
            return 1;
        }

        var environment = SteamOptions.FromEnvironment();
        var started = DateTimeOffset.UtcNow;
        if (!environment.HasApiKey)
        {
            Console.Error.WriteLine("MissingKey: set your authorized GAMEPULSE_STEAM_API_KEY locally. No HTTP request was made.");
            return await WriteReportAsync(settings.Output, new
            {
                Mode = "Profile", StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow,
                ExitCode = 3, State = "MissingKey", KeyUsed = false, Requests = 0, Retries = 0,
                Results = Array.Empty<StepCheck>()
            }) ? 3 : 1;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var metrics = new SteamRequestMetrics();
        var options = new SteamOptions
        {
            ApiKey = environment.ApiKey, MaxAttempts = 1, MaxConcurrentRequests = 1,
            MaxConcurrentFriendBatches = 1, RequestTimeout = TimeSpan.FromSeconds(15)
        };
        using var transport = new ProfileObservationHandler(TimeSpan.FromSeconds(settings.SpacingSeconds), MaximumRequests);
        using var http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PADO.GamePulse.LiveChecks/1.0");
        using var client = new SteamApiClient(http, options, NullLogger<SteamApiClient>.Instance,
            TimeProvider.System, new RetryDelay(TimeProvider.System), metrics);
        var profiles = new ProfileLookupService(client, options, TimeProvider.System);
        var libraries = new SteamLibraryService(client, options, TimeProvider.System);
        var friends = new SteamFriendsService(client, options, TimeProvider.System);
        var achievements = new SteamAchievementService(client, options, TimeProvider.System);
        var results = new List<StepCheck>();
        var exitCode = 0;
        string? stopped = null;

        try
        {
            var libraryA = await CheckProfileAsync("A", settings.Profile);
            if (settings.AchievementApp is { } app && libraryA.ProfileAvailable)
                await StepAsync("Achievements", "A", ct => achievements.GetAchievementsAsync(settings.Profile, app, true, ct),
                    value => new
                    {
                        SelectedGames = 1, Complete = value?.IsComplete, Returned = value?.Achievements.Count,
                        Total = value?.TotalCount, Unlocked = value?.UnlockedCount, CompletionPercent = value?.Completion
                    });
            if (settings.CompareProfile is { } second)
            {
                var libraryB = await CheckProfileAsync("B", second);
                var comparison = new ProfileComparisonService().Compare(libraryA.Library, libraryB.Library);
                var comparisonCheck = new StepCheck("MutualGames", "A+B", comparison.State.ToString(), [], 0,
                    new
                    {
                        ComparedLibraries = 2, MutualGames = comparison.Data?.MutualGames.Count,
                        GamesA = comparison.Data?.GamesA, GamesB = comparison.Data?.GamesB,
                        TotalMinutesA = comparison.Data?.TotalMinutesA, TotalMinutesB = comparison.Data?.TotalMinutesB
                    });
                results.Add(comparisonCheck);
                Console.WriteLine(JsonSerializer.Serialize(comparisonCheck));
            }
        }
        catch (DiagnosticStopException reason)
        {
            stopped = reason.Reason;
            exitCode = transport.RateLimited ? 2 : 1;
            Console.Error.WriteLine("Profile checks stopped: " + stopped + ". No further network request was made.");
        }
        catch (OperationCanceledException)
        {
            stopped = "CanceledOrTimeBudget";
            exitCode = transport.RateLimited ? 2 : 1;
            Console.Error.WriteLine("Profile checks canceled or their total time budget expired. No further work was started.");
        }
        catch (Exception)
        {
            stopped = "UnexpectedFailure";
            exitCode = transport.RateLimited ? 2 : 1;
            Console.Error.WriteLine("Profile checks could not complete. No raw response or exception was recorded.");
        }
        finally { Console.CancelKeyPress -= cancel; }

        var report = new
        {
            Mode = "Profile", StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow,
            ExitCode = exitCode, KeyUsed = true, RequestSpacingSeconds = settings.SpacingSeconds,
            MaximumRequests, Requests = transport.RequestCount, ApiRequestAttempts = metrics.Requests,
            Retries = metrics.Retries, HttpStatuses = transport.Statuses.ToArray(), StopReason = stopped,
            ProfilesRequested = settings.CompareProfile is null ? 1 : 2,
            AchievementGameRequested = settings.AchievementApp.HasValue, Results = results
        };
        return await WriteReportAsync(settings.Output, report) ? exitCode : 1;

        async Task<(bool ProfileAvailable, Outcome<OwnedLibrary>? Library)> CheckProfileAsync(string side, string steamId)
        {
            var profile = await StepAsync("Summary", side, ct => profiles.FindAsync(steamId, true, ct), value => new
            {
                ProfileReturned = value is not null,
                KnownFields = value is null ? (int?)null : Count(value.DisplayName, value.AvatarUrl, value.Visibility,
                    value.PersonaState, value.CreatedAt, value.LastLogoff, value.CurrentGame)
            });
            // Do not repeatedly query other methods for a nonexistent profile or rejected key.
            if (!profile.IsReady) return (false, null);
            await StepAsync("Level", side, ct => profiles.GetLevelAsync(steamId, true, ct),
                value => new { KnownValues = value is null ? (int?)null : 1 });
            await StepAsync("Bans", side, ct => profiles.GetBansAsync(steamId, true, ct), value => new
            {
                KnownFields = value is null ? (int?)null : Count(value.CommunityBanned, value.VacBanned,
                    value.VacBanCount, value.GameBanCount, value.DaysSinceLastBan, value.EconomyBan)
            });
            var owned = await StepAsync("Library", side, ct => libraries.GetLibraryAsync(steamId, true, ct), value => new
            {
                Complete = value?.IsComplete, Reported = value?.ReportedCount, Returned = value?.Games.Count,
                KnownPlaytime = value?.Games.Count(game => game.PlaytimeMinutes.HasValue),
                TotalMinutes = value is { IsComplete: true } && value.Games.All(game => game.PlaytimeMinutes.HasValue)
                    ? value.Games.Sum(game => (decimal)game.PlaytimeMinutes!.Value) : (decimal?)null
            });
            await StepAsync("Recent", side, ct => libraries.GetRecentAsync(steamId, true, ct), value => new
            {
                Reported = value?.ReportedCount, Returned = value?.Games.Count,
                KnownRecentPlaytime = value?.Games.Count(game => game.RecentPlaytimeMinutes.HasValue)
            });
            // The optional second side needs profile/library facts for Compare, not an implicit friend-graph crawl.
            if (side == "A")
                await StepAsync("Friends", side, ct => friends.GetFriendsAsync(steamId, true, ct), value => new
                {
                    Returned = value?.Friends.Count, SummariesAvailable = value?.Friends.Count(friend => friend.Profile is not null),
                    SummariesComplete = value?.SummariesComplete, FailedBatches = value?.FailedSummaryBatches
                });
            return (true, owned);
        }

        async Task<Outcome<T>> StepAsync<T>(string name, string subject,
            Func<CancellationToken, Task<Outcome<T>>> action, Func<T?, object> summarize) where T : class
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (transport.RateLimited) throw new DiagnosticStopException("RateLimited");
            var firstStatus = transport.Statuses.Count;
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await action(cancellation.Token);
                var check = new StepCheck(name, subject, result.State.ToString(), transport.Statuses.Skip(firstStatus).ToArray(),
                    timer.ElapsedMilliseconds, summarize(result.Data));
                results.Add(check);
                Console.WriteLine(JsonSerializer.Serialize(check));
                if (result.State == DataState.Failed ||
                    result.Data is OwnedLibrary { IsComplete: false } or FriendDirectory { SummariesComplete: false } or GameAchievements { IsComplete: false })
                    exitCode = 1;
                if (transport.RateLimited || result.State == DataState.RateLimited)
                    throw new DiagnosticStopException("RateLimited");
                return result;
            }
            catch (DiagnosticStopException)
            {
                // Friends batches can hit the guard inside a single service call. Preserve that aggregate outcome.
                if (!results.Any(check => check.Name == name && check.Subject == subject))
                {
                    var check = new StepCheck(name, subject, transport.RateLimited ? "RateLimited" : "RequestBudgetExceeded",
                        transport.Statuses.Skip(firstStatus).ToArray(), timer.ElapsedMilliseconds, null);
                    results.Add(check);
                    Console.WriteLine(JsonSerializer.Serialize(check));
                }
                throw;
            }
        }
    }

    private static int Count(params object?[] values) => values.Count(value => value is not null);

    private static async Task<bool> WriteReportAsync(string? path, object report)
    {
        if (path is null) return true;
        try
        {
            await File.WriteAllTextAsync(Path.GetFullPath(path), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("The aggregate profile report could not be written.");
            return false;
        }
    }

    private static bool TryOptions(string[] args, out Settings settings)
    {
        settings = new("", null, null, 3, null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!seen.Add(arg) || ++index >= args.Length) return false;
            var value = args[index];
            switch (arg)
            {
                case "--profile": settings = settings with { Profile = value }; break;
                case "--compare-profile": settings = settings with { CompareProfile = value }; break;
                case "--achievement-app" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var app) && app > 0:
                    settings = settings with { AchievementApp = app }; break;
                case "--spacing-seconds" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 3 and <= 60:
                    settings = settings with { SpacingSeconds = seconds }; break;
                case "--out": settings = settings with { Output = value }; break;
                default: return false;
            }
        }
        return IsSteamId(settings.Profile) && (settings.CompareProfile is null || IsSteamId(settings.CompareProfile));
    }

    private static bool IsSteamId(string value) => ProfileInput.Parse(value).Data is
        { Kind: ProfileInputKind.SteamId } parsed && parsed.Value == value;

    private sealed record Settings(string Profile, string? CompareProfile, uint? AchievementApp, int SpacingSeconds, string? Output);
    private sealed record StepCheck(string Name, string Subject, string State, int[] HttpStatuses, long ElapsedMilliseconds, object? Aggregate);

    private sealed class DiagnosticStopException(string reason) : Exception
    {
        public string Reason { get; } = reason;
    }

    private sealed class ProfileObservationHandler(TimeSpan spacing, int maximumRequests) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private long? previousStart;
        private int disposed;
        public List<int> Statuses { get; } = [];
        public int RequestCount { get; private set; }
        public bool RateLimited { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (RateLimited) throw new DiagnosticStopException("RateLimited");
                if (RequestCount >= maximumRequests) throw new DiagnosticStopException("RequestBudgetExceeded");
                var uri = request.RequestUri;
                if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                    !string.Equals(uri.IdnHost, "api.steampowered.com", StringComparison.OrdinalIgnoreCase))
                    throw new DiagnosticStopException("UnexpectedEndpoint");
                if (previousStart is { } prior)
                {
                    var remaining = spacing - Stopwatch.GetElapsedTime(prior);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                }
                token.ThrowIfCancellationRequested();
                previousStart = Stopwatch.GetTimestamp();
                RequestCount++;
                var response = await base.SendAsync(request, token);
                Statuses.Add((int)response.StatusCode);
                if ((int)response.StatusCode == 429) RateLimited = true;
                return response;
            }
            finally { gate.Release(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) gate.Dispose();
            base.Dispose(disposing);
        }
    }
}

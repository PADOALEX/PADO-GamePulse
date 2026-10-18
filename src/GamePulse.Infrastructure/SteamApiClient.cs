using System.Net;
using System.Text.Json;
using GamePulse.Core;
using Microsoft.Extensions.Logging;

namespace GamePulse.Infrastructure;

/// <summary>Read-only public profile queries. Endpoints and HTTPS are fixed, redirects are disabled in DI.</summary>
public sealed partial class SteamApiClient : ISteamApiClient, ISteamLibraryClient, ISteamFriendsClient, ISteamAchievementClient, IDisposable
{
    static SteamApiClient() => SteamHttpPrivacy.ConfigureBeforeHttpClients();

    private const string ApiRoot = "https://api.steampowered.com/";
    private readonly HttpClient httpClient;
    private readonly SteamOptions options;
    private readonly ILogger<SteamApiClient> logger;
    private readonly TimeProvider clock;
    private readonly IRetryDelay delay;
    private readonly SteamRequestMetrics metrics;
    private readonly SemaphoreSlim requestGate;
    private int disposed;

    public SteamApiClient(HttpClient httpClient, SteamOptions options, ILogger<SteamApiClient> logger,
        TimeProvider timeProvider, IRetryDelay retryDelay, SteamRequestMetrics metrics)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        clock = timeProvider;
        delay = retryDelay;
        this.metrics = metrics;
        requestGate = new SemaphoreSlim(Math.Clamp(options.MaxConcurrentRequests, 1, 8));
    }

    public async Task<Outcome<string>> ResolveVanityAsync(string vanity, CancellationToken cancellationToken)
    {
        var parsed = ProfileInput.Parse(vanity);
        if (!parsed.IsReady || parsed.Data!.Kind != ProfileInputKind.Vanity || parsed.Data.Value != vanity)
            return Outcome<string>.Failure(DataState.InvalidInput, "Enter a valid vanity slug.");
        var result = await GetAsync<VanityEnvelope>("ISteamUser/ResolveVanityURL/v1/",
            "vanityurl=" + Uri.EscapeDataString(parsed.Data.Value), cancellationToken).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<VanityEnvelope, string>(result);
        var value = result.Data!.Response;
        if (value?.Success == 42)
            return Outcome<string>.Failure(DataState.NotFound, "Steam could not resolve this vanity slug. Display names are not unique; use a Steam profile URL or SteamID64.");
        if (value?.Success != 1 || value.SteamId is null || !IsSteamId(value.SteamId))
            return Malformed<string>();
        return Outcome<string>.Ready(value.SteamId, clock.GetUtcNow());
    }

    public async Task<Outcome<SteamProfile>> GetProfileAsync(string steamId, CancellationToken cancellationToken)
    {
        if (!IsSteamId(steamId)) return Invalid<SteamProfile>();
        var result = await GetAsync<PlayerEnvelope>("ISteamUser/GetPlayerSummaries/v2/",
            "steamids=" + Uri.EscapeDataString(steamId), cancellationToken).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<PlayerEnvelope, SteamProfile>(result);
        var players = result.Data!.Response?.Players;
        if (players is null) return Malformed<SteamProfile>();
        if (players.Length == 0)
            return Outcome<SteamProfile>.Failure(DataState.NotFound, "Steam returned no profile for this SteamID64.");
        // A single-ID request must not pick an arbitrary winner from contradictory or
        // unrequested rows. Apply the same identity checks as the batched summary reader.
        if (players.Length != 1 || players[0] is not { } player || player.SteamId != steamId)
            return Malformed<SteamProfile>();
        // Construct the canonical Steam link ourselves; never open an arbitrary URL from a response.
        var profile = new SteamProfile(steamId, player.Name,
            "https://steamcommunity.com/profiles/" + steamId, SafeAvatar(player.Avatar),
            player.Visibility, player.PersonaState, UnixTime(player.Created), UnixTime(player.LastLogoff), player.CurrentGame);
        return Outcome<SteamProfile>.Ready(profile, clock.GetUtcNow());
    }

    public async Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, CancellationToken cancellationToken)
    {
        if (!IsSteamId(steamId)) return Invalid<ProfileLevel>();
        var result = await GetAsync<LevelEnvelope>("IPlayerService/GetSteamLevel/v1/",
            "input_json=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { steamid = steamId })),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<LevelEnvelope, ProfileLevel>(result);
        if (result.Data!.Response is null) return Malformed<ProfileLevel>();
        var level = result.Data.Response.Level;
        return level is >= 0
            ? Outcome<ProfileLevel>.Ready(new(level.Value), clock.GetUtcNow())
            : Outcome<ProfileLevel>.Failure(DataState.Unavailable, "Steam did not expose this profile's level.");
    }

    public async Task<Outcome<SteamBans>> GetBansAsync(string steamId, CancellationToken cancellationToken)
    {
        if (!IsSteamId(steamId)) return Invalid<SteamBans>();
        var result = await GetAsync<BansEnvelope>("ISteamUser/GetPlayerBans/v1/",
            "steamids=" + Uri.EscapeDataString(steamId), cancellationToken).ConfigureAwait(false);
        if (!result.IsReady) return CopyFailure<BansEnvelope, SteamBans>(result);
        if (result.Data!.Players is not { } players) return Malformed<SteamBans>();
        if (players.Length == 0)
            return Outcome<SteamBans>.Failure(DataState.Unavailable, "Steam did not expose ban information for this profile.");
        // Conflicting ban records must never be presented as a known clean/banned result.
        if (players.Length != 1 || players[0] is not { } bans || bans.SteamId != steamId)
            return Malformed<SteamBans>();
        return Outcome<SteamBans>.Ready(new(bans.CommunityBanned, bans.VacBanned,
            Nonnegative(bans.VacBanCount), Nonnegative(bans.GameBanCount),
            Nonnegative(bans.DaysSinceLastBan), bans.EconomyBan), clock.GetUtcNow());
    }

    private async Task<Outcome<T>> GetAsync<T>(string endpoint, string query, CancellationToken cancellationToken,
        long maximumResponseBytes = 2 * 1024 * 1024, Func<T, bool>? acceptBadRequest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.HasApiKey)
            return Outcome<T>.Failure(DataState.MissingKey, "Set GAMEPULSE_STEAM_API_KEY locally, then restart GamePulse. Steam sign-in is not required.");
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempts = Math.Clamp(options.MaxAttempts, 1, 3);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan? retryWait = null;
                Outcome<T>? error = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(options.RequestTimeout.TotalMilliseconds, 1, 60_000)));
                try
                {
                    // Never log this URI, raw exceptions, request objects or response bodies.
                    using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoot + endpoint + "?" + query +
                        "&key=" + Uri.EscapeDataString(options.ApiKey!));
                    metrics.RecordRequest();
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    // Some achievement failures use HTTP 400 with a structured playerstats error.
                    // Only callers explicitly accepting that envelope may inspect its bounded body.
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest && acceptBadRequest is not null)
                    {
                        if (response.Content.Headers.ContentLength > maximumResponseBytes) return Malformed<T>();
                        await response.Content.LoadIntoBufferAsync(maximumResponseBytes, timeout.Token).ConfigureAwait(false);
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                        var data = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                        if (data is null) return Malformed<T>();
                        if (response.StatusCode == HttpStatusCode.BadRequest && !acceptBadRequest!(data))
                            return HttpFailure<T>(response.StatusCode);
                        return Outcome<T>.Ready(data, clock.GetUtcNow());
                    }
                    error = HttpFailure<T>(response.StatusCode);
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                    {
                        retryWait = Backoff(attempt);
                        if (response.Headers.RetryAfter is { } retryAfter)
                        {
                            var advised = retryAfter.Delta ?? (retryAfter.Date - clock.GetUtcNow());
                            if (advised > TimeSpan.FromSeconds(30)) return error;
                            if (advised is { } wait && wait > retryWait) retryWait = wait;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    error = Outcome<T>.Failure(DataState.Failed, "Steam request timed out. Try again later.");
                    retryWait = Backoff(attempt);
                }
                catch (HttpRequestException)
                {
                    error = Outcome<T>.Failure(DataState.Failed, "Steam could not be reached securely. Check the connection and try again.");
                    retryWait = Backoff(attempt);
                }
                catch (IOException)
                {
                    error = Outcome<T>.Failure(DataState.Failed, "The Steam response was interrupted. Try again later.");
                    retryWait = Backoff(attempt);
                }
                catch (JsonException)
                {
                    return Malformed<T>();
                }
                if (retryWait is null || attempt == attempts) return error!;
                metrics.RecordRetry();
                logger.LogDebug("Retrying Steam request. Attempt {Attempt} of {MaximumAttempts}.", attempt + 1, attempts);
                await delay.WaitAsync(retryWait.Value, cancellationToken).ConfigureAwait(false);
            }
            return Outcome<T>.Failure(DataState.Failed, "Steam request failed.");
        }
        finally { requestGate.Release(); }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 150));
    private static bool IsSteamId(string value) => ProfileInput.Parse(value).Data is
        { Kind: ProfileInputKind.SteamId } parsed && parsed.Value == value;
    private static int? Nonnegative(int? value) => value is >= 0 ? value : null;
    private static DateTimeOffset? UnixTime(long? value)
    {
        if (value is null or <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(value.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static string? SafeAvatar(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        string[] hosts = ["steamstatic.com", "steamusercontent.com", "akamaihd.net"];
        return hosts.Any(h => uri.IdnHost == h || uri.IdnHost.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri : null;
    }
    private static Outcome<T> Invalid<T>() => Outcome<T>.Failure(DataState.InvalidInput, "Enter a valid public individual SteamID64.");
    private static Outcome<T> Malformed<T>() => Outcome<T>.Failure(DataState.Failed, "Steam returned an incomplete or invalid response. Try refreshing later.");
    private static Outcome<TOut> CopyFailure<TIn, TOut>(Outcome<TIn> value) =>
        Outcome<TOut>.Failure(value.State, value.Message ?? "Steam request failed.");
    private static Outcome<T> HttpFailure<T>(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => Outcome<T>.Failure(DataState.Unavailable, "Steam denied access. Check API-key permission and profile privacy; this response alone does not prove the profile is private."),
        404 => Outcome<T>.Failure(DataState.NotFound, "Steam could not find the requested resource."),
        429 => Outcome<T>.Failure(DataState.RateLimited, "Steam is limiting requests. Wait before refreshing."),
        _ => Outcome<T>.Failure(DataState.Failed, "Steam request failed. Try again later.")
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        requestGate.Dispose();
        httpClient.Dispose();
    }
}

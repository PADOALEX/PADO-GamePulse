using System.Net;
using System.Net.Http.Headers;
using GamePulse.Core;
using GamePulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GamePulse.IntegrationTests;

public sealed class SteamClientTests
{
    private const string Id = "76561198000000001";
    private const string Key = "test-secret-never-log";
    private const string ProfileJson = """{"response":{"players":[{"steamid":"76561198000000001","personaname":"Test Player","personastate":6,"communityvisibilitystate":3,"timecreated":1262304000,"avatarfull":"https://avatars.steamstatic.com/avatar.jpg"}]}}""";

    [Fact]
    public async Task Profile_maps_real_wire_fields_without_inventing_missing_values()
    {
        using var h = Harness.Create(_ => Json(ProfileJson));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Equal(DataState.Ready, result.State);
        Assert.Equal("Test Player", result.Data!.DisplayName);
        Assert.Equal(6, result.Data.PersonaState);
        Assert.Equal(2010, result.Data.CreatedAt!.Value.Year);
        Assert.Null(result.Data.LastLogoff);
        Assert.Null(result.Data.CurrentGame);
        Assert.Equal("https://steamcommunity.com/profiles/" + Id, result.Data.ProfileUrl);
        Assert.Equal("https", h.Handler.Uris.Single().Scheme);
        Assert.Equal("api.steampowered.com", h.Handler.Uris.Single().Host);
    }

    [Theory]
    [InlineData(403, DataState.Unavailable, 1)]
    [InlineData(404, DataState.NotFound, 1)]
    [InlineData(429, DataState.RateLimited, 3)]
    [InlineData(500, DataState.Failed, 3)]
    public async Task Http_status_has_explicit_state_and_bounded_retry(int status, DataState expected, int attempts)
    {
        using var h = Harness.Create(_ => new((HttpStatusCode)status));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Equal(expected, result.State);
        Assert.Equal(attempts, h.Metrics.Requests);
        Assert.Equal(attempts - 1, h.Metrics.Retries);
    }

    [Theory]
    [InlineData("{broken", DataState.Failed)]
    [InlineData("{}", DataState.Failed)]
    [InlineData("{\"response\":{}}", DataState.Failed)]
    [InlineData("{\"response\":{\"players\":[]}}", DataState.NotFound)]
    [InlineData("{\"response\":{\"players\":[{}]}}", DataState.Failed)]
    public async Task Missing_or_malformed_profile_differs_from_empty_profile(string json, DataState expected)
    {
        using var h = Harness.Create(_ => Json(json));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Equal(expected, result.State);
        Assert.Equal(1, h.Metrics.Requests);
    }

    [Fact]
    public async Task Partial_profile_retains_unknown_fields_and_rejects_external_avatar()
    {
        using var h = Harness.Create(_ => Json("""{"response":{"players":[{"steamid":"76561198000000001","avatarfull":"https://attacker.invalid/avatar.png"}]}}"""));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Null(result.Data!.DisplayName);
        Assert.Null(result.Data.Visibility);
        Assert.Null(result.Data.PersonaState);
        Assert.Null(result.Data.CreatedAt);
        Assert.Null(result.Data.AvatarUrl);
    }

    [Theory]
    [InlineData("[{\"steamid\":\"76561198000000001\",\"personaname\":\"First\"},{\"steamid\":\"76561198000000001\",\"personaname\":\"Conflicting\"}]")]
    [InlineData("[{\"steamid\":\"76561198000000001\"},{\"steamid\":\"76561198000000002\"}]")]
    [InlineData("[{\"steamid\":\"76561198000000001\"},null]")]
    [InlineData("[{\"steamid\":\"76561198000000002\"}]")]
    public async Task Single_profile_rejects_duplicate_or_unrequested_records_instead_of_choosing_first(string players)
    {
        using var h = Harness.Create(_ => Json("{\"response\":{\"players\":" + players + "}}"));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
        Assert.Single(h.Handler.Uris);
    }

    [Fact]
    public async Task Missing_key_requires_no_network_and_is_safe()
    {
        using var h = Harness.Create(_ => throw new InvalidOperationException("Must not request"), new());
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Equal(DataState.MissingKey, result.State);
        Assert.Empty(h.Handler.Uris);
    }

    [Fact]
    public async Task Vanity_resolves_but_display_name_is_not_a_global_search()
    {
        using var h = Harness.Create(uri => uri.AbsolutePath.Contains("ResolveVanityURL")
            ? Json("""{"response":{"success":1,"steamid":"76561198000000001"}}""") : Json(ProfileJson));
        var lookup = new ProfileLookupService(h.Client, h.Options, h.Clock);
        var result = await lookup.FindAsync("https://steamcommunity.com/id/test-slug/?l=english", false, default);
        Assert.True(result.IsReady);
        Assert.Equal(2, h.Metrics.Requests);
        var invalid = await lookup.FindAsync("Non unique display name", false, default);
        Assert.Equal(DataState.InvalidInput, invalid.State);
        Assert.Equal(2, h.Metrics.Requests);
    }

    [Fact]
    public async Task Vanity_not_found_has_actionable_explanation()
    {
        using var h = Harness.Create(_ => Json("""{"response":{"success":42,"message":"No match"}}"""));
        var result = await h.Client.ResolveVanityAsync("unmatched", default);
        Assert.Equal(DataState.NotFound, result.State);
        Assert.Contains("Display names", result.Message);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001")]
    [InlineData(" 76561198000000001 ")]
    public async Task Wire_steamid_must_be_exact_id_and_not_an_input_url(string invalidId)
    {
        using var h = Harness.Create(_ => Json("{\"response\":{\"success\":1,\"steamid\":\"" + invalidId + "\"}}"));
        Assert.Equal(DataState.InvalidInput, (await h.Client.GetProfileAsync(invalidId, default)).State);
        Assert.Empty(h.Handler.Uris);
        Assert.Equal(DataState.Failed, (await h.Client.ResolveVanityAsync("test-slug", default)).State);
        Assert.Single(h.Handler.Uris);
    }

    [Fact]
    public async Task Level_zero_is_known_but_missing_level_is_unavailable()
    {
        var call = 0;
        using var h = Harness.Create(_ => Json(++call == 1 ? """{"response":{"player_level":0}}""" : """{"response":{}}"""));
        Assert.Equal(0, (await h.Client.GetLevelAsync(Id, default)).Data!.Value);
        Assert.Equal(DataState.Unavailable, (await h.Client.GetLevelAsync(Id, default)).State);
    }

    [Fact]
    public async Task Missing_ban_fields_remain_unknown()
    {
        using var h = Harness.Create(_ => Json("""{"players":[{"SteamId":"76561198000000001","VACBanned":false}]}"""));
        var result = await h.Client.GetBansAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.False(result.Data!.VacBanned);
        Assert.Null(result.Data.CommunityBanned);
        Assert.Null(result.Data.GameBanCount);
        Assert.Null(result.Data.VacBanCount);
    }

    [Theory]
    [InlineData("[{\"SteamId\":\"76561198000000001\",\"VACBanned\":false},{\"SteamId\":\"76561198000000001\",\"VACBanned\":true}]")]
    [InlineData("[{\"SteamId\":\"76561198000000001\"},{\"SteamId\":\"76561198000000002\"}]")]
    [InlineData("[{\"SteamId\":\"76561198000000001\"},null]")]
    [InlineData("[{\"SteamId\":\"76561198000000002\"}]")]
    [InlineData("[null]")]
    public async Task Ban_records_with_ambiguous_identity_never_establish_clean_or_banned(string players)
    {
        using var h = Harness.Create(_ => Json("{\"players\":" + players + "}"));
        var result = await h.Client.GetBansAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
        Assert.Single(h.Handler.Uris);
    }

    [Fact]
    public async Task Explicitly_absent_ban_record_remains_unavailable()
    {
        using var h = Harness.Create(_ => Json("{\"players\":[]}"));
        var result = await h.Client.GetBansAsync(Id, default);
        Assert.Equal(DataState.Unavailable, result.State);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_after_supports_delta_and_http_date(bool useDate)
    {
        var call = 0;
        using var h = Harness.Create(_ =>
        {
            if (++call != 1) return Json(ProfileJson);
            var result = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            result.Headers.RetryAfter = useDate
                ? new RetryConditionHeaderValue(FakeClock.Start + TimeSpan.FromSeconds(10))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return result;
        });
        Assert.True((await h.Client.GetProfileAsync(Id, default)).IsReady);
        Assert.Equal(TimeSpan.FromSeconds(10), h.Delay.Delays.Single());
        Assert.Equal(2, h.Metrics.Requests);
    }

    [Fact]
    public async Task Long_retry_after_stops_instead_of_retrying_earlier_than_steam_allows()
    {
        using var h = Harness.Create(_ =>
        {
            var result = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            result.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return result;
        });
        Assert.Equal(DataState.RateLimited, (await h.Client.GetProfileAsync(Id, default)).State);
        Assert.Single(h.Handler.Uris);
        Assert.Empty(h.Delay.Delays);
    }

    [Fact]
    public async Task Timeout_retries_are_bounded_and_caller_cancellation_propagates()
    {
        using var h = Harness.Create(_ => throw new TaskCanceledException("sensitive URL key=" + Key));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Equal(3, h.Metrics.Requests);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Client.GetProfileAsync(Id, canceled.Token));
        Assert.Equal(3, h.Metrics.Requests);
    }

    [Fact]
    public async Task Actual_attempt_timeout_cancels_http_without_blocking()
    {
        using var handler = new AsyncHandler(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(ProfileJson);
        });
        using var client = new SteamApiClient(new(handler), new() { ApiKey = Key, RequestTimeout = TimeSpan.FromMilliseconds(20), MaxAttempts = 1 },
            new CapturingLogger(), new FakeClock(), new ImmediateDelay(), new());
        Assert.Equal(DataState.Failed, (await client.GetProfileAsync(Id, default)).State);
    }

    [Fact]
    public async Task Inflight_caller_cancellation_propagates_without_retry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(ProfileJson);
        });
        var metrics = new SteamRequestMetrics();
        using var client = new SteamApiClient(new(handler), new() { ApiKey = Key },
            new CapturingLogger(), new FakeClock(), new ImmediateDelay(), metrics);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetProfileAsync(Id, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, metrics.Requests);
        Assert.Equal(0, metrics.Retries);
    }

    [Fact]
    public async Task Parallel_http_work_never_exceeds_configured_capacity()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        using var handler = new AsyncHandler(async ct =>
        {
            var count = Interlocked.Increment(ref active);
            int old;
            do { old = maximum; } while (count > old && Interlocked.CompareExchange(ref maximum, count, old) != old);
            if (count == 4) entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref active);
            return Json(ProfileJson);
        });
        var metrics = new SteamRequestMetrics();
        using var client = new SteamApiClient(new(handler), new() { ApiKey = Key, MaxConcurrentRequests = 4 },
            new CapturingLogger(), new FakeClock(), new ImmediateDelay(), metrics);
        var pending = Enumerable.Range(0, 12).Select(_ => client.GetProfileAsync(Id, default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, metrics.Requests);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(pending), result => Assert.True(result.IsReady));
        Assert.Equal(4, maximum);
        Assert.Equal(12, metrics.Requests);
    }

    [Fact]
    public async Task Secret_never_reaches_errors_or_application_logs()
    {
        using var h = Harness.Create(_ => throw new HttpRequestException("https://api.steampowered.com/?key=" + Key));
        var result = await h.Client.GetProfileAsync(Id, default);
        Assert.DoesNotContain(Key, result.Message!);
        Assert.DoesNotContain(Key, string.Join(" ", h.Logger.Messages));
        Assert.DoesNotContain("key=", string.Join(" ", h.Logger.Messages));
        Assert.DoesNotContain(Key, h.Options.ToString());
    }

    [Fact]
    public async Task Factory_builtin_loggers_are_removed_even_at_trace_level()
    {
        var provider = new CapturingProvider();
        using var handler = new StubHandler(_ => Json(ProfileJson));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        services.AddGamePulseInfrastructure(new() { ApiKey = Key });
        services.AddHttpClient("SteamApi").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var container = services.BuildServiceProvider();
        var result = await container.GetRequiredService<ISteamApiClient>().GetProfileAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.DoesNotContain(provider.Messages, message => message.Contains(Key) || message.Contains("key="));
    }

    [Fact]
    public async Task Cache_hit_expiry_force_refresh_and_failed_refresh_behave_independently()
    {
        var call = 0;
        using var h = Harness.Create(_ => ++call == 4 ? new(HttpStatusCode.Forbidden) : Json(ProfileJson));
        var lookup = new ProfileLookupService(h.Client, h.Options, h.Clock);
        Assert.False((await lookup.FindAsync(Id, false, default)).FromCache);
        Assert.True((await lookup.FindAsync(Id, false, default)).FromCache);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False((await lookup.FindAsync(Id, false, default)).FromCache);
        Assert.False((await lookup.FindAsync(Id, true, default)).FromCache);
        Assert.Equal(DataState.Unavailable, (await lookup.FindAsync(Id, true, default)).State);
        Assert.True((await lookup.FindAsync(Id, false, default)).IsReady);
        Assert.Equal(5, call);
    }

    [Fact]
    public async Task Cache_capacity_evicts_oldest_success_and_metadata_loads_separately()
    {
        using var h = Harness.Create(uri => uri.AbsolutePath.Contains("GetSteamLevel")
            ? Json("""{"response":{"player_level":10}}""") : Json(ProfileJson), new() { ApiKey = Key, MaxCacheEntries = 1 });
        var lookup = new ProfileLookupService(h.Client, h.Options, h.Clock);
        await lookup.FindAsync(Id, false, default);
        await lookup.GetLevelAsync(Id, false, default);
        Assert.False((await lookup.FindAsync(Id, false, default)).FromCache);
        Assert.Equal(3, h.Metrics.Requests);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Harness : IDisposable
    {
        public SteamApiClient Client { get; set; } = null!;
        public required StubHandler Handler { get; init; }
        public required SteamOptions Options { get; init; }
        public FakeClock Clock { get; } = new();
        public ImmediateDelay Delay { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public SteamRequestMetrics Metrics { get; } = new();
        public static Harness Create(Func<Uri, HttpResponseMessage> respond, SteamOptions? options = null)
        {
            var handler = new StubHandler(respond);
            var h = new Harness { Client = null!, Handler = handler, Options = options ?? new() { ApiKey = Key } };
            h.Client = new(new HttpClient(handler), h.Options, h.Logger, h.Clock, h.Delay, h.Metrics);
            return h;
        }
        public void Dispose() => Client.Dispose();
    }
    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }
    private sealed class FakeClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-16T10:00:00Z");
        private DateTimeOffset now = Start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
    private sealed class ImmediateDelay : IRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
    private sealed class CapturingLogger : ILogger<SteamApiClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }
        private sealed class Logger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
        }
    }
}

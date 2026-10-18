using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GamePulse.Core;
using GamePulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.IntegrationTests;

public sealed class AchievementClientTests
{
    private const string Id = "76561198000000001";
    private const string Other = "76561198000000002";
    private const string Key = "synthetic-achievement-test-key";
    private const uint App = 730;

    [Fact]
    public async Task Selected_game_uses_schema_first_then_player_on_fixed_HTTPS_endpoints_with_English_strings()
    {
        using var harness = new Harness(uri => Json(IsSchema(uri) ? Schema() : Player()));
        var result = await Service(harness).GetAchievementsAsync(Id, App, false, default);
        Assert.True(result.IsReady);
        Assert.True(result.Data!.IsComplete);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.Equal(1, result.Data.UnlockedCount);
        Assert.Equal(50d, result.Data.Completion);
        Assert.Equal("First step", result.Data.Achievements[0].DisplayName);
        Assert.Equal("A test description", result.Data.Achievements[0].Description);
        Assert.False(result.Data.Achievements[0].Hidden);
        Assert.True(result.Data.Achievements[1].Hidden);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), result.Data.Achievements[0].UnlockedAt);
        Assert.Null(result.Data.Achievements[1].UnlockedAt);
        var uris = harness.Uris;
        Assert.Equal(2, uris.Length);
        Assert.Equal("/ISteamUserStats/GetSchemaForGame/v2/", uris[0].AbsolutePath);
        Assert.Equal("/ISteamUserStats/GetPlayerAchievements/v1/", uris[1].AbsolutePath);
        Assert.Equal(Id, Query(uris[1], "steamid"));
        Assert.All(uris, uri =>
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("api.steampowered.com", uri.Host);
            Assert.Equal("730", Query(uri, "appid"));
            Assert.Equal("english", Query(uri, "l"));
        });
    }

    [Theory]
    [InlineData("{}", DataState.Unavailable)]
    [InlineData("{\"game\":{}}", DataState.Unavailable)]
    [InlineData("{\"game\":{\"availableGameStats\":{}}}", DataState.Unavailable)]
    [InlineData("{\"game\":{\"availableGameStats\":{\"achievements\":null}}}", DataState.Unavailable)]
    [InlineData("{\"game\":{\"availableGameStats\":{\"achievements\":[]}}}", DataState.NoAchievements)]
    [InlineData("null", DataState.Failed)]
    [InlineData("not-json", DataState.Failed)]
    public async Task Only_explicit_schema_empty_array_establishes_no_achievements(string json, DataState expected)
    {
        using var harness = new Harness(_ => Json(json));
        var service = Service(harness);
        var result = await service.GetAchievementsAsync(Id, App, false, default);
        Assert.Equal(expected, result.State);
        Assert.Single(harness.Uris); // No player call when schema unavailable or explicitly empty.
        if (expected == DataState.NoAchievements)
        {
            Assert.Empty(result.Data!.Achievements);
            Assert.True(result.Data.IsComplete);
            Assert.Null(result.Data.Completion);
            Assert.True((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        }
        else Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":\" \"}]")]
    [InlineData("[{\"name\":\"A\",\"hidden\":2}]")]
    [InlineData("[{\"name\":\"A\",\"hidden\":true}]")]
    [InlineData("[{\"name\":\"A\"},{\"name\":\"A\"}]")]
    public async Task Malformed_or_duplicate_schema_rows_fail_without_silently_dropping_definitions(string array)
    {
        using var harness = new Harness(_ => Json("{\"game\":{\"availableGameStats\":{\"achievements\":" + array + "}}}"));
        var result = await harness.Client.GetAchievementSchemaAsync(App, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("{}", DataState.Failed)]
    [InlineData("{\"playerstats\":{}}", DataState.Failed)]
    [InlineData("{\"playerstats\":{\"success\":null}}", DataState.Failed)]
    [InlineData("{\"playerstats\":{\"success\":true}}", DataState.Failed)]
    [InlineData("{\"playerstats\":{\"success\":false}}", DataState.Unavailable)]
    [InlineData("{\"playerstats\":{\"success\":false,\"error\":\"Requested app has no stats\"}}", DataState.Unavailable)]
    [InlineData("{\"playerstats\":{\"success\":false,\"error\":\"Profile is not public\"}}", DataState.Private)]
    [InlineData("{\"playerstats\":{\"success\":true,\"steamID\":\"76561198000000002\",\"achievements\":[]}}", DataState.Failed)]
    [InlineData("{\"playerstats\":{\"success\":true,\"gameID\":\"440\",\"achievements\":[]}}", DataState.Failed)]
    [InlineData("{\"playerstats\":{\"success\":true,\"gameID\":730,\"achievements\":[]}}", DataState.Failed)]
    public async Task Player_availability_and_source_identity_are_explicit(string json, DataState expected)
    {
        using var harness = new Harness(_ => Json(json));
        var result = await harness.Client.GetPlayerAchievementsAsync(Id, App, default);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"apiname\":\"A\"}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":null}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":2}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":true}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":0,\"unlocktime\":1}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":1,\"unlocktime\":-1}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":1,\"unlocktime\":9223372036854775807}]")]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":0},{\"apiname\":\"A\",\"achieved\":1}]")]
    public async Task Invalid_player_rows_are_never_treated_as_locked(string array)
    {
        using var harness = new Harness(_ => Json("{\"playerstats\":{\"success\":true,\"achievements\":" + array + "}}"));
        var result = await harness.Client.GetPlayerAchievementsAsync(Id, App, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task Missing_optional_schema_metadata_and_unlock_dates_remain_unknown()
    {
        using var harness = new Harness(uri => Json(IsSchema(uri)
            ? """{"game":{"availableGameStats":{"achievements":[{"name":"A"}]}}}"""
            : """{"playerstats":{"success":true,"gameID":"730","steamID":"76561198000000001","achievements":[{"apiname":"A","achieved":1}]}}"""));
        var detail = Assert.Single((await Service(harness).GetAchievementsAsync(Id, App, false, default)).Data!.Achievements);
        Assert.Null(detail.DisplayName);
        Assert.Null(detail.Description);
        Assert.Null(detail.Hidden);
        Assert.True(detail.Unlocked);
        Assert.Null(detail.UnlockedAt);
    }

    [Theory]
    [InlineData(0, 0d)]
    [InlineData(1, 100d)]
    public async Task Explicit_zero_and_complete_progress_remain_ready(int achieved, double percent)
    {
        using var harness = new Harness(uri => Json(IsSchema(uri) ? Schema("A") : Player("A", achieved)));
        var result = await Service(harness).GetAchievementsAsync(Id, App, false, default);
        Assert.Equal(DataState.Ready, result.State);
        Assert.True(result.Data!.IsComplete);
        Assert.Equal(percent, result.Data.Completion);
    }

    [Theory]
    [InlineData("[]", 2)]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":1}]", 2)]
    [InlineData("[{\"apiname\":\"A\",\"achieved\":1},{\"apiname\":\"B\",\"achieved\":0},{\"apiname\":\"EXTRA\",\"achieved\":1}]", 3)]
    public async Task Schema_player_mismatch_preserves_union_and_unknown_unlocks_with_NA_progress(string array, int count)
    {
        using var harness = new Harness(uri => Json(IsSchema(uri) ? Schema() : "{\"playerstats\":{\"success\":true,\"achievements\":" + array + "}}"));
        var service = Service(harness);
        var result = await service.GetAchievementsAsync(Id, App, false, default);
        Assert.True(result.IsReady);
        Assert.False(result.Data!.IsComplete);
        Assert.Equal(count, result.Data.Achievements.Count);
        Assert.Null(result.Data.TotalCount);
        Assert.Null(result.Data.UnlockedCount);
        Assert.Null(result.Data.Completion);
        if (count == 2) Assert.Null(result.Data.Achievements.Single(x => x.ApiName == "B").Unlocked);
        else Assert.Null(result.Data.Achievements.Single(x => x.ApiName == "EXTRA").DisplayName);
        Assert.False((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.Equal(3, harness.Uris.Length); // Valid schema cached, partial player result retried.
    }

    [Theory]
    [InlineData(401, DataState.Unavailable, 1)]
    [InlineData(403, DataState.Unavailable, 1)]
    [InlineData(404, DataState.NotFound, 1)]
    [InlineData(429, DataState.RateLimited, 3)]
    [InlineData(500, DataState.Failed, 3)]
    public async Task Generic_HTTP_failures_preserve_semantics_and_retry_bound(int status, DataState expected, int requests)
    {
        using var harness = new Harness(_ => new((HttpStatusCode)status));
        var result = await Service(harness).GetAchievementsAsync(Id, App, false, default);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Data);
        Assert.Equal(requests, harness.Metrics.Requests);
        Assert.Equal(requests - 1, harness.Metrics.Retries);
    }

    [Theory]
    [InlineData("Profile is not public", DataState.Private)]
    [InlineData("Requested app has no stats", DataState.Unavailable)]
    public async Task Player_HTTP_400_only_interprets_structured_failure_without_exposing_raw_body(string error, DataState expected)
    {
        using var harness = new Harness(uri => IsSchema(uri) ? Json(Schema()) : new(HttpStatusCode.BadRequest)
            { Content = new StringContent(JsonSerializer.Serialize(new { playerstats = new { success = false, error } })) });
        var result = await Service(harness).GetAchievementsAsync(Id, App, false, default);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Data);
        Assert.Equal(2, harness.Uris.Length);
    }

    [Fact]
    public async Task Generic_400_and_forged_success_body_do_not_establish_progress()
    {
        using var harness = new Harness(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent(Player()) });
        Assert.Equal(DataState.Failed, (await harness.Client.GetPlayerAchievementsAsync(Id, App, default)).State);
        Assert.Equal(DataState.Failed, (await harness.Client.GetAchievementSchemaAsync(App, default)).State);
    }

    [Fact]
    public async Task Missing_key_and_strict_input_validation_never_send_HTTP()
    {
        using var harness = new Harness(_ => throw new InvalidOperationException("No HTTP expected"), new());
        Assert.Equal(DataState.MissingKey, (await Service(harness).GetAchievementsAsync(Id, App, false, default)).State);
        Assert.Equal(DataState.InvalidInput, (await Service(harness).GetAchievementsAsync("https://steamcommunity.com/profiles/" + Id, App, false, default)).State);
        Assert.Equal(DataState.InvalidInput, (await Service(harness).GetAchievementsAsync(Id, 0, false, default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetAchievementSchemaAsync(0, default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetPlayerAchievementsAsync("bad", App, default)).State);
        Assert.Empty(harness.Uris);
    }

    [Fact]
    public async Task Response_safety_limit_fails_explicitly_without_truncation()
    {
        using var harness = new Harness(_ => Json("{\"game\":{\"ignored\":\"" + new string('x', 8 * 1024 * 1024) + "\"}}"));
        Assert.Equal(DataState.Failed, (await harness.Client.GetAchievementSchemaAsync(App, default)).State);
        Assert.Single(harness.Uris);
    }

    [Fact]
    public async Task Schema_is_shared_by_app_across_profiles_and_player_TTL_expires_first()
    {
        using var harness = new Harness(uri => Json(IsSchema(uri) ? Schema() : Player()));
        var service = Service(harness);
        Assert.False((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.True((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        await service.GetAchievementsAsync(Other, App, false, default);
        Assert.Equal(3, harness.Uris.Length);
        harness.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.Equal(4, harness.Uris.Length);
        harness.Clock.Advance(TimeSpan.FromHours(7));
        await service.GetAchievementsAsync(Id, App, false, default);
        Assert.Equal(6, harness.Uris.Length);
        await service.GetAchievementsAsync(Id, App, true, default);
        Assert.Equal(8, harness.Uris.Length);
    }

    [Theory]
    [InlineData(0, 16000000L)]
    [InlineData(30000, 1L)]
    public async Task Oversized_cache_entries_remain_complete_but_are_not_retained(int rows, long bytes)
    {
        using var harness = new Harness(uri => Json(IsSchema(uri) ? Schema() : Player()),
            new() { ApiKey = Key, MaxAchievementCacheRows = rows, MaxAchievementCacheBytes = bytes });
        var service = Service(harness);
        Assert.Equal(2, (await service.GetAchievementsAsync(Id, App, false, default)).Data!.Achievements.Count);
        Assert.False((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.Equal(4, harness.Uris.Length);
    }

    [Fact]
    public async Task Entry_budget_evicts_oldest_profile_and_schema_slots()
    {
        using var harness = new Harness(uri => Json(IsSchema(uri) ? Schema() : Player()), new() { ApiKey = Key, MaxCacheEntries = 1 });
        var service = Service(harness);
        await service.GetAchievementsAsync(Id, 730, false, default);
        await service.GetAchievementsAsync(Other, 440, false, default);
        Assert.False((await service.GetAchievementsAsync(Id, 730, false, default)).FromCache);
        Assert.Equal(6, harness.Uris.Length);
    }

    [Fact]
    public async Task Failed_forced_refresh_removes_stale_success_and_remains_retryable()
    {
        var fail = false;
        using var harness = new Harness(uri => fail ? new(HttpStatusCode.Forbidden) : Json(IsSchema(uri) ? Schema() : Player()));
        var service = Service(harness);
        await service.GetAchievementsAsync(Id, App, false, default);
        fail = true;
        Assert.Equal(DataState.Unavailable, (await service.GetAchievementsAsync(Id, App, true, default)).State);
        Assert.Equal(DataState.Unavailable, (await service.GetAchievementsAsync(Id, App, false, default)).State);
        Assert.Equal(4, harness.Uris.Length);
        fail = false;
        Assert.False((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.Equal(6, harness.Uris.Length);
    }

    [Fact]
    public async Task Identical_observers_share_requests_and_one_cancellation_does_not_cancel_the_other()
    {
        var entered = Signal();
        var release = Signal();
        var transportCanceled = false;
        var fake = new DelegateClient(async ct =>
        {
            using var registration = ct.Register(() => transportCanceled = true);
            entered.TrySetResult();
            await release.Task;
            return SchemaOutcome();
        });
        var service = new SteamAchievementService(fake, new(), TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var first = service.GetAchievementsAsync(Id, App, false, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.GetAchievementsAsync(Id, App, false, default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(transportCanceled);
        release.TrySetResult();
        Assert.True((await second).Data!.IsComplete);
        Assert.Equal(1, fake.SchemaCalls);
        Assert.Equal(1, fake.PlayerCalls);
    }

    [Fact]
    public async Task Cross_profile_observers_share_schema_but_keep_player_requests_separate()
    {
        var entered = Signal();
        var release = Signal();
        var fake = new DelegateClient(async _ => { entered.TrySetResult(); await release.Task; return SchemaOutcome(); });
        var service = new SteamAchievementService(fake, new(), TimeProvider.System);
        var first = service.GetAchievementsAsync(Id, App, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.GetAchievementsAsync(Other, App, false, default);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, fake.SchemaCalls);
        Assert.Equal(2, fake.PlayerCalls);
    }

    [Fact]
    public async Task Last_observer_cancellation_cancels_transport_and_prevents_late_cache_write()
    {
        var entered = Signal();
        var canceled = Signal();
        var release = Signal();
        var fake = new DelegateClient(_ => Task.FromResult(SchemaOutcome()), async (_, ct) =>
        {
            using var registration = ct.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation until a late result arrives.
            return PlayerOutcome();
        });
        var service = new SteamAchievementService(fake, new(), TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var first = service.GetAchievementsAsync(Id, App, false, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        release.TrySetResult();
        Assert.False((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.True((await service.GetAchievementsAsync(Id, App, false, default)).FromCache);
        Assert.Equal(2, fake.PlayerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_player_result_cannot_overwrite_new_force_or_its_failure(bool failRefresh)
    {
        var old = new TaskCompletionSource<Outcome<PlayerAchievements>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<Outcome<PlayerAchievements>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fake = new DelegateClient(_ => Task.FromResult(SchemaOutcome()), (_, _) => ++calls switch
        {
            1 => old.Task,
            2 => fresh.Task,
            _ => Task.FromResult(PlayerOutcome(false))
        });
        var service = new SteamAchievementService(fake, new(), TimeProvider.System);
        var first = service.GetAchievementsAsync(Id, App, false, default);
        var second = service.GetAchievementsAsync(Id, App, true, default);
        fresh.SetResult(failRefresh ? Outcome<PlayerAchievements>.Failure(DataState.Unavailable, "Unavailable") : PlayerOutcome(false));
        await second;
        old.SetResult(PlayerOutcome(true));
        await first;
        var next = await service.GetAchievementsAsync(Id, App, false, default);
        Assert.Equal(!failRefresh, next.FromCache);
        Assert.False(Assert.Single(next.Data!.Achievements).Unlocked);
        Assert.Equal(failRefresh ? 3 : 2, calls);
    }

    [Fact]
    public async Task Old_schema_completion_cannot_replace_new_forced_schema_for_another_profile()
    {
        var old = new TaskCompletionSource<Outcome<AchievementSchema>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<Outcome<AchievementSchema>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var schemaCalls = 0;
        var fake = new DelegateClient(_ => ++schemaCalls == 1 ? old.Task : fresh.Task,
            (_, _) => Task.FromResult(PlayerOutcome(false, "B")));
        var service = new SteamAchievementService(fake, new(), TimeProvider.System);
        var first = service.GetAchievementsAsync(Id, App, false, default);
        var second = service.GetAchievementsAsync(Id, App, true, default);
        fresh.SetResult(SchemaOutcome("B"));
        await second;
        old.SetResult(SchemaOutcome("A"));
        Assert.False((await first).Data!.IsComplete);
        var next = await service.GetAchievementsAsync(Other, App, false, default);
        Assert.True(next.Data!.IsComplete);
        Assert.Equal("B", Assert.Single(next.Data.Achievements).ApiName);
        Assert.Equal(2, schemaCalls);
    }

    [Fact]
    public async Task Retry_after_is_respected_and_long_server_wait_is_not_bypassed()
    {
        using var harness = new Harness(_ => new(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new(TimeSpan.FromSeconds(4)) } });
        Assert.Equal(DataState.RateLimited, (await harness.Client.GetAchievementSchemaAsync(App, default)).State);
        Assert.Equal(2, harness.Delay.Delays.Count);
        Assert.All(harness.Delay.Delays, value => Assert.True(value >= TimeSpan.FromSeconds(4)));
        using var longWait = new Harness(_ => new(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new(TimeSpan.FromMinutes(1)) } });
        await longWait.Client.GetAchievementSchemaAsync(App, default);
        Assert.Single(longWait.Uris);
        Assert.Empty(longWait.Delay.Delays);
    }

    [Fact]
    public async Task Timeout_is_bounded_and_pre_cancellation_never_sends_a_request()
    {
        using var handler = new AsyncHandler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(Schema()); });
        var metrics = new SteamRequestMetrics();
        using var client = new SteamApiClient(new(handler), new() { ApiKey = Key, MaxAttempts = 1, RequestTimeout = TimeSpan.FromMilliseconds(10) },
            NullLogger<SteamApiClient>.Instance, TimeProvider.System, new ImmediateDelay(), metrics);
        Assert.Equal(DataState.Failed, (await client.GetAchievementSchemaAsync(App, default)).State);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAchievementSchemaAsync(App, cancel.Token));
        Assert.Equal(1, metrics.Requests);
    }

    [Fact]
    public async Task Secret_and_remote_error_body_never_enter_logs_or_public_errors()
    {
        var logger = new CaptureLogger();
        using var handler = new Handler(_ => throw new HttpRequestException("https://api.steampowered.com?key=" + Key));
        using var client = new SteamApiClient(new(handler), new() { ApiKey = Key }, logger, TimeProvider.System, new ImmediateDelay(), new());
        var result = await client.GetAchievementSchemaAsync(App, default);
        Assert.DoesNotContain(Key, result.Message ?? "");
        Assert.All(logger.Messages, line => { Assert.DoesNotContain(Key, line); Assert.DoesNotContain("key=", line); });
        using var harness = new Harness(_ => Json(JsonSerializer.Serialize(new { playerstats = new { success = false, error = Key } })));
        var failed = await harness.Client.GetPlayerAchievementsAsync(Id, App, default);
        Assert.DoesNotContain(Key, failed.Message ?? "");
    }

    [Fact]
    public async Task DI_reuses_shared_transport_and_no_key_setup_state()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGamePulseInfrastructure(new());
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<ISteamApiClient>(), provider.GetRequiredService<ISteamAchievementClient>());
        Assert.Equal(DataState.MissingKey,
            (await provider.GetRequiredService<IAchievementService>().GetAchievementsAsync(Id, App, false, default)).State);
    }

    private static SteamAchievementService Service(Harness harness) => new(harness.Client, harness.Options, harness.Clock);
    private static bool IsSchema(Uri uri) => uri.AbsolutePath.Contains("GetSchemaForGame", StringComparison.Ordinal);
    private static string Query(Uri uri, string name) => Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&')
        .Single(part => part.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    private static string Schema(string? single = null) => JsonSerializer.Serialize(new { game = new { gameName = "Test game",
        availableGameStats = new { achievements = (single is null ? new[] { "A", "B" } : [single]).Select((name, index) =>
            new { name, displayName = index == 0 ? "First step" : "Hidden test", description = "A test description", hidden = index }) } } });
    private static string Player(string? single = null, int achieved = 1) => JsonSerializer.Serialize(new { playerstats = new { success = true,
        achievements = (single is null ? new[] { "A", "B" } : [single]).Select((apiname, index) =>
            new { apiname, achieved = index == 0 ? achieved : 0, unlocktime = index == 0 && achieved == 1 ? 1_700_000_000 : 0 }) } });
    private static Outcome<AchievementSchema> SchemaOutcome(string name = "A") => Outcome<AchievementSchema>.Ready(new(App, "Test game", [new(name, name, null, false)]));
    private static Outcome<PlayerAchievements> PlayerOutcome(bool unlocked = true, string name = "A") => Outcome<PlayerAchievements>.Ready(new(App, [new(name, unlocked, null)]));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Harness : IDisposable
    {
        private readonly Handler handler;
        public SteamApiClient Client { get; }
        public SteamOptions Options { get; }
        public Clock Clock { get; } = new();
        public SteamRequestMetrics Metrics { get; } = new();
        public ImmediateDelay Delay { get; } = new();
        public Uri[] Uris => handler.Uris.ToArray();
        public Harness(Func<Uri, HttpResponseMessage> respond, SteamOptions? options = null)
        {
            handler = new(respond);
            Options = options ?? new() { ApiKey = Key };
            Client = new(new(handler), Options, NullLogger<SteamApiClient>.Instance, Clock, Delay, Metrics);
        }
        public void Dispose() => Client.Dispose();
    }
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Uris { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Enqueue(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
    private sealed class AsyncHandler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request.RequestUri!, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
    private sealed class ImmediateDelay : IRetryDelay
    {
        public ConcurrentQueue<TimeSpan> Delays { get; } = new();
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Enqueue(delay);
            return Task.CompletedTask;
        }
    }
    private sealed class CaptureLogger : ILogger<SteamApiClient>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
    private sealed class DelegateClient(Func<CancellationToken, Task<Outcome<AchievementSchema>>> schema,
        Func<string, CancellationToken, Task<Outcome<PlayerAchievements>>>? player = null) : ISteamAchievementClient
    {
        private int schemaCalls;
        private int playerCalls;
        public int SchemaCalls => schemaCalls;
        public int PlayerCalls => playerCalls;
        public Task<Outcome<AchievementSchema>> GetAchievementSchemaAsync(uint appId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref schemaCalls);
            return schema(cancellationToken);
        }
        public Task<Outcome<PlayerAchievements>> GetPlayerAchievementsAsync(string steamId, uint appId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref playerCalls);
            return player?.Invoke(steamId, cancellationToken) ?? Task.FromResult(PlayerOutcome());
        }
    }
}

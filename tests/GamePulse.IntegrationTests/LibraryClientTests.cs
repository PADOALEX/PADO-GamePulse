using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GamePulse.Core;
using GamePulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.IntegrationTests;

public sealed class LibraryClientTests
{
    private const string Id = "76561198000000001";
    private const string OtherId = "76561198000000002";
    private const string Key = "synthetic-library-test-key";
    private const string OwnedJson = """{"response":{"game_count":2,"games":[{"appid":10,"name":"A","playtime_forever":0,"rtime_last_played":0},{"appid":20,"name":"B","playtime_forever":123,"playtime_2weeks":12,"rtime_last_played":1730000000}]}}""";
    private const string RecentJson = """{"response":{"total_count":1,"games":[{"appid":20,"name":"B","playtime_forever":123,"playtime_2weeks":12}]}}""";

    [Fact]
    public async Task Library_uses_documented_service_input_json_and_preserves_zero_and_unknowns()
    {
        using var harness = new Harness(_ => Json(OwnedJson));
        var result = await harness.Client.GetLibraryAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.True(result.Data!.IsComplete);
        Assert.Equal(2, result.Data.ReportedCount);
        Assert.Equal(0, result.Data.Games[0].PlaytimeMinutes);
        Assert.Null(result.Data.Games[0].RecentPlaytimeMinutes);
        Assert.Null(result.Data.Games[0].LastPlayed);
        Assert.Equal(123, result.Data.Games[1].PlaytimeMinutes);
        Assert.Equal(12, result.Data.Games[1].RecentPlaytimeMinutes);
        Assert.NotNull(result.Data.Games[1].LastPlayed);
        var uri = Assert.Single(harness.Uris);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("api.steampowered.com", uri.Host);
        Assert.Equal("/IPlayerService/GetOwnedGames/v1/", uri.AbsolutePath);
        using var args = InputJson(uri);
        Assert.Equal(Id, args.RootElement.GetProperty("steamid").GetString());
        Assert.True(args.RootElement.GetProperty("include_appinfo").GetBoolean());
        Assert.True(args.RootElement.GetProperty("include_played_free_games").GetBoolean());
    }

    [Fact]
    public async Task Compared_profile_level_also_uses_documented_service_input_json()
    {
        using var harness = new Harness(_ => Json("""{"response":{"player_level":0}}"""));
        var result = await harness.Client.GetLevelAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.Equal(0, result.Data!.Value);
        var uri = Assert.Single(harness.Uris);
        Assert.Equal("/IPlayerService/GetSteamLevel/v1/", uri.AbsolutePath);
        using var args = InputJson(uri);
        Assert.Equal(Id, args.RootElement.GetProperty("steamid").GetString());
    }

    [Fact]
    public async Task Recent_uses_all_results_without_count_cap_and_a_separate_endpoint()
    {
        using var harness = new Harness(_ => Json(RecentJson));
        var result = await harness.Client.GetRecentAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.Equal(1, result.Data!.ReportedCount);
        Assert.Equal(12, Assert.Single(result.Data.Games).RecentPlaytimeMinutes);
        var uri = Assert.Single(harness.Uris);
        Assert.Equal("/IPlayerService/GetRecentlyPlayedGames/v1/", uri.AbsolutePath);
        using var args = InputJson(uri);
        Assert.Equal(Id, args.RootElement.GetProperty("steamid").GetString());
        Assert.False(args.RootElement.TryGetProperty("count", out _));
    }

    [Theory]
    [InlineData("{\"response\":{}}", DataState.Unavailable)]
    [InlineData("{\"response\":{\"games\":[]}}", DataState.Unavailable)]
    [InlineData("{}", DataState.Failed)]
    [InlineData("null", DataState.Failed)]
    [InlineData("{broken", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1}}", DataState.Unavailable)]
    [InlineData("{\"response\":{\"game_count\":2,\"games\":[{\"appid\":1}]}}", DataState.Unavailable)]
    [InlineData("{\"response\":{\"game_count\":0,\"games\":[{\"appid\":1}]}}", DataState.Unavailable)]
    [InlineData("{\"response\":{\"game_count\":-1,\"games\":[]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[null]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":0}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":-1}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":4294967296}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":2,\"games\":[{\"appid\":1},{\"appid\":1}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":1,\"playtime_forever\":-1}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":1,\"playtime_2weeks\":-1}]}}", DataState.Failed)]
    [InlineData("{\"response\":{\"game_count\":1,\"games\":[{\"appid\":1,\"rtime_last_played\":-1}]}}", DataState.Failed)]
    public async Task Missing_private_ambiguous_or_malformed_library_never_becomes_zero(string json, DataState state)
    {
        using var harness = new Harness(_ => Json(json));
        var result = await harness.Client.GetLibraryAsync(Id, default);
        Assert.Equal(state, result.State);
        Assert.Null(result.Data);
        Assert.NotEqual(DataState.Private, result.State);
        Assert.Single(harness.Uris);
    }

    [Theory]
    [InlineData("{\"response\":{\"game_count\":0}}")]
    [InlineData("{\"response\":{\"game_count\":0,\"games\":[]}}")]
    public async Task Explicit_zero_count_is_a_real_empty_complete_library(string json)
    {
        using var harness = new Harness(_ => Json(json));
        var result = await harness.Client.GetLibraryAsync(Id, default);
        Assert.Equal(DataState.Empty, result.State);
        Assert.True(result.HasData());
        Assert.True(result.Data!.IsComplete);
        Assert.Empty(result.Data.Games);
        Assert.Equal(0, result.Data.ReportedCount);
        Assert.NotNull(result.UpdatedAt);
    }

    [Fact]
    public async Task Missing_minutes_and_game_name_remain_unknown()
    {
        using var harness = new Harness(_ => Json("""{"response":{"game_count":1,"games":[{"appid":42}]}}"""));
        var game = Assert.Single((await harness.Client.GetLibraryAsync(Id, default)).Data!.Games);
        Assert.Null(game.Name);
        Assert.Null(game.PlaytimeMinutes);
        Assert.Null(game.RecentPlaytimeMinutes);
        Assert.Null(game.LastPlayed);
    }

    [Theory]
    [InlineData("{\"response\":{}}", DataState.Unavailable)]
    [InlineData("{\"response\":{\"total_count\":0}}", DataState.Empty)]
    [InlineData("{\"response\":{\"total_count\":0,\"games\":[]}}", DataState.Empty)]
    [InlineData("{\"response\":{\"total_count\":2,\"games\":[{\"appid\":1}]}}", DataState.Unavailable)]
    [InlineData("{\"response\":{\"total_count\":-1}}", DataState.Failed)]
    [InlineData("{\"response\":{\"total_count\":1,\"games\":[{\"appid\":1,\"playtime_2weeks\":-1}]}}", DataState.Failed)]
    public async Task Recent_does_not_infer_empty_from_absent_data(string json, DataState state)
    {
        using var harness = new Harness(_ => Json(json));
        var result = await harness.Client.GetRecentAsync(Id, default);
        Assert.Equal(state, result.State);
        if (state == DataState.Empty) Assert.Empty(result.Data!.Games);
        else Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(403, DataState.Unavailable, 1)]
    [InlineData(404, DataState.NotFound, 1)]
    [InlineData(429, DataState.RateLimited, 3)]
    [InlineData(500, DataState.Failed, 3)]
    public async Task Library_and_recent_keep_http_failures_distinct_and_bound_retries(int status, DataState state, int attempts)
    {
        using var harness = new Harness(_ => new((HttpStatusCode)status));
        Assert.Equal(state, (await harness.Client.GetLibraryAsync(Id, default)).State);
        Assert.Equal(state, (await harness.Client.GetRecentAsync(Id, default)).State);
        Assert.Equal(2 * attempts, harness.Metrics.Requests);
        Assert.Equal(2 * (attempts - 1), harness.Metrics.Retries);
    }

    [Fact]
    public async Task Library_retry_after_is_observed_and_cancellation_prevents_further_attempts()
    {
        var count = 0;
        using var harness = new Harness(_ =>
        {
            if (++count != 1) return Json(OwnedJson);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(8));
            return response;
        });
        Assert.True((await harness.Client.GetLibraryAsync(Id, default)).IsReady);
        Assert.Equal(TimeSpan.FromSeconds(8), Assert.Single(harness.Delay.Delays));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.GetLibraryAsync(Id, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.GetRecentAsync(Id, canceled.Token));
        Assert.Equal(2, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Timeout_and_transport_exceptions_are_safe_for_both_endpoints()
    {
        using var harness = new Harness(_ => throw new TaskCanceledException("sensitive key=" + Key));
        var library = await harness.Client.GetLibraryAsync(Id, default);
        var recent = await harness.Client.GetRecentAsync(Id, default);
        Assert.Equal(DataState.Failed, library.State);
        Assert.Equal(DataState.Failed, recent.State);
        Assert.DoesNotContain(Key, library.Message!);
        Assert.DoesNotContain(Key, recent.Message!);
        Assert.Equal(6, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Missing_key_and_invalid_wire_id_use_no_network()
    {
        using var harness = new Harness(_ => throw new InvalidOperationException("Must not request"), new());
        Assert.Equal(DataState.MissingKey, (await harness.Client.GetLibraryAsync(Id, default)).State);
        Assert.Equal(DataState.MissingKey, (await harness.Client.GetRecentAsync(Id, default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetLibraryAsync("bad", default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetRecentAsync("https://steamcommunity.com/profiles/" + Id, default)).State);
        Assert.Empty(harness.Uris);
    }

    [Fact]
    public async Task Library_above_ten_thousand_games_and_two_megabytes_is_not_truncated()
    {
        const int count = 12_001;
        var json = JsonSerializer.Serialize(new { response = new { game_count = count,
            games = Enumerable.Range(1, count).Select(index => new
                { appid = index, name = new string('n', 220) + index, playtime_forever = index }) } });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024);
        using var harness = new Harness(_ => Json(json));
        var result = await harness.Client.GetLibraryAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.Equal(count, result.Data!.Games.Count);
        Assert.Equal((uint)count, result.Data.Games[^1].AppId);
        Assert.True(result.Data.IsComplete);
        Assert.Single(harness.Uris);
    }

    [Fact]
    public async Task Excessively_large_responses_fail_instead_of_becoming_empty_or_unbounded()
    {
        var body = "{\"response\":{\"ignored\":\"" + new string('x', 16 * 1024 * 1024) + "\"}}";
        using var harness = new Harness(_ => Json(body), new() { ApiKey = Key, MaxAttempts = 1 });
        var result = await harness.Client.GetLibraryAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
        Assert.DoesNotContain(Key, result.Message!);
    }

    [Fact]
    public async Task Cache_uses_distinct_ttl_force_refresh_and_never_caches_failed_refresh()
    {
        var ownedCalls = 0;
        using var harness = new Harness(uri => uri.AbsolutePath.Contains("GetRecentlyPlayedGames")
            ? Json(RecentJson) : ++ownedCalls == 4 ? new(HttpStatusCode.Forbidden) : Json(OwnedJson));
        var service = new SteamLibraryService(harness.Client, harness.Options, harness.Clock);
        var first = await service.GetLibraryAsync(Id, false, default);
        Assert.False(first.FromCache);
        var hit = await service.GetLibraryAsync(Id, false, default);
        Assert.True(hit.FromCache);
        Assert.Equal(first.UpdatedAt, hit.UpdatedAt);
        Assert.False((await service.GetRecentAsync(Id, false, default)).FromCache);
        Assert.True((await service.GetRecentAsync(Id, false, default)).FromCache);
        harness.Clock.Advance(TimeSpan.FromMinutes(2.5));
        Assert.True((await service.GetLibraryAsync(Id, false, default)).FromCache);
        Assert.False((await service.GetRecentAsync(Id, false, default)).FromCache);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await service.GetLibraryAsync(Id, false, default)).FromCache);
        Assert.False((await service.GetLibraryAsync(Id, true, default)).FromCache);
        Assert.Equal(DataState.Unavailable, (await service.GetLibraryAsync(Id, true, default)).State);
        Assert.False((await service.GetLibraryAsync(Id, false, default)).FromCache);
        Assert.Equal(5, ownedCalls);
    }

    [Fact]
    public async Task Explicit_empty_is_cached_but_ambiguous_unavailable_is_not()
    {
        var calls = 0;
        using var harness = new Harness(_ => Json(++calls == 1 ? "{\"response\":{}}" : "{\"response\":{\"game_count\":0}}"));
        var service = new SteamLibraryService(harness.Client, harness.Options, harness.Clock);
        Assert.Equal(DataState.Unavailable, (await service.GetLibraryAsync(Id, false, default)).State);
        Assert.Equal(DataState.Empty, (await service.GetLibraryAsync(Id, false, default)).State);
        Assert.True((await service.GetLibraryAsync(Id, false, default)).FromCache);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(2, 1_000_000)]
    [InlineData(100_000, 700)]
    public async Task Cache_row_and_byte_budgets_evict_oldest_lists_without_truncating_results(int rowBudget, long byteBudget)
    {
        using var harness = new Harness(_ => Json(OwnedJson), new()
            { ApiKey = Key, MaxLibraryCacheRows = rowBudget, MaxLibraryCacheBytes = byteBudget });
        var service = new SteamLibraryService(harness.Client, harness.Options, harness.Clock);
        Assert.Equal(2, (await service.GetLibraryAsync(Id, false, default)).Data!.Games.Count);
        Assert.True((await service.GetLibraryAsync(Id, false, default)).FromCache);
        await service.GetLibraryAsync(OtherId, false, default);
        Assert.False((await service.GetLibraryAsync(Id, false, default)).FromCache);
        Assert.Equal(3, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Single_library_larger_than_cache_budget_is_returned_in_full_but_not_retained()
    {
        using var harness = new Harness(_ => Json(OwnedJson), new() { ApiKey = Key, MaxLibraryCacheRows = 1 });
        var service = new SteamLibraryService(harness.Client, harness.Options, harness.Clock);
        Assert.Equal(2, (await service.GetLibraryAsync(Id, false, default)).Data!.Games.Count);
        Assert.False((await service.GetLibraryAsync(Id, false, default)).FromCache);
        Assert.Equal(2, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Identical_pending_queries_share_transport_and_one_canceled_observer_does_not_cancel_another()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Json(OwnedJson);
        });
        var metrics = new SteamRequestMetrics();
        var options = new SteamOptions { ApiKey = Key };
        using var client = Client(handler, options, new Clock(), metrics);
        var service = new SteamLibraryService(client, options, TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        var first = service.GetLibraryAsync(Id, false, canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.GetLibraryAsync(Id, false, default);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        Assert.True((await second).IsReady);
        Assert.Equal(1, metrics.Requests);
    }

    [Fact]
    public async Task Canceling_final_observer_cancels_transport_and_does_not_cache_a_late_result()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { transportCanceled.TrySetResult(); throw; }
            return Json(OwnedJson);
        });
        var metrics = new SteamRequestMetrics();
        var options = new SteamOptions { ApiKey = Key };
        using var client = Client(handler, options, new Clock(), metrics);
        var service = new SteamLibraryService(client, options, TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        var request = service.GetLibraryAsync(Id, false, canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await transportCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, metrics.Requests);
        Assert.Equal(0, metrics.Retries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_library_request_cannot_replace_new_forced_refresh_even_if_refresh_fails(bool failRefresh)
    {
        var steam = new ControlledLibrary();
        var service = new SteamLibraryService(steam, new(), TimeProvider.System);
        var first = service.GetLibraryAsync(Id, false, default);
        var refresh = service.GetLibraryAsync(Id, true, default);
        steam.Second.SetResult(failRefresh ? Outcome<OwnedLibrary>.Failure(DataState.Unavailable, "Unavailable") : Library("new"));
        await refresh;
        steam.First.SetResult(Library("old"));
        await first;
        var next = await service.GetLibraryAsync(Id, false, default);
        Assert.Equal(failRefresh ? "reload" : "new", next.Data!.Games[0].Name);
        Assert.Equal(!failRefresh, next.FromCache);
        Assert.Equal(failRefresh ? 3 : 2, steam.Calls);
    }

    [Fact]
    public async Task Default_di_wires_profile_and_library_to_shared_transport_and_service()
    {
        using var handler = new Handler(_ => Json(OwnedJson));
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));
        services.AddGamePulseInfrastructure(new() { ApiKey = Key });
        services.AddHttpClient("SteamApi").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var container = services.BuildServiceProvider();
        Assert.Same(container.GetRequiredService<ISteamApiClient>(), container.GetRequiredService<ISteamLibraryClient>());
        Assert.True((await container.GetRequiredService<ILibraryService>().GetLibraryAsync(Id, false, default)).IsReady);
    }

    private static Outcome<OwnedLibrary> Library(string name) => Outcome<OwnedLibrary>.Ready(
        new([new(1, name, 0, null, null)], 1, true));
    private sealed class ControlledLibrary : ISteamLibraryClient
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<Outcome<OwnedLibrary>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Outcome<OwnedLibrary>> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Outcome<OwnedLibrary>> GetLibraryAsync(string steamId, CancellationToken cancellationToken) => ++Calls switch
        {
            1 => First.Task,
            2 => Second.Task,
            _ => Task.FromResult(Library("reload"))
        };
        public Task<Outcome<RecentGames>> GetRecentAsync(string steamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private static JsonDocument InputJson(Uri uri)
    {
        var encoded = uri.Query.TrimStart('?').Split('&').Single(arg => arg.StartsWith("input_json=", StringComparison.Ordinal));
        return JsonDocument.Parse(Uri.UnescapeDataString(encoded["input_json=".Length..]));
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static SteamApiClient Client(HttpMessageHandler handler, SteamOptions options, TimeProvider clock, SteamRequestMetrics metrics) =>
        new(new(handler), options, NullLogger<SteamApiClient>.Instance, clock, new ImmediateDelay(), metrics);
    private sealed class Harness : IDisposable
    {
        private readonly Handler handler;
        public SteamApiClient Client { get; }
        public SteamOptions Options { get; }
        public Clock Clock { get; } = new();
        public SteamRequestMetrics Metrics { get; } = new();
        public ImmediateDelay Delay { get; } = new();
        public IReadOnlyList<Uri> Uris => handler.Uris;
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
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-16T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
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
}

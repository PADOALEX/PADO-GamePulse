using System.Collections.Concurrent;
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

public sealed class FriendsClientTests
{
    private const string Id = "76561198000000001";
    private const string OtherId = "76561198000000002";
    private const string Key = "synthetic-friends-test-key";
    private static string FriendId(int index) => (76561198000100000UL + (ulong)index).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Friend_list_uses_documented_endpoint_and_keeps_timestamps_and_unknown_summaries()
    {
        using var harness = new Harness(_ => Json(Friends(2)));
        var result = await harness.Client.GetFriendListAsync(Id, default);
        Assert.True(result.IsReady);
        Assert.Equal(2, result.Data!.Count);
        Assert.Equal(FriendId(0), result.Data[0].SteamId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), result.Data[0].FriendsSince);
        Assert.Null(result.Data[0].Profile);
        Assert.Equal(DataState.Unavailable, result.Data[0].SummaryState);
        var uri = Assert.Single(harness.Uris);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("api.steampowered.com", uri.Host);
        Assert.Equal("/ISteamUser/GetFriendList/v1/", uri.AbsolutePath);
        Assert.Equal(Id, Query(uri, "steamid"));
        Assert.Equal("friend", Query(uri, "relationship"));
    }

    [Theory]
    [InlineData("{}", DataState.Unavailable)]
    [InlineData("{\"friendslist\":{}}", DataState.Unavailable)]
    [InlineData("{\"friendslist\":{\"friends\":null}}", DataState.Unavailable)]
    [InlineData("{\"friendslist\":{\"friends\":[]}}", DataState.Empty)]
    [InlineData("null", DataState.Failed)]
    [InlineData("{broken", DataState.Failed)]
    public async Task Missing_friend_list_is_distinct_from_an_explicit_empty_array(string json, DataState expected)
    {
        using var harness = new Harness(_ => Json(json));
        var service = Service(harness);
        var result = await service.GetFriendsAsync(Id, false, default);
        Assert.Equal(expected, result.State);
        if (expected == DataState.Empty)
        {
            Assert.Empty(result.Data!.Friends);
            Assert.True(result.Data.SummariesComplete);
            Assert.True((await service.GetFriendsAsync(Id, false, default)).FromCache);
        }
        else Assert.Null(result.Data);
        Assert.Single(harness.Uris);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"steamid\":\"123\",\"relationship\":\"friend\"}]")]
    [InlineData("[{\"steamid\":\"76561198000000002\",\"relationship\":\"blocked\"}]")]
    [InlineData("[{\"steamid\":\"76561198000000002\"}]")]
    [InlineData("[{\"steamid\":\"76561198000000002\",\"relationship\":\"friend\",\"friend_since\":-1}]")]
    [InlineData("[{\"steamid\":\"76561198000000002\",\"relationship\":\"friend\"},{\"steamid\":\"76561198000000002\",\"relationship\":\"friend\"}]")]
    public async Task Invalid_or_duplicate_relationships_fail_instead_of_silently_dropping_rows(string array)
    {
        using var harness = new Harness(_ => Json("{\"friendslist\":{\"friends\":" + array + "}}"));
        var result = await harness.Client.GetFriendListAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("9223372036854775807")]
    public async Task Unknown_or_out_of_range_friend_dates_are_nullable(string timestamp)
    {
        using var harness = new Harness(_ => Json("{\"friendslist\":{\"friends\":[{\"steamid\":\"" + OtherId +
            "\",\"relationship\":\"friend\",\"friend_since\":" + timestamp + "}]}}"));
        Assert.Null(Assert.Single((await harness.Client.GetFriendListAsync(Id, default)).Data!).FriendsSince);
    }

    [Fact]
    public async Task Summary_batch_preserves_unknown_fields_and_constructs_safe_canonical_links()
    {
        using var harness = new Harness(_ => Json("""{"response":{"players":[{"steamid":"76561198000000002","profileurl":"https://untrusted.invalid","avatarfull":"https://untrusted.invalid/avatar.png"}]}}"""));
        var result = await harness.Client.GetPlayerSummariesAsync([OtherId], default);
        var profile = Assert.Single(result.Data!);
        Assert.Null(profile.DisplayName);
        Assert.Null(profile.PersonaState);
        Assert.Null(profile.Visibility);
        Assert.Null(profile.AvatarUrl);
        Assert.Null(profile.CreatedAt);
        Assert.Equal("https://steamcommunity.com/profiles/" + OtherId, profile.ProfileUrl);
        var uri = Assert.Single(harness.Uris);
        Assert.Equal("/ISteamUser/GetPlayerSummaries/v2/", uri.AbsolutePath);
        Assert.Equal(OtherId, Query(uri, "steamids"));
    }

    [Theory]
    [InlineData("{\"response\":{}}")]
    [InlineData("{\"response\":{\"players\":[null]}}")]
    [InlineData("{\"response\":{\"players\":[{}]}}")]
    [InlineData("{\"response\":{\"players\":[{\"steamid\":\"76561198000000001\"}]}}")]
    [InlineData("{\"response\":{\"players\":[{\"steamid\":\"76561198000000002\"},{\"steamid\":\"76561198000000002\"}]}}")]
    public async Task Malformed_unrequested_or_duplicate_summary_rows_fail_the_batch(string json)
    {
        using var harness = new Harness(_ => Json(json));
        var result = await harness.Client.GetPlayerSummariesAsync([OtherId], default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task Missing_key_invalid_id_and_invalid_summary_batches_never_use_network()
    {
        using var harness = new Harness(_ => throw new InvalidOperationException("No request expected"), new());
        Assert.Equal(DataState.MissingKey, (await Service(harness).GetFriendsAsync(Id, false, default)).State);
        Assert.Equal(DataState.MissingKey, (await harness.Client.GetPlayerSummariesAsync([Id], default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetFriendListAsync("bad", default)).State);
        Assert.Equal(DataState.InvalidInput, (await Service(harness).GetFriendsAsync("https://steamcommunity.com/profiles/" + Id, false, default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetPlayerSummariesAsync([Id, Id], default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetPlayerSummariesAsync(["bad"], default)).State);
        Assert.Equal(DataState.InvalidInput, (await harness.Client.GetPlayerSummariesAsync(Enumerable.Range(0, 101).Select(FriendId).ToArray(), default)).State);
        Assert.Empty((await harness.Client.GetPlayerSummariesAsync([], default)).Data!);
        Assert.Empty(harness.Uris);
    }

    [Fact]
    public async Task More_than_250_friends_are_enriched_in_three_batches_of_at_most_100_without_per_friend_calls()
    {
        using var harness = new Harness(uri => IsList(uri) ? Json(Friends(257)) : Json(Summaries(Ids(uri))));
        var result = await Service(harness).GetFriendsAsync(Id, false, default);
        Assert.True(result.IsReady);
        Assert.True(result.Data!.SummariesComplete);
        Assert.Equal(0, result.Data.FailedSummaryBatches);
        Assert.Equal(257, result.Data.Friends.Count);
        Assert.Equal(257, result.Data.Friends.Count(friend => friend.Profile is not null));
        Assert.Equal(FriendId(256), result.Data.Friends[^1].SteamId);
        Assert.All(result.Data.Friends, friend => Assert.Equal(DataState.Ready, friend.SummaryState));
        var batches = harness.Uris.Where(uri => !IsList(uri)).Select(Ids).ToArray();
        Assert.Equal(3, batches.Length);
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 100));
        Assert.Equal(257, batches.SelectMany(batch => batch).Distinct().Count());
        Assert.Equal(4, harness.Metrics.Requests);
        Assert.All(harness.Uris, uri => Assert.True(IsList(uri) || uri.AbsolutePath.Contains("GetPlayerSummaries", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Partial_summary_response_retains_every_id_and_remains_retryable_without_cache()
    {
        using var harness = new Harness(uri => IsList(uri) ? Json(Friends(3)) : Json(Summaries(Ids(uri).Take(1))));
        var service = Service(harness);
        var result = await service.GetFriendsAsync(Id, false, default);
        Assert.True(result.IsReady);
        Assert.False(result.Data!.SummariesComplete);
        Assert.Equal(0, result.Data.FailedSummaryBatches);
        Assert.Equal(3, result.Data.Friends.Count);
        Assert.NotNull(result.Data.Friends[0].Profile);
        Assert.Null(result.Data.Friends[1].Profile);
        Assert.Equal(DataState.Unavailable, result.Data.Friends[1].SummaryState);
        Assert.NotNull(result.Message);
        Assert.False((await service.GetFriendsAsync(Id, false, default)).FromCache);
        Assert.Equal(4, harness.Metrics.Requests);
    }

    [Theory]
    [InlineData(429, DataState.RateLimited)]
    [InlineData(500, DataState.Failed)]
    [InlineData(403, DataState.Unavailable)]
    public async Task Failed_summary_batch_preserves_all_relationships_and_successful_other_batches(int status, DataState state)
    {
        using var harness = new Harness(uri => IsList(uri) ? Json(Friends(201)) :
            Ids(uri).Contains(FriendId(100)) ? new((HttpStatusCode)status) : Json(Summaries(Ids(uri))));
        var service = Service(harness);
        var result = await service.GetFriendsAsync(Id, false, default);
        Assert.True(result.IsReady);
        Assert.False(result.Data!.SummariesComplete);
        Assert.Equal(1, result.Data.FailedSummaryBatches);
        Assert.Equal(201, result.Data.Friends.Count);
        Assert.Equal(101, result.Data.Friends.Count(friend => friend.Profile is not null));
        Assert.Equal(100, result.Data.Friends.Count(friend => friend.SummaryState == state));
        Assert.Null(result.Data.Friends[100].Profile);
        Assert.NotNull(result.Data.Friends[200].Profile);
        Assert.False((await service.GetFriendsAsync(Id, false, default)).FromCache);
        Assert.Equal(status == 403 ? 8 : 12, harness.Metrics.Requests);
    }

    [Theory]
    [InlineData(401, DataState.Unavailable, 1)]
    [InlineData(403, DataState.Unavailable, 1)]
    [InlineData(404, DataState.NotFound, 1)]
    [InlineData(429, DataState.RateLimited, 3)]
    [InlineData(500, DataState.Failed, 3)]
    public async Task Friend_list_http_errors_never_claim_private_or_empty_and_retries_are_bounded(int status, DataState state, int attempts)
    {
        using var harness = new Harness(_ => new((HttpStatusCode)status));
        var result = await Service(harness).GetFriendsAsync(Id, false, default);
        Assert.Equal(state, result.State);
        Assert.Null(result.Data);
        Assert.NotEqual(DataState.Private, result.State);
        Assert.Equal(attempts, harness.Metrics.Requests);
        Assert.Equal(attempts - 1, harness.Metrics.Retries);
    }

    [Fact]
    public async Task Summary_429_respects_retry_after_and_does_not_run_unbounded_retries()
    {
        var calls = 0;
        using var harness = new Harness(uri =>
        {
            if (IsList(uri)) return Json(Friends(1));
            if (Interlocked.Increment(ref calls) > 1) return Json(Summaries(Ids(uri)));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return response;
        });
        Assert.True((await Service(harness).GetFriendsAsync(Id, false, default)).Data!.SummariesComplete);
        Assert.Equal(TimeSpan.FromSeconds(9), Assert.Single(harness.Delay.Delays));
        Assert.Equal(3, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Timeout_and_exception_messages_do_not_expose_synthetic_key_or_request_uri()
    {
        var logger = new CaptureLogger();
        using var handler = new Handler(_ => throw new HttpRequestException("https://api.steampowered.com/?key=" + Key));
        using var client = new SteamApiClient(new(handler), new() { ApiKey = Key }, logger,
            TimeProvider.System, new ImmediateDelay(), new());
        var list = await client.GetFriendListAsync(Id, default);
        var summaries = await client.GetPlayerSummariesAsync([OtherId], default);
        Assert.Equal(DataState.Failed, list.State);
        Assert.Equal(DataState.Failed, summaries.State);
        Assert.DoesNotContain(Key, list.Message!);
        Assert.DoesNotContain(Key, summaries.Message!);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(Key, message);
            Assert.DoesNotContain("key=", message);
        });
    }

    [Fact]
    public async Task Explicit_timeout_is_safe_and_canceled_request_has_no_retries()
    {
        using var harness = new Harness(_ => throw new TaskCanceledException("key=" + Key));
        var result = await harness.Client.GetFriendListAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.DoesNotContain(Key, result.Message!);
        Assert.Equal(3, harness.Metrics.Requests);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(harness).GetFriendsAsync(Id, false, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Client.GetPlayerSummariesAsync([Id], canceled.Token));
        Assert.Equal(3, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Cache_hit_expiry_force_and_failed_refresh_preserve_observed_availability()
    {
        var listCalls = 0;
        using var harness = new Harness(uri => !IsList(uri) ? Json(Summaries(Ids(uri))) :
            Interlocked.Increment(ref listCalls) == 4 ? new(HttpStatusCode.Forbidden) : Json(Friends(2)));
        var service = Service(harness);
        var first = await service.GetFriendsAsync(Id, false, default);
        var cached = await service.GetFriendsAsync(Id, false, default);
        Assert.False(first.FromCache);
        Assert.True(cached.FromCache);
        Assert.Equal(first.UpdatedAt, cached.UpdatedAt);
        harness.Clock.Advance(TimeSpan.FromMinutes(3.1));
        Assert.False((await service.GetFriendsAsync(Id, false, default)).FromCache);
        Assert.False((await service.GetFriendsAsync(Id, true, default)).FromCache);
        Assert.Equal(DataState.Unavailable, (await service.GetFriendsAsync(Id, true, default)).State);
        Assert.False((await service.GetFriendsAsync(Id, false, default)).FromCache);
        Assert.Equal(5, listCalls);
    }

    [Theory]
    [InlineData(2, 1_000_000)]
    [InlineData(30_000, 2_000)]
    public async Task Friends_cache_row_and_byte_limits_evict_without_truncating_rows(int rowBudget, long byteBudget)
    {
        using var harness = new Harness(uri => IsList(uri) ? Json(Friends(2)) : Json(Summaries(Ids(uri))),
            new() { ApiKey = Key, MaxFriendsCacheRows = rowBudget, MaxFriendsCacheBytes = byteBudget });
        var service = Service(harness);
        Assert.Equal(2, (await service.GetFriendsAsync(Id, false, default)).Data!.Friends.Count);
        Assert.True((await service.GetFriendsAsync(Id, false, default)).FromCache);
        await service.GetFriendsAsync(OtherId, false, default);
        Assert.False((await service.GetFriendsAsync(Id, false, default)).FromCache);
        Assert.Equal(6, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Directory_larger_than_cache_budget_is_returned_whole_and_not_cached()
    {
        using var harness = new Harness(uri => IsList(uri) ? Json(Friends(3)) : Json(Summaries(Ids(uri))),
            new() { ApiKey = Key, MaxFriendsCacheRows = 2 });
        var service = Service(harness);
        Assert.Equal(3, (await service.GetFriendsAsync(Id, false, default)).Data!.Friends.Count);
        Assert.False((await service.GetFriendsAsync(Id, false, default)).FromCache);
        Assert.Equal(4, harness.Metrics.Requests);
    }

    [Fact]
    public async Task Summary_batch_enrichment_is_limited_to_two_concurrent_requests()
    {
        var enteredTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        using var handler = new AsyncHandler(async (uri, ct) =>
        {
            if (IsList(uri)) return Json(Friends(401));
            var current = Interlocked.Increment(ref active);
            lock (enteredTwo) maximum = Math.Max(maximum, current);
            if (current == 2) enteredTwo.TrySetResult();
            await enteredTwo.Task.WaitAsync(ct);
            await Task.Yield();
            Interlocked.Decrement(ref active);
            return Json(Summaries(Ids(uri)));
        });
        var options = new SteamOptions { ApiKey = Key, MaxConcurrentFriendBatches = 100 };
        using var client = CreateClient(handler, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new SteamFriendsService(client, options, TimeProvider.System).GetFriendsAsync(Id, false, timeout.Token);
        Assert.Equal(401, result.Data!.Friends.Count);
        Assert.True(result.Data.SummariesComplete);
        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task Identical_pending_observers_share_transport_and_one_cancellation_does_not_cancel_another()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(async (uri, ct) =>
        {
            Interlocked.Increment(ref calls);
            if (!IsList(uri)) return Json(Summaries(Ids(uri)));
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Json(Friends(2));
        });
        var options = new SteamOptions { ApiKey = Key };
        using var client = CreateClient(handler, options);
        var service = new SteamFriendsService(client, options, TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var first = service.GetFriendsAsync(Id, false, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.GetFriendsAsync(Id, false, default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        Assert.True((await second).Data!.SummariesComplete);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Final_observer_cancellation_cancels_transport_and_cannot_cache_a_late_success()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(async (uri, ct) =>
        {
            var number = Interlocked.Increment(ref calls);
            if (!IsList(uri)) return Json(Summaries(Ids(uri)));
            if (number != 1) return Json(Friends(1));
            using var registration = ct.Register(() => transportCanceled.TrySetResult());
            entered.TrySetResult();
            await release.Task; // Simulate a transport that completes after cancellation.
            return Json(Friends(1));
        });
        var options = new SteamOptions { ApiKey = Key };
        using var client = CreateClient(handler, options);
        var service = new SteamFriendsService(client, options, TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var first = service.GetFriendsAsync(Id, false, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await transportCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        release.TrySetResult();
        var second = await service.GetFriendsAsync(Id, false, default);
        Assert.False(second.FromCache);
        Assert.True(second.Data!.SummariesComplete);
        Assert.True((await service.GetFriendsAsync(Id, false, default)).FromCache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Earlier_request_cannot_replace_new_forced_result_even_if_refresh_fails(bool failRefresh)
    {
        var steam = new ControlledFriends();
        var service = new SteamFriendsService(steam, new(), TimeProvider.System);
        var old = service.GetFriendsAsync(Id, false, default);
        var fresh = service.GetFriendsAsync(Id, true, default);
        steam.Second.SetResult(failRefresh
            ? Outcome<IReadOnlyList<SteamFriend>>.Failure(DataState.Unavailable, "Unavailable")
            : FriendOutcome(OtherId));
        await fresh;
        steam.First.SetResult(FriendOutcome(Id));
        await old;
        var next = await service.GetFriendsAsync(Id, false, default);
        Assert.Equal(failRefresh ? FriendId(0) : OtherId, next.Data!.Friends[0].SteamId);
        Assert.Equal(!failRefresh, next.FromCache);
        Assert.Equal(failRefresh ? 3 : 2, steam.Calls);
    }

    [Fact]
    public async Task Oversized_friend_response_fails_explicitly_instead_of_silent_truncation()
    {
        using var harness = new Harness(_ => Json("{\"friendslist\":{\"ignored\":\"" + new string('x', 8 * 1024 * 1024) + "\"}}"),
            new() { ApiKey = Key, MaxAttempts = 1 });
        var result = await harness.Client.GetFriendListAsync(Id, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.Null(result.Data);
        Assert.Single(harness.Uris);
    }

    [Fact]
    public async Task Default_di_registers_friends_with_existing_shared_transport_and_no_key_setup_outcome()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGamePulseInfrastructure(new());
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<ISteamApiClient>(), provider.GetRequiredService<ISteamFriendsClient>());
        Assert.Equal(DataState.MissingKey,
            (await provider.GetRequiredService<IFriendsService>().GetFriendsAsync(Id, false, default)).State);
    }

    private static SteamFriendsService Service(Harness harness) => new(harness.Client, harness.Options, harness.Clock);
    private static bool IsList(Uri uri) => uri.AbsolutePath.Contains("GetFriendList", StringComparison.Ordinal);
    private static string Query(Uri uri, string key) => Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&')
        .Single(part => part.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..]);
    private static string[] Ids(Uri uri) => Query(uri, "steamids").Split(',');
    private static string Friends(int count) => JsonSerializer.Serialize(new { friendslist = new {
        friends = Enumerable.Range(0, count).Select(index => new { steamid = FriendId(index), relationship = "friend", friend_since = 1_700_000_000 + index }) } });
    private static string Summaries(IEnumerable<string> ids) => JsonSerializer.Serialize(new { response = new {
        players = ids.Select(id => new { steamid = id, personaname = "Player " + id, personastate = 1,
            avatarfull = "https://avatars.steamstatic.com/synthetic.jpg", gameextrainfo = "Counter-Strike 2" }) } });
    private static Outcome<IReadOnlyList<SteamFriend>> FriendOutcome(string id) =>
        Outcome<IReadOnlyList<SteamFriend>>.Ready([new(id, null, null, DataState.Unavailable)]);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static SteamApiClient CreateClient(HttpMessageHandler handler, SteamOptions options) =>
        new(new(handler), options, NullLogger<SteamApiClient>.Instance, TimeProvider.System, new ImmediateDelay(), new());
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request.RequestUri!, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
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
    private sealed class ControlledFriends : ISteamFriendsClient
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<Outcome<IReadOnlyList<SteamFriend>>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Outcome<IReadOnlyList<SteamFriend>>> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Outcome<IReadOnlyList<SteamFriend>>> GetFriendListAsync(string steamId, CancellationToken cancellationToken) => ++Calls switch
        {
            1 => First.Task,
            2 => Second.Task,
            _ => Task.FromResult(FriendOutcome(FriendId(0)))
        };
        public Task<Outcome<IReadOnlyList<SteamProfile>>> GetPlayerSummariesAsync(IReadOnlyList<string> steamIds,
            CancellationToken cancellationToken) => Task.FromResult(Outcome<IReadOnlyList<SteamProfile>>.Ready(
                steamIds.Select(id => new SteamProfile(id, id, "https://steamcommunity.com/profiles/" + id,
                    null, null, null, null, null, null)).ToArray()));
    }
}

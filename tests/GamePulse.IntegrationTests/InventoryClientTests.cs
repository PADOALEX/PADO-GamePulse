using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using GamePulse.Core;
using GamePulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.IntegrationTests;

public sealed class InventoryClientTests
{
    private static readonly InventoryRequest Request = new("76561198000000001", 730, "2");
    private const string SyntheticKey = "synthetic-inventory-test-key-never-sent";

    [Fact]
    public async Task Pages_are_key_free_fixed_HTTPS_and_join_descriptions_across_pages_by_class_and_instance()
    {
        using var h = new Harness(uri => string.IsNullOrEmpty(Query(uri, "start_assetid"))
            ? Json(Page([Asset("10", "100", "0", "3")], [], 3, true, "10"))
            : Json(Page([Asset("11", "100", "1", "2"), Asset("12", "100", "0", "4")],
                [Description("100", "0", "First"), Description("100", "1", "Second")], 3)));
        var progress = new ProgressCapture();
        var result = await Service(h).GetInventoryAsync(Request, false, progress, default);
        Assert.Equal(DataState.Ready, result.State);
        Assert.True(result.Data!.IsComplete);
        Assert.Null(result.Data.InterruptionState);
        Assert.Equal(3, result.Data.ReportedAssetCount);
        Assert.Equal(2, result.Data.PagesLoaded);
        Assert.Equal(["First", "Second", "First"], result.Data.Items.Select(x => x.Name));
        Assert.Equal([3UL, 2UL, 4UL], result.Data.Items.Select(x => x.Amount));
        Assert.All(result.Data.Items, x => Assert.True(x.HasDescription));
        Assert.Equal([new InventoryProgress(1, 1, 3), new(2, 3, 3)], progress.Values.ToArray());
        Assert.Equal(2, h.Requests.Count);
        Assert.All(h.Requests, request =>
        {
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("steamcommunity.com", request.Uri.Host);
            Assert.Equal("/inventory/76561198000000001/730/2/", request.Uri.AbsolutePath);
            Assert.Equal("english", Query(request.Uri, "l"));
            Assert.Equal("2000", Query(request.Uri, "count"));
            Assert.DoesNotContain("key", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SyntheticKey, request.Uri.ToString());
            Assert.Null(request.Authorization);
            Assert.Null(request.Cookie);
        });
        Assert.Equal("10", Query(h.Requests.Last().Uri, "start_assetid"));
    }

    [Theory]
    [InlineData("{\"success\":1,\"total_inventory_count\":0}", DataState.Empty)]
    [InlineData("{\"success\":1,\"total_inventory_count\":0,\"assets\":[]}", DataState.Empty)]
    [InlineData("{\"success\":1,\"assets\":[]}", DataState.Unavailable)]
    [InlineData("{\"success\":1}", DataState.Failed)]
    [InlineData("{}", DataState.Unavailable)]
    [InlineData("{\"success\":0,\"error\":\"This inventory is private.\"}", DataState.Unavailable)]
    [InlineData("{\"success\":false}", DataState.Unavailable)]
    [InlineData("null", DataState.Failed)]
    [InlineData("{broken", DataState.Failed)]
    public async Task Explicit_empty_inventory_is_distinct_from_missing_or_denied_data(string json, DataState expected)
    {
        using var h = new Harness(_ => Json(json));
        var service = Service(h);
        var result = await service.GetInventoryAsync(Request, false, null, default);
        Assert.Equal(expected, result.State);
        if (expected == DataState.Empty)
        {
            Assert.True(result.Data!.IsComplete);
            Assert.Empty(result.Data.Items);
            Assert.True((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
            Assert.Single(h.Requests);
        }
        else Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("assetid", "0")]
    [InlineData("assetid", "01")]
    [InlineData("assetid", "-1")]
    [InlineData("assetid", "18446744073709551616")]
    [InlineData("classid", "0")]
    [InlineData("instanceid", "-1")]
    [InlineData("amount", "0")]
    [InlineData("amount", "no")]
    [InlineData("amount", "18446744073709551616")]
    [InlineData("contextid", "6")]
    [InlineData("contextid", "../2")]
    public async Task Malformed_asset_ids_amounts_or_mismatched_context_fail_without_inventing_rows(string field, string value)
    {
        var asset = Asset("10");
        asset[field] = value;
        using var h = new Harness(_ => Json(Page([asset], [], 1)));
        Assert.Equal(DataState.Failed, (await Service(h).GetInventoryAsync(Request, false, null, default)).State);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"appid\":\"730\"}")]
    [InlineData("{\"appid\":440}")]
    public async Task Invalid_app_or_asset_shape_is_rejected(string asset)
    {
        using var h = new Harness(_ => Json("{\"success\":1,\"total_inventory_count\":1,\"assets\":[" + asset + "]}"));
        Assert.Equal(DataState.Failed, (await h.Client.GetPageAsync(Request, null, default)).State);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"1\"")]
    [InlineData("{}")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_total_is_a_typed_failure(string total)
    {
        using var h = new Harness(_ => Json("{\"success\":1,\"total_inventory_count\":" + total + ",\"assets\":[]}"));
        Assert.Equal(DataState.Failed, (await h.Client.GetPageAsync(Request, null, default)).State);
    }

    [Fact]
    public async Task Final_page_may_omit_more_items_but_missing_total_never_establishes_global_completeness()
    {
        var json = JsonNode.Parse(Page([Asset("10")], [], 1))!;
        json.AsObject().Remove("more_items");
        using var h = new Harness(_ => Json(json.ToJsonString()));
        Assert.True((await Service(h).GetInventoryAsync(Request, false, null, default)).Data!.IsComplete);
        json.AsObject().Remove("total_inventory_count");
        var service = Service(h);
        var result = await service.GetInventoryAsync(Request, false, null, default);
        Assert.Equal(DataState.Ready, result.State);
        Assert.False(result.Data!.IsComplete);
        Assert.Null(result.Data.ReportedAssetCount);
        Assert.Equal(DataState.Unavailable, result.Data.InterruptionState);
        Assert.Equal(InventoryStopReason.MissingTotal, result.Data.StopReason);
        Assert.Single(result.Data.Items);
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("1", "0")]
    public async Task Success_and_pagination_flags_accept_actual_boolean_or_zero_one_values(string yes, string no)
    {
        using var h = new Harness(_ => Json(Page([Asset("10")], [], 1)
            .Replace("\"success\":1", "\"success\":" + yes).Replace("\"more_items\":0", "\"more_items\":" + no)));
        Assert.True((await Service(h).GetInventoryAsync(Request, false, null, default)).Data!.IsComplete);
    }

    [Theory]
    [InlineData("\"0\"")]
    [InlineData("null")]
    [InlineData("2")]
    [InlineData("{}")]
    public async Task Invalid_more_items_flag_fails_instead_of_ending_pagination(string flag)
    {
        using var h = new Harness(_ => Json(Page([Asset("10")], [], 1).Replace("\"more_items\":0", "\"more_items\":" + flag)));
        Assert.Equal(DataState.Failed, (await Service(h).GetInventoryAsync(Request, false, null, default)).State);
    }

    [Fact]
    public async Task Missing_descriptions_preserve_amount_and_unknown_metadata_and_maximum_ulong_quantity()
    {
        using var h = new Harness(_ => Json(Page([Asset("10", amount: ulong.MaxValue.ToString())], [], 1)));
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.True(result.Data!.IsComplete);
        var item = Assert.Single(result.Data.Items);
        Assert.Equal(ulong.MaxValue, item.Amount);
        Assert.False(item.HasDescription);
        Assert.Null(item.Name); Assert.Null(item.Type); Assert.Null(item.IconUrl);
        Assert.Null(item.Marketable); Assert.Null(item.Tradable);
        Assert.NotNull(result.Message);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("2", null)]
    [InlineData("\"1\"", null)]
    public async Task Trade_facts_remain_nullable_instead_of_assuming_false(string flag, bool? expected)
    {
        var description = JsonNode.Parse(JsonSerializer.Serialize(Description()))!;
        description["marketable"] = JsonNode.Parse(flag);
        description["tradable"] = JsonNode.Parse(flag);
        using var h = new Harness(_ => Json(Page([Asset("10")], [description], 1)));
        var item = Assert.Single((await Service(h).GetInventoryAsync(Request, false, null, default)).Data!.Items);
        Assert.Equal(expected, item.Marketable); Assert.Equal(expected, item.Tradable);
    }

    [Theory]
    [InlineData("opaque_IMAGE-123+/part=", true)]
    [InlineData("https://untrusted.invalid/image", false)]
    [InlineData("https://community.fastly.steamstatic.com/economy/image/test", false)]
    [InlineData("//untrusted.invalid/image", false)]
    [InlineData("../image", false)]
    [InlineData("a?key=secret", false)]
    [InlineData("a#fragment", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public async Task Icons_use_only_opaque_safe_paths_and_fixed_HTTPS_CDN(string icon, bool accepted)
    {
        var description = Description(); description["icon_url"] = icon;
        using var h = new Harness(_ => Json(Page([Asset("10")], [description], 1)));
        var item = Assert.Single((await Service(h).GetInventoryAsync(Request, false, null, default)).Data!.Items);
        Assert.True(item.HasDescription);
        if (accepted) Assert.Equal("https://community.fastly.steamstatic.com/economy/image/" + icon + "/256fx256f", item.IconUrl);
        else Assert.Null(item.IconUrl);
    }

    [Fact]
    public async Task Conflicting_description_for_same_class_instance_becomes_unknown_not_last_writer_wins()
    {
        using var h = new Harness(uri => Query(uri, "start_assetid") is null
            ? Json(Page([Asset("10")], [Description(name: "Original")], 2, true, "10"))
            : Json(Page([Asset("11")], [Description(name: "Conflicting")], 2)));
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.True(result.Data!.IsComplete);
        Assert.Equal(2, result.Data.Items.Count);
        Assert.All(result.Data.Items, item => { Assert.False(item.HasDescription); Assert.Null(item.Name); Assert.Null(item.Marketable); });
        Assert.NotNull(result.Message);
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("overlap")]
    [InlineData("changed-total")]
    [InlineData("early-end")]
    public async Task Broken_pagination_retains_prior_rows_but_never_claims_complete_totals(string failure)
    {
        using var h = new Harness(uri => Query(uri, "start_assetid") is null
            ? Json(Page([Asset("10")], [], 3, true, "10"))
            : Json(failure switch
            {
                "loop" => Page([Asset("11")], [], 3, true, "10"),
                "overlap" => Page([Asset("10")], [], 3),
                "changed-total" => Page([Asset("11")], [], 4),
                _ => Page([Asset("11")], [], 3)
            }));
        var service = Service(h);
        var result = await service.GetInventoryAsync(Request, false, null, default);
        Assert.Equal(DataState.Ready, result.State);
        Assert.False(result.Data!.IsComplete);
        Assert.Equal(DataState.Failed, result.Data.InterruptionState);
        Assert.Equal(failure == "changed-total" ? InventoryStopReason.SourceChanged : InventoryStopReason.InvalidPagination, result.Data.StopReason);
        Assert.InRange(result.Data.Items.Count, 1, 2);
        Assert.Equal(result.Data.Items.Count, result.Data.Items.Select(x => x.AssetId).Distinct().Count());
        Assert.NotNull(result.Message);
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
    }

    [Fact]
    public async Task Duplicate_asset_within_first_page_fails_without_double_counting()
    {
        using var h = new Harness(_ => Json(Page([Asset("10"), Asset("10")], [], 2)));
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.Equal(DataState.Failed, result.State); Assert.Null(result.Data);
    }

    [Theory]
    [InlineData(401, DataState.Unavailable, 1)]
    [InlineData(403, DataState.Unavailable, 1)]
    [InlineData(404, DataState.NotFound, 1)]
    [InlineData(429, DataState.RateLimited, 3)]
    [InlineData(500, DataState.Failed, 3)]
    [InlineData(408, DataState.Failed, 3)]
    [InlineData(302, DataState.Failed, 1)]
    public async Task Http_failures_are_typed_and_retries_bounded_without_reading_raw_errors(int status, DataState expected, int attempts)
    {
        using var h = new Harness(_ => new((HttpStatusCode)status) { Content = new StringContent(SyntheticKey) });
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.Equal(expected, result.State); Assert.Null(result.Data);
        Assert.Equal(attempts, h.Requests.Count);
        Assert.Equal(attempts - 1, h.Delay.Values.Count);
        Assert.DoesNotContain(SyntheticKey, result.Message ?? "");
    }

    [Theory]
    [InlineData(403, DataState.Unavailable)]
    [InlineData(429, DataState.RateLimited)]
    [InlineData(500, DataState.Failed)]
    public async Task Later_page_error_preserves_collected_assets_and_its_access_reason(int status, DataState expected)
    {
        using var h = new Harness(uri => Query(uri, "start_assetid") is null
            ? Json(Page([Asset("10")], [], 2, true, "10")) : new((HttpStatusCode)status));
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.Equal(DataState.Ready, result.State); Assert.False(result.Data!.IsComplete);
        Assert.Equal(expected, result.Data.InterruptionState); Assert.Single(result.Data.Items);
        Assert.Null(result.Data.StopReason);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(60, 1)]
    public async Task Retry_after_is_honored_or_long_wait_is_stopped_without_retrying_early(int seconds, int attempts)
    {
        using var h = new Harness(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)); return response;
        });
        Assert.Equal(DataState.RateLimited, (await Service(h).GetInventoryAsync(Request, false, null, default)).State);
        Assert.Equal(attempts, h.Requests.Count);
        Assert.All(h.Delay.Values, value => Assert.True(value >= TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001")]
    [InlineData("76561198000000001 ")]
    public async Task Invalid_steam_id_never_uses_network(string id)
    {
        using var h = new Harness(_ => throw new InvalidOperationException());
        Assert.Equal(DataState.InvalidInput, (await Service(h).GetInventoryAsync(Request with { SteamId = id }, false, null, default)).State);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("02")]
    [InlineData("../2")]
    [InlineData("2?key=secret")]
    [InlineData("-2")]
    [InlineData("18446744073709551616")]
    public async Task Invalid_context_never_uses_network(string context)
    {
        using var h = new Harness(_ => throw new InvalidOperationException());
        Assert.Equal(DataState.InvalidInput, (await Service(h).GetInventoryAsync(Request with { ContextId = context }, false, null, default)).State);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Invalid_app_and_cursor_never_use_network()
    {
        using var h = new Harness(_ => throw new InvalidOperationException());
        Assert.Equal(DataState.InvalidInput, (await h.Client.GetPageAsync(Request with { AppId = 0 }, null, default)).State);
        Assert.Equal(DataState.InvalidInput, (await h.Client.GetPageAsync(Request, "10&key=x", default)).State);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("bytes")]
    [InlineData("pages")]
    public async Task Resource_safety_limits_are_explicit_partial_results_and_not_hidden_row_truncation(string budget)
    {
        var firstPage = Page([Asset("10")], [], 2, true, "10");
        var secondPage = Page([Asset("11")], [], 2);
        var options = new SteamOptions
        {
            MaxInventoryAssets = budget == "rows" ? 1 : 100,
            // Accept exactly the first response, then require the second page to exceed the wire budget.
            MaxInventoryResponseBytes = budget == "bytes" ? System.Text.Encoding.UTF8.GetByteCount(firstPage) : 1024 * 1024,
            MaxInventoryPages = budget == "pages" ? 1 : 100
        };
        using var h = new Harness(uri => Query(uri, "start_assetid") is null
            ? Json(firstPage) : Json(secondPage), options);
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.Equal(DataState.Ready, result.State); Assert.False(result.Data!.IsComplete);
        Assert.Equal(DataState.Failed, result.Data.InterruptionState); Assert.Single(result.Data.Items);
        Assert.Equal(budget == "pages" ? InventoryStopReason.PageLimit : InventoryStopReason.SizeLimit, result.Data.StopReason);
        Assert.Contains("safety limit", result.Message);
    }

    [Fact]
    public async Task More_than_400_assets_are_preserved_across_every_page()
    {
        using var h = new Harness(uri => Query(uri, "start_assetid") is null
            ? Json(Page(Enumerable.Range(1, 2000).Select(i => Asset(i.ToString())).ToArray(), [], 2501, true, "2000"))
            : Json(Page(Enumerable.Range(2001, 501).Select(i => Asset(i.ToString())).ToArray(), [], 2501)));
        var result = await Service(h).GetInventoryAsync(Request, false, null, default);
        Assert.True(result.Data!.IsComplete); Assert.Equal(2501, result.Data.Items.Count);
        Assert.Equal("2501", result.Data.Items[^1].AssetId); Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Oversized_wire_response_fails_explicitly_without_parsing_unbounded_body()
    {
        using var h = new Harness(_ => Json("{\"ignored\":\"" + new string('x', 8 * 1024 * 1024) + "\"}"), new() { MaxAttempts = 1 });
        Assert.Equal(DataState.Failed, (await Service(h).GetInventoryAsync(Request, false, null, default)).State);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Cache_hit_expiry_and_force_refresh_use_complete_values_and_report_cached_progress()
    {
        using var h = new Harness(_ => Json(Page([Asset("10")], [], 1)));
        var service = Service(h);
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
        var progress = new ProgressCapture();
        Assert.True((await service.GetInventoryAsync(Request, false, progress, default)).FromCache);
        Assert.Equal(new InventoryProgress(1, 1, 1), Assert.Single(progress.Values));
        Assert.Single(h.Requests);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
        Assert.False((await service.GetInventoryAsync(Request, true, null, default)).FromCache);
        Assert.Equal(3, h.Requests.Count);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("bytes")]
    [InlineData("disabled")]
    public async Task Cache_budget_does_not_trim_or_cache_oversized_complete_snapshots(string budget)
    {
        using var h = new Harness(_ => Json(Page([Asset("10"), Asset("11")], [], 2)), new()
        {
            MaxInventoryCacheRows = budget == "rows" ? 1 : 100,
            MaxInventoryCacheBytes = budget == "bytes" ? 1 : 1024 * 1024,
            InventoryTtl = budget == "disabled" ? TimeSpan.Zero : TimeSpan.FromMinutes(2)
        });
        var service = Service(h);
        Assert.Equal(2, (await service.GetInventoryAsync(Request, false, null, default)).Data!.Items.Count);
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Cache_entry_budget_evicts_oldest_without_removing_live_result_rows()
    {
        using var h = new Harness(_ => Json(Page([Asset("10")], [], 1)), new() { MaxCacheEntries = 1 });
        var service = Service(h);
        await service.GetInventoryAsync(Request, false, null, default);
        await service.GetInventoryAsync(Request with { SteamId = "76561198000000002" }, false, null, default);
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task Shared_observers_receive_progress_and_one_cancellation_does_not_cancel_the_other()
    {
        var steam = new ControlledClient();
        var service = new SteamInventoryService(steam, new(), TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var firstProgress = new ProgressCapture(); var secondProgress = new ProgressCapture();
        var first = service.GetInventoryAsync(Request, false, firstProgress, cancel.Token);
        var second = service.GetInventoryAsync(Request, false, secondProgress, default);
        Assert.Equal(1, steam.Calls);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(steam.Token.IsCancellationRequested);
        steam.First.SetResult(PageOutcome("10"));
        Assert.True((await second).Data!.IsComplete);
        Assert.Empty(firstProgress.Values);
        Assert.Single(secondProgress.Values);
        Assert.True((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
    }

    [Fact]
    public async Task Last_observer_cancels_shared_work_and_late_result_cannot_populate_cache()
    {
        var steam = new ControlledClient();
        var service = new SteamInventoryService(steam, new(), TimeProvider.System);
        using var cancel = new CancellationTokenSource();
        var first = service.GetInventoryAsync(Request, false, null, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(steam.Token.IsCancellationRequested);
        steam.First.TrySetResult(PageOutcome("10"));
        var next = service.GetInventoryAsync(Request, false, null, default);
        steam.Second.TrySetResult(PageOutcome("20"));
        Assert.False((await next).FromCache); Assert.Equal(2, steam.Calls);
        Assert.Equal("20", Assert.Single((await service.GetInventoryAsync(Request, false, null, default)).Data!.Items).AssetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_inflight_result_cannot_replace_forced_refresh_even_if_refresh_fails(bool fail)
    {
        var steam = new ControlledClient();
        var service = new SteamInventoryService(steam, new(), TimeProvider.System);
        var old = service.GetInventoryAsync(Request, false, null, default);
        var fresh = service.GetInventoryAsync(Request, true, null, default);
        steam.Second.SetResult(fail ? Outcome<SteamInventoryPage>.Failure(DataState.Unavailable, "Unavailable") : PageOutcome("20"));
        await fresh;
        steam.First.SetResult(PageOutcome("10")); await old;
        var next = await service.GetInventoryAsync(Request, false, null, default);
        Assert.Equal(fail ? "30" : "20", Assert.Single(next.Data!.Items).AssetId);
        Assert.Equal(!fail, next.FromCache); Assert.Equal(fail ? 3 : 2, steam.Calls);
    }

    [Fact]
    public async Task Throwing_progress_observer_does_not_corrupt_shared_data()
    {
        using var h = new Harness(_ => Json(Page([Asset("10")], [], 1)));
        Assert.True((await Service(h).GetInventoryAsync(Request, false, new ThrowingProgress(), default)).Data!.IsComplete);
    }

    [Fact]
    public async Task Late_joiner_receives_current_progress_and_cancelled_observer_receives_no_more_pages()
    {
        var release = new TaskCompletionSource<Outcome<SteamInventoryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var steam = new DelegateClient((_, cursor, _) =>
        {
            Interlocked.Increment(ref calls);
            if (cursor is null) return Task.FromResult(Outcome<SteamInventoryPage>.Ready(
                new([new("10", "100", "0", 1)], [], 2, true, "10", 100)));
            entered.TrySetResult(); return release.Task;
        });
        var service = new SteamInventoryService(steam, new(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var firstProgress = new ProgressCapture(); var secondProgress = new ProgressCapture();
        var first = service.GetInventoryAsync(Request, false, firstProgress, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.GetInventoryAsync(Request, false, secondProgress, default);
        Assert.Equal(new InventoryProgress(1, 1, 2), Assert.Single(secondProgress.Values));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(Outcome<SteamInventoryPage>.Ready(new([new("11", "100", "0", 1)], [], 2, false, null, 100)));
        Assert.True((await second).Data!.IsComplete);
        Assert.Equal(2, calls);
        Assert.Single(firstProgress.Values); Assert.Equal(2, secondProgress.Values.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public async Task Local_inventory_concurrency_is_bounded_to_configured_one_or_two(int configured)
    {
        var expected = Math.Clamp(configured, 1, 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var maximum = 0; var calls = 0;
        using var handler = new AsyncHandler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            Interlocked.Increment(ref calls);
            maximum = Math.Max(maximum, current);
            if (current == expected) entered.TrySetResult();
            try { await release.Task.WaitAsync(token); return Json(Page([Asset("10")], [], 1)); }
            finally { Interlocked.Decrement(ref active); }
        });
        using var client = new SteamInventoryClient(new(handler), new() { MaxConcurrentInventoryRequests = configured },
            NullLogger<SteamInventoryClient>.Instance, TimeProvider.System, new ImmediateDelay(), new());
        var work = Enumerable.Range(0, 3).Select(_ => client.GetPageAsync(Request, null, default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(expected, calls);
        release.SetResult();
        Assert.All(await Task.WhenAll(work), result => Assert.True(result.IsReady));
        Assert.Equal(expected, maximum); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Failed_force_refresh_invalidates_a_previous_complete_cache_value()
    {
        var calls = 0;
        using var h = new Harness(_ => ++calls == 2 ? new(HttpStatusCode.Forbidden) : Json(Page([Asset("10")], [], 1)));
        var service = Service(h);
        await service.GetInventoryAsync(Request, false, null, default);
        Assert.True((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
        Assert.Equal(DataState.Unavailable, (await service.GetInventoryAsync(Request, true, null, default)).State);
        Assert.False((await service.GetInventoryAsync(Request, false, null, default)).FromCache);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Missing_api_key_does_not_prevent_this_separate_public_inventory_endpoint()
    {
        using var h = new Harness(_ => Json(Page([Asset("10")], [], 1)), new());
        Assert.True((await Service(h).GetInventoryAsync(Request, false, null, default)).Data!.IsComplete);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Precancelled_inventory_request_never_starts_transport()
    {
        using var h = new Harness(_ => throw new InvalidOperationException());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(h).GetInventoryAsync(Request, false, null, cancellation.Token));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Date_retry_after_uses_injected_clock_and_is_not_retried_earlier()
    {
        var date = DateTimeOffset.Parse("2026-10-17T10:00:04Z");
        using var h = new Harness(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date); return response;
        });
        Assert.Equal(DataState.RateLimited, (await h.Client.GetPageAsync(Request, null, default)).State);
        Assert.Equal(3, h.Requests.Count);
        Assert.All(h.Delay.Values, value => Assert.True(value >= TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Timeout_retries_are_bounded_and_explicit_cancellation_is_not_retried()
    {
        var calls = 0;
        using var handler = new AsyncHandler(async (_, token) => { Interlocked.Increment(ref calls); await Task.Delay(Timeout.Infinite, token); return Json(""); });
        using var client = new SteamInventoryClient(new(handler), new() { RequestTimeout = TimeSpan.FromMilliseconds(5) },
            NullLogger<SteamInventoryClient>.Instance, TimeProvider.System, new ImmediateDelay(), new());
        Assert.Equal(DataState.Failed, (await client.GetPageAsync(Request, null, default)).State);
        Assert.Equal(3, calls);
        using var cancellation = new CancellationTokenSource();
        var work = client.GetPageAsync(Request, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Transport_exception_and_retry_logs_never_expose_sensitive_values()
    {
        using var handler = new AsyncHandler((_, _) => throw new HttpRequestException("https://untrusted.invalid?key=" + SyntheticKey));
        var logger = new CaptureLogger();
        using var client = new SteamInventoryClient(new(handler), new() { ApiKey = SyntheticKey }, logger,
            TimeProvider.System, new ImmediateDelay(), new());
        var result = await client.GetPageAsync(Request, null, default);
        Assert.Equal(DataState.Failed, result.State);
        Assert.DoesNotContain(SyntheticKey, result.Message ?? "");
        Assert.All(logger.Messages, value => { Assert.DoesNotContain(SyntheticKey, value); Assert.DoesNotContain("key=", value); });
    }

    [Fact]
    public void Di_registers_separate_key_free_inventory_transport_and_service_without_requiring_key()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddGamePulseInfrastructure(new());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SteamInventoryService>(provider.GetRequiredService<IInventoryService>());
        Assert.IsType<SteamInventoryClient>(provider.GetRequiredService<ISteamInventoryClient>());
        Assert.NotSame(provider.GetRequiredService<ISteamApiClient>(), provider.GetRequiredService<ISteamInventoryClient>());
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("SteamInventory");
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.DoesNotContain(client.DefaultRequestHeaders.GetValues("User-Agent"), x => x.Contains("key="));
    }

    private static SteamInventoryService Service(Harness h) => new(h.Client, h.Options, h.Clock);
    private static Dictionary<string, object?> Asset(string id, string classId = "100", string instance = "0", string amount = "1") => new()
    { ["appid"] = 730, ["contextid"] = "2", ["assetid"] = id, ["classid"] = classId, ["instanceid"] = instance, ["amount"] = amount };
    private static Dictionary<string, object?> Description(string classId = "100", string instance = "0", string name = "Test item") => new()
    { ["appid"] = 730, ["classid"] = classId, ["instanceid"] = instance, ["name"] = name, ["type"] = "Test type", ["icon_url"] = "opaque_TEST-path", ["marketable"] = 1, ["tradable"] = 0 };
    private static string Page(object[] assets, object[] descriptions, long total, bool more = false, string? cursor = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    { ["success"] = 1, ["assets"] = assets, ["descriptions"] = descriptions, ["total_inventory_count"] = total, ["more_items"] = more ? 1 : 0, ["last_assetid"] = cursor });
    private static Outcome<SteamInventoryPage> PageOutcome(string id) => Outcome<SteamInventoryPage>.Ready(new([new(id, "100", "0", 1)], [], 1, false, null, 100));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static string? Query(Uri uri, string key) => uri.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
    private sealed record CapturedRequest(Uri Uri, string? Authorization, string? Cookie);
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : null));
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class Harness : IDisposable
    {
        private readonly Handler handler;
        public SteamOptions Options { get; }
        public SteamInventoryClient Client { get; }
        public Clock Clock { get; } = new();
        public ImmediateDelay Delay { get; } = new();
        public ConcurrentQueue<CapturedRequest> Requests => handler.Requests;
        public Harness(Func<Uri, HttpResponseMessage> respond, SteamOptions? options = null)
        {
            handler = new(respond); Options = options ?? new() { ApiKey = SyntheticKey };
            Client = new(new(handler), Options, NullLogger<SteamInventoryClient>.Instance, Clock, Delay, new());
        }
        public void Dispose() => Client.Dispose();
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    private sealed class ImmediateDelay : IRetryDelay
    {
        public ConcurrentQueue<TimeSpan> Values { get; } = new();
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Values.Enqueue(delay); return Task.CompletedTask; }
    }
    private sealed class ProgressCapture : IProgress<InventoryProgress>
    {
        public ConcurrentQueue<InventoryProgress> Values { get; } = new();
        public void Report(InventoryProgress value) => Values.Enqueue(value);
    }
    private sealed class ThrowingProgress : IProgress<InventoryProgress>
    {
        public void Report(InventoryProgress value) => throw new InvalidOperationException("Synthetic observer failure");
    }
    private sealed class ControlledClient : ISteamInventoryClient
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<Outcome<SteamInventoryPage>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Outcome<SteamInventoryPage>> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Outcome<SteamInventoryPage>> GetPageAsync(InventoryRequest request, string? cursor, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return ++Calls switch { 1 => First.Task, 2 => Second.Task, _ => Task.FromResult(PageOutcome("30")) };
        }
    }
    private sealed class DelegateClient(Func<InventoryRequest, string?, CancellationToken, Task<Outcome<SteamInventoryPage>>> get) : ISteamInventoryClient
    {
        public Task<Outcome<SteamInventoryPage>> GetPageAsync(InventoryRequest request, string? cursor, CancellationToken cancellationToken) =>
            get(request, cursor, cancellationToken);
    }
    private sealed class CaptureLogger : ILogger<SteamInventoryClient>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}

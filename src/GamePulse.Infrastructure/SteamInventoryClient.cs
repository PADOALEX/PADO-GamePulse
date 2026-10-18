using System.Globalization;
using System.Net;
using System.Text.Json;
using GamePulse.Core;
using Microsoft.Extensions.Logging;

namespace GamePulse.Infrastructure;

public sealed record SteamInventoryAsset(string AssetId, string ClassId, string InstanceId, ulong Amount);
public sealed record SteamInventoryDescription(string ClassId, string InstanceId, string? Name, string? Type,
    string? IconUrl, bool? Marketable, bool? Tradable);
public sealed record SteamInventoryPage(IReadOnlyList<SteamInventoryAsset> Assets,
    IReadOnlyList<SteamInventoryDescription> Descriptions, long? ReportedAssetCount, bool MoreItems,
    string? LastAssetId, long ResponseBytes);
public interface ISteamInventoryClient
{
    Task<Outcome<SteamInventoryPage>> GetPageAsync(InventoryRequest request, string? startAssetId,
        CancellationToken cancellationToken);
}

/// <summary>Best-effort public Community inventory reader. It never sends the Web API key or cookies.</summary>
public sealed class SteamInventoryClient : ISteamInventoryClient, IDisposable
{
    static SteamInventoryClient() => SteamHttpPrivacy.ConfigureBeforeHttpClients();
    private readonly HttpClient http;
    private readonly SteamOptions options;
    private readonly ILogger<SteamInventoryClient> logger;
    private readonly TimeProvider clock;
    private readonly IRetryDelay delay;
    private readonly SteamRequestMetrics metrics;
    private readonly SemaphoreSlim gate;
    private int disposed;

    public SteamInventoryClient(HttpClient http, SteamOptions options, ILogger<SteamInventoryClient> logger,
        TimeProvider clock, IRetryDelay delay, SteamRequestMetrics metrics)
    {
        this.http = http; this.options = options; this.logger = logger; this.clock = clock;
        this.delay = delay; this.metrics = metrics;
        gate = new(Math.Clamp(options.MaxConcurrentInventoryRequests, 1, 2));
    }

    public async Task<Outcome<SteamInventoryPage>> GetPageAsync(InventoryRequest request, string? startAssetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidRequest(request) || startAssetId is not null && !NumericId(startAssetId, false))
            return Outcome<SteamInventoryPage>.Failure(DataState.InvalidInput,
                "Enter a valid SteamID64, positive AppID and numeric inventory context.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempts = Math.Clamp(options.MaxAttempts, 1, 3);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                TimeSpan? retry = null;
                Outcome<SteamInventoryPage>? failure = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(options.RequestTimeout.TotalMilliseconds, 1, 60_000)));
                try
                {
                    var uri = "https://steamcommunity.com/inventory/" + request.SteamId + "/" +
                        request.AppId.ToString(CultureInfo.InvariantCulture) + "/" + request.ContextId +
                        "/?l=english&count=" + Math.Clamp(options.InventoryPageSize, 1, 2000).ToString(CultureInfo.InvariantCulture) +
                        (startAssetId is null ? "" : "&start_assetid=" + startAssetId);
                    using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                    metrics.RecordRequest();
                    using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        const int maximumPageBytes = 8 * 1024 * 1024;
                        if (response.Content.Headers.ContentLength > maximumPageBytes) return Malformed();
                        await response.Content.LoadIntoBufferAsync(maximumPageBytes, timeout.Token).ConfigureAwait(false);
                        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                        using var json = JsonDocument.Parse(bytes);
                        return Parse(json.RootElement, request, bytes.LongLength);
                    }
                    failure = HttpFailure(response.StatusCode);
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                    {
                        retry = Backoff(attempt);
                        if (response.Headers.RetryAfter is { } advice)
                        {
                            var wait = advice.Delta ?? advice.Date - clock.GetUtcNow();
                            // Do not retry earlier than Steam's advice, or tie up the desktop for an unbounded wait.
                            if (wait > TimeSpan.FromSeconds(30)) return failure;
                            if (wait is { } advised && advised > retry) retry = advised;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    failure = Outcome<SteamInventoryPage>.Failure(DataState.Failed, "The inventory request timed out. Try again later.");
                    retry = Backoff(attempt);
                }
                catch (HttpRequestException)
                {
                    failure = Outcome<SteamInventoryPage>.Failure(DataState.Failed, "Steam inventory could not be reached securely. Try again later.");
                    retry = Backoff(attempt);
                }
                catch (IOException)
                {
                    failure = Outcome<SteamInventoryPage>.Failure(DataState.Failed, "The inventory response was interrupted. Try again later.");
                    retry = Backoff(attempt);
                }
                catch (JsonException) { return Malformed(); }
                if (retry is null || attempt == attempts) return failure!;
                metrics.RecordRetry();
                logger.LogDebug("Retrying Community inventory request. Attempt {Attempt} of {MaximumAttempts}.", attempt + 1, attempts);
                await delay.WaitAsync(retry.Value, cancellationToken).ConfigureAwait(false);
            }
            return Malformed();
        }
        finally { gate.Release(); }
    }

    private Outcome<SteamInventoryPage> Parse(JsonElement root, InventoryRequest request, long responseBytes)
    {
        if (root.ValueKind != JsonValueKind.Object) return Malformed();
        if (!root.TryGetProperty("success", out var success) || Flag(success) != true)
            return Outcome<SteamInventoryPage>.Failure(DataState.Unavailable,
                "Steam did not expose this inventory. It may be private, unavailable or unsupported; the response does not establish which.");
        long? total = null;
        if (root.TryGetProperty("total_inventory_count", out var count) && count.ValueKind != JsonValueKind.Null)
        {
            if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var value) || value < 0) return Malformed();
            total = value;
        }
        var more = false;
        if (root.TryGetProperty("more_items", out var moreValue))
        {
            if (Flag(moreValue) is not { } flag) return Malformed();
            more = flag;
        }
        // Steam's frontend also treats an omitted more_items as the terminal page.
        // The service still requires total_inventory_count to agree before declaring completeness.
        string? cursor = null;
        if (root.TryGetProperty("last_assetid", out var cursorValue))
        {
            if (cursorValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return Malformed();
            cursor = Text(cursorValue);
            if (cursor is not null && !NumericId(cursor, false)) return Malformed();
        }
        if (more && cursor is null) return Malformed();
        var assets = new List<SteamInventoryAsset>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind != JsonValueKind.Null)
        {
            if (assetArray.ValueKind != JsonValueKind.Array) return Malformed();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in assetArray.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("appid", out var app) ||
                    app.ValueKind != JsonValueKind.Number || !app.TryGetUInt32(out var appId) || appId != request.AppId ||
                    !GetId(asset, "contextid", false, out var context) || context != request.ContextId ||
                    !GetId(asset, "assetid", false, out var id) || !ids.Add(id!) ||
                    !GetId(asset, "classid", false, out var classId) || !GetId(asset, "instanceid", true, out var instance) ||
                    !GetId(asset, "amount", false, out var amount)) return Malformed();
                assets.Add(new(id!, classId!, instance!, ulong.Parse(amount!, CultureInfo.InvariantCulture)));
            }
        }
        else if (total != 0) return Malformed();
        if (more && assets.Count == 0 || total == 0 && (assets.Count != 0 || more)) return Malformed();
        var descriptions = new List<SteamInventoryDescription>();
        if (root.TryGetProperty("descriptions", out var descriptionArray) && descriptionArray.ValueKind != JsonValueKind.Null)
        {
            if (descriptionArray.ValueKind != JsonValueKind.Array) return Malformed();
            foreach (var value in descriptionArray.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object || !GetId(value, "classid", false, out var classId) ||
                    !GetId(value, "instanceid", true, out var instance)) return Malformed();
                if (value.TryGetProperty("appid", out var app) && (app.ValueKind != JsonValueKind.Number || !app.TryGetUInt32(out var appId) || appId != request.AppId)) return Malformed();
                descriptions.Add(new(classId!, instance!, OptionalText(value, "name"), OptionalText(value, "type"),
                    SafeIcon(OptionalText(value, "icon_url")), OptionalFlag(value, "marketable"), OptionalFlag(value, "tradable")));
            }
        }
        return Outcome<SteamInventoryPage>.Ready(new(assets.AsReadOnly(), descriptions.AsReadOnly(), total, more, cursor, responseBytes), clock.GetUtcNow());
    }

    internal static bool ValidRequest(InventoryRequest? request) => request is not null && request.AppId > 0 &&
        ProfileInput.Parse(request.SteamId).Data is { Kind: ProfileInputKind.SteamId } parsed && parsed.Value == request.SteamId &&
        NumericId(request.ContextId, false);
    private static bool GetId(JsonElement value, string property, bool zeroAllowed, out string? id)
    {
        id = value.TryGetProperty(property, out var field) ? Text(field) : null;
        return NumericId(id, zeroAllowed);
    }
    internal static bool NumericId(string? value, bool zeroAllowed) => value is { Length: > 0 and <= 20 } &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && (zeroAllowed || number > 0) &&
        number.ToString(CultureInfo.InvariantCulture) == value;
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? OptionalText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) ? Text(field) : null;
    private static bool? OptionalFlag(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) ? Flag(field) : null;
    private static bool? Flag(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var flag) && flag is 0 or 1 => flag == 1,
        _ => null
    };
    private static string? SafeIcon(string? value)
    {
        // The primary Steam frontend constructs this URL from an opaque relative image path.
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.StartsWith('/') ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '=' or '/'))) return null;
        return "https://community.fastly.steamstatic.com/economy/image/" + value + "/256fx256f";
    }
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(150));
    private static Outcome<SteamInventoryPage> Malformed() => Outcome<SteamInventoryPage>.Failure(DataState.Failed,
        "Steam returned an incomplete or invalid inventory response. No empty inventory is inferred.");
    private static Outcome<SteamInventoryPage> HttpFailure(HttpStatusCode code) => Outcome<SteamInventoryPage>.Failure((int)code switch
    {
        401 or 403 => DataState.Unavailable, 404 => DataState.NotFound, 429 => DataState.RateLimited, _ => DataState.Failed
    }, (int)code switch
    {
        401 or 403 => "Steam denied access to this inventory. Privacy or service restrictions may apply; this response alone does not prove it is private.",
        404 => "Steam could not find this inventory or context.",
        429 => "Steam is limiting inventory requests. Wait before refreshing.",
        _ => "Steam inventory request failed. Try again later."
    });
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Dispose(); http.Dispose();
    }
}

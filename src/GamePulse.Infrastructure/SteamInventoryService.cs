using GamePulse.Core;

namespace GamePulse.Infrastructure;

/// <summary>Fetches every cursor page on demand. Interrupted snapshots retain rows and never claim totals.</summary>
public sealed class SteamInventoryService(ISteamInventoryClient steam, SteamOptions options, TimeProvider clock) : IInventoryService
{
    private readonly InventoryOutcomeCache cache = new(options, clock);

    public Task<Outcome<InventorySnapshot>> GetInventoryAsync(InventoryRequest request, bool forceRefresh,
        IProgress<InventoryProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SteamInventoryClient.ValidRequest(request)) return Task.FromResult(Outcome<InventorySnapshot>.Failure(DataState.InvalidInput,
            "Enter a valid SteamID64, positive AppID and numeric inventory context."));
        return cache.GetAsync(request, forceRefresh, progress, cancellationToken, report => ct => LoadAsync(request, report, ct));
    }

    private async Task<Outcome<InventorySnapshot>> LoadAsync(InventoryRequest request, Action<InventoryProgress> report,
        CancellationToken cancellationToken)
    {
        var assets = new List<SteamInventoryAsset>();
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        var descriptions = new Dictionary<(string ClassId, string InstanceId), SteamInventoryDescription>();
        var conflictingDescriptions = new HashSet<(string ClassId, string InstanceId)>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        long? total = null;
        var pages = 0;
        long responseBytes = 0;
        var maxRows = Math.Clamp(options.MaxInventoryAssets, 1, 1_000_000);
        var maxBytes = Math.Clamp(options.MaxInventoryResponseBytes, 1, 1024L * 1024 * 1024);
        var maxPages = Math.Clamp(options.MaxInventoryPages, 1, 10_000);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pages >= maxPages) return Interrupted(DataState.Failed,
                "The configured inventory page safety limit was reached. The retained rows are incomplete; increase the limit only when appropriate.", InventoryStopReason.PageLimit);
            var outcome = await steam.GetPageAsync(request, cursor, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!outcome.IsReady) return Interrupted(outcome.State, outcome.Message ?? "Inventory loading was interrupted.");
            var page = outcome.Data!;
            if (page.ReportedAssetCount is { } reported)
            {
                if (total is { } known && known != reported) return Interrupted(DataState.Failed,
                    "Steam's inventory total changed between pages. Retained rows are incomplete; refresh after inventory changes settle.", InventoryStopReason.SourceChanged);
                total = reported;
            }
            if (page.Assets.Count > maxRows - assets.Count || page.ResponseBytes > maxBytes - responseBytes)
                return Interrupted(DataState.Failed,
                    "The configured inventory size safety limit was reached. Retained rows are incomplete; no global total is inferred.", InventoryStopReason.SizeLimit);
            if (page.Assets.Select(x => x.AssetId).Distinct(StringComparer.Ordinal).Count() != page.Assets.Count ||
                page.Assets.Any(x => assetIds.Contains(x.AssetId)))
                return Interrupted(DataState.Failed,
                    "Steam repeated an asset between inventory pages. Retained rows are incomplete; refresh before interpreting totals.", InventoryStopReason.InvalidPagination);
            if (total is { } count && assets.Count + page.Assets.Count > count)
                return Interrupted(DataState.Failed, "Steam returned more assets than its reported inventory count. Retained rows are incomplete.", InventoryStopReason.SourceChanged);
            assets.AddRange(page.Assets);
            foreach (var asset in page.Assets) assetIds.Add(asset.AssetId);
            responseBytes += page.ResponseBytes;
            pages++;
            foreach (var description in page.Descriptions)
            {
                var key = (description.ClassId, description.InstanceId);
                if (conflictingDescriptions.Contains(key)) continue;
                if (descriptions.TryGetValue(key, out var existing) && existing != description)
                {
                    descriptions.Remove(key);
                    conflictingDescriptions.Add(key); // Ambiguous metadata stays unknown; never silently pick a winner.
                }
                else descriptions[key] = description;
            }
            report(new(pages, assets.Count, total));
            if (!page.MoreItems)
            {
                if (total is null) return Interrupted(DataState.Unavailable,
                    "Steam omitted the inventory total. All returned rows are shown, but completeness and global totals are unavailable.", InventoryStopReason.MissingTotal);
                if (assets.Count != total.Value) return Interrupted(DataState.Failed,
                    "Steam ended pagination before its reported asset count was reached. The retained rows are incomplete.", InventoryStopReason.InvalidPagination);
                var snapshot = Snapshot(true, null);
                var unknown = snapshot.Items.Count(x => !x.HasDescription);
                return new(assets.Count == 0 ? DataState.Empty : DataState.Ready, snapshot,
                    unknown == 0 ? null : "Every returned asset is shown, but some descriptions are missing or conflicting. Their names and trade facts remain unknown.", clock.GetUtcNow());
            }
            if (page.Assets.Count == 0 || page.LastAssetId is null || !cursors.Add(page.LastAssetId))
                return Interrupted(DataState.Failed, "Steam returned an empty or repeated pagination cursor. The retained rows are incomplete.", InventoryStopReason.InvalidPagination);
            cursor = page.LastAssetId;
        }

        InventorySnapshot Snapshot(bool complete, DataState? interruption, InventoryStopReason? reason = null) => new(request,
            assets.Select(asset =>
            {
                descriptions.TryGetValue((asset.ClassId, asset.InstanceId), out var description);
                return new InventoryItem(asset.AssetId, asset.ClassId, asset.InstanceId, asset.Amount,
                    description?.Name, description?.Type, description?.IconUrl,
                    description?.Marketable, description?.Tradable, description is not null);
            }).ToArray(), total, complete, pages, interruption, reason);
        Outcome<InventorySnapshot> Interrupted(DataState state, string message, InventoryStopReason? reason = null) => assets.Count == 0
            ? Outcome<InventorySnapshot>.Failure(state, message)
            : new(DataState.Ready, Snapshot(false, state, reason), message, clock.GetUtcNow());
    }
}

/// <summary>Bounded session cache and shared pagination; progress and cancellation belong to each observer.</summary>
internal sealed class InventoryOutcomeCache(SteamOptions options, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<InventoryRequest, Entry> values = new();
    private readonly Dictionary<InventoryRequest, Pending> pending = new();
    private long sequence;
    private long cachedRows;
    private long cachedBytes;
    private sealed record Entry(Outcome<InventorySnapshot> Value, DateTimeOffset Expires, long Sequence, int Rows, long Bytes);
    private sealed record Observer(IProgress<InventoryProgress>? Progress, CancellationToken Cancellation);
    private sealed class Pending(Task<Outcome<InventorySnapshot>> task)
    {
        public Task<Outcome<InventorySnapshot>> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = new();
        public HashSet<Observer> Observers { get; } = new(ReferenceEqualityComparer.Instance);
        public InventoryProgress? LastProgress { get; set; }
        public bool Completed { get; set; }
        public bool Abandoned { get; set; }
    }

    public async Task<Outcome<InventorySnapshot>> GetAsync(InventoryRequest key, bool forceRefresh,
        IProgress<InventoryProgress>? progress, CancellationToken cancellationToken,
        Func<Action<InventoryProgress>, Func<CancellationToken, Task<Outcome<InventorySnapshot>>>> load)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Pending active;
        var observer = new Observer(progress, cancellationToken);
        TaskCompletionSource<Outcome<InventorySnapshot>>? completion = null;
        Outcome<InventorySnapshot>? cached = null;
        InventoryProgress? prior = null;
        lock (gate)
        {
            if (forceRefresh) Remove(key);
            else if (values.TryGetValue(key, out var hit))
            {
                if (hit.Expires > clock.GetUtcNow()) cached = hit.Value with { FromCache = true };
                else Remove(key);
            }
            if (cached is not null) active = null!;
            else
            {
                if (!forceRefresh && pending.TryGetValue(key, out var existing) && !existing.Abandoned) active = existing;
                else
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    active = new(completion.Task);
                    pending[key] = active;
                }
                active.Observers.Add(observer);
                prior = active.LastProgress;
            }
        }
        if (cached is not null)
        {
            if (cached.Data is { } snapshot) Report(observer, new(snapshot.PagesLoaded, snapshot.Items.Count, snapshot.ReportedAssetCount));
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }
        if (prior is not null) Report(observer, prior);
        if (completion is not null) _ = CompleteAsync(key, active, completion, load(value => Broadcast(active, value)));
        try
        {
            var result = await active.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            var cancel = false;
            lock (gate)
            {
                active.Observers.Remove(observer);
                if (active.Observers.Count == 0)
                {
                    if (active.Completed) active.Cancellation.Dispose();
                    else if (!active.Task.IsCompleted) { active.Abandoned = true; cancel = true; }
                }
            }
            if (cancel) try { active.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void Broadcast(Pending active, InventoryProgress value)
    {
        Observer[] observers;
        lock (gate)
        {
            active.LastProgress = value;
            observers = active.Observers.ToArray();
        }
        foreach (var observer in observers) Report(observer, value);
    }
    private static void Report(Observer observer, InventoryProgress value)
    {
        if (observer.Cancellation.IsCancellationRequested) return;
        try { observer.Progress?.Report(value); }
        catch (Exception) { /* A UI progress observer cannot corrupt shared transport or another observer. */ }
    }

    private async Task CompleteAsync(InventoryRequest key, Pending active,
        TaskCompletionSource<Outcome<InventorySnapshot>> completion, Func<CancellationToken, Task<Outcome<InventorySnapshot>>> load)
    {
        try
        {
            var result = await load(active.Cancellation.Token).ConfigureAwait(false);
            active.Cancellation.Token.ThrowIfCancellationRequested();
            if (result.State is DataState.Ready or DataState.Empty && result.Data is { IsComplete: true } snapshot && options.InventoryTtl > TimeSpan.Zero)
            {
                var rows = snapshot.Items.Count;
                var bytes = 256L + snapshot.Items.Sum(x => 160L + x.AssetId.Length * 2L + x.ClassId.Length * 2L + x.InstanceId.Length * 2L +
                    (x.Name?.Length ?? 0) * 2L + (x.Type?.Length ?? 0) * 2L + (x.IconUrl?.Length ?? 0) * 2L);
                lock (gate)
                {
                    if (!active.Abandoned && pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, active))
                    {
                        foreach (var expired in values.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray()) Remove(expired);
                        var rowBudget = Math.Clamp(options.MaxInventoryCacheRows, 0, 100_000);
                        var byteBudget = Math.Clamp(options.MaxInventoryCacheBytes, 0, 64L * 1024 * 1024);
                        var entryBudget = Math.Clamp(options.MaxCacheEntries, 1, 4096);
                        if (rows <= rowBudget && bytes <= byteBudget)
                        {
                            Remove(key);
                            while (values.Count > 0 && (values.Count >= entryBudget || cachedRows + rows > rowBudget || cachedBytes + bytes > byteBudget))
                                Remove(values.MinBy(x => x.Value.Sequence).Key);
                            values[key] = new(result, clock.GetUtcNow() + options.InventoryTtl, ++sequence, rows, bytes);
                            cachedRows += rows; cachedBytes += bytes;
                        }
                    }
                }
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally
        {
            lock (gate)
            {
                if (pending.TryGetValue(key, out var latest) && ReferenceEquals(latest, active)) pending.Remove(key);
                active.Completed = true;
                if (active.Observers.Count == 0) active.Cancellation.Dispose();
            }
        }
    }
    private void Remove(InventoryRequest key)
    {
        if (!values.Remove(key, out var old)) return;
        cachedRows -= old.Rows; cachedBytes -= old.Bytes;
    }
}

using System.Globalization;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using GamePulse.Core;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;

namespace GamePulse.Desktop.ViewModels;

public sealed record InventoryRow(InventoryItem Item) : LocalizedRecord
{
    public string AssetId => Item.AssetId;
    public string ClassId => Item.ClassId;
    public string InstanceId => Item.InstanceId;
    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? InventoryText.Format("AssetFallback", AssetId) : Item.Name;
    public string Type => string.IsNullOrWhiteSpace(Item.Type) ? InventoryText.Get("UnknownType") : Item.Type;
    public string Amount => Item.Amount.ToString("N0", CultureInfo.CurrentCulture);
    public string Marketable => Flag(Item.Marketable);
    public string Tradable => Flag(Item.Tradable);
    public bool IsMarketable => Item.Marketable == true;
    public bool IsTradable => Item.Tradable == true;
    public string Metadata => InventoryText.Get(Item.HasDescription ? "MetadataAvailable" : "MetadataMissing");
    public string? IconUrl => Item.IconUrl;
    public string AccessibleName => InventoryText.Format("AccessibleAsset", Name, Amount, Type, Marketable, Tradable, AssetId);
    private static string Flag(bool? value) => InventoryText.Get(value switch { true => "Yes", false => "No", _ => "Unknown" });
}

/// <summary>Explicitly loaded, source-isolated inventory with cancellable queries and shared, bounded icons.</summary>
public sealed class InventoryViewModel : ObservableObject, IDisposable
{
    private readonly IInventoryService _service;
    private readonly IInventoryIconLoader _icons;
    private CancellationTokenSource? _loadCancellation, _filterCancellation, _iconCancellation;
    private long _sourceGeneration, _loadGeneration, _filterGeneration, _selectionGeneration;
    private bool _disposed, _hasProfile, _cancelled, _isFiltering, _publishingRows, _isIconBusy;
    private string _steamId = "", _contextId = InventoryCatalog.Apps[0].DefaultContextId, _query = "", _localMessage = "", _filterError = "";
    private int _selectedAppIndex, _selectedSortIndex, _pagesLoaded, _assetsLoaded;
    private long? _reportedAssetCount;
    private bool _marketableOnly, _tradableOnly;
    private DataState _state = DataState.Unavailable;
    private Outcome<InventorySnapshot>? _result;
    private InventoryStatistics? _statistics;
    private IReadOnlyList<InventoryRow> _rows = Array.Empty<InventoryRow>();
    private InventoryRow? _selectedItem;
    private Bitmap? _selectedIcon;

    public InventoryViewModel(IInventoryService service, IInventoryIconLoader icons)
    {
        _service = service; _icons = icons;
        ResetFiltersCommand = new RelayCommand(_ => ResetFilters());
    }

    public IInventoryIconLoader RowThumbnailLoader => _icons;
    public IReadOnlyList<InventoryApp> Apps => InventoryCatalog.Apps;
    public IReadOnlyList<LocalizedText> SortOptions { get; } = [LocalizedResources.For("Inventory").Text("SortName"), LocalizedResources.For("Inventory").Text("SortType"), LocalizedResources.For("Inventory").Text("SortAmount"), LocalizedResources.For("Inventory").Text("SortAssetId")];
    public ICommand ResetFiltersCommand { get; }
    public int SelectedAppIndex
    {
        get => _selectedAppIndex;
        set
        {
            if (value < 0 || value >= Apps.Count || !Set(ref _selectedAppIndex, value)) return;
            _contextId = SelectedApp.DefaultContextId; Notify(nameof(ContextId)); Notify(nameof(SelectedApp));
            ResetSource();
        }
    }
    public InventoryApp SelectedApp => Apps[SelectedAppIndex];
    public string ContextId
    {
        get => _contextId;
        set { if (Set(ref _contextId, value ?? "")) ResetSource(); }
    }
    public string SteamId => _steamId;
    public string Source => _hasProfile ? InventoryText.Format("Source", _steamId, SelectedApp.AppId, ContextId) : "";
    public bool HasProfile => _hasProfile;
    public DataState State => _state;
    public bool IsBusy => State == DataState.Loading;
    public bool CanLoad => _hasProfile && !IsBusy && ValidRequest;
    public bool HasSelection => SelectedItem is not null;
    public bool HasSnapshot => _result is { State: DataState.Ready or DataState.Empty, Data: not null };
    public Outcome<InventorySnapshot>? Snapshot => _result;
    public bool IsComplete => HasSnapshot && _result!.Data!.IsComplete;
    public bool HasCompleteness => HasSnapshot && !IsComplete;
    public bool IsFiltering { get => _isFiltering; private set { if (Set(ref _isFiltering, value)) Notify(nameof(StateMessage)); } }
    public bool HasFilterError => _filterError.Length > 0;
    public IReadOnlyList<InventoryRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public string Query { get => _query; set { if (Set(ref _query, value ?? "")) QueueFilters(); } }
    public int SelectedSortIndex { get => _selectedSortIndex; set { if (Set(ref _selectedSortIndex, value)) QueueFilters(); } }
    public bool MarketableOnly { get => _marketableOnly; set { if (Set(ref _marketableOnly, value)) QueueFilters(); } }
    public bool TradableOnly { get => _tradableOnly; set { if (Set(ref _tradableOnly, value)) QueueFilters(); } }
    public InventoryRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_disposed || _publishingRows || (value is not null && !Rows.Contains(value)) || !Set(ref _selectedItem, value)) return;
            Notify(nameof(HasSelection)); LocalMessage = "";
            IconLoadingTask = LoadSelectedIconAsync(value);
        }
    }
    public Bitmap? SelectedIcon { get => _selectedIcon; private set { if (Set(ref _selectedIcon, value)) { Notify(nameof(HasSelectedIcon)); Notify(nameof(HasNoPreview)); } } }
    public bool HasSelectedIcon => SelectedIcon is not null;
    public bool HasNoPreview => !HasSelectedIcon && !IsIconBusy;
    public bool IsIconBusy { get => _isIconBusy; private set { if (Set(ref _isIconBusy, value)) Notify(nameof(HasNoPreview)); } }
    public Task IconLoadingTask { get; private set; } = Task.CompletedTask;
    public Task FilteringTask { get; private set; } = Task.CompletedTask;
    public string LocalMessage { get => LocalizedStatus.Read(_localMessage, "Inventory"); private set => Set(ref _localMessage, value); }
    public string LoadedAssetCount => _statistics?.AssetCount.ToString("N0", CultureInfo.CurrentCulture) ?? InventoryText.Get("NA");
    public string QuantityCount => _statistics?.LoadedUnits.ToString("N0", CultureInfo.CurrentCulture) ?? InventoryText.Get("NA");
    public string TotalAssetCount => IsComplete ? LoadedAssetCount : InventoryText.Get("NA");
    public string MetadataCoverage => _statistics is { } stats ? InventoryText.Format("Coverage", stats.MetadataCount, stats.AssetCount) : InventoryText.Get("NA");
    public string ShownCount => HasSnapshot ? InventoryText.Format("Shown", Rows.Count, _result!.Data!.Items.Count) : "";
    public string Completeness => HasCompleteness ? InventoryText.Format("Partial", Interruption(_result!.Data!)) : "";
    public string ProgressText => IsBusy ? _reportedAssetCount is { } expected
        ? InventoryText.Format("ProgressReported", _pagesLoaded, _assetsLoaded, expected)
        : InventoryText.Format("Progress", _pagesLoaded, _assetsLoaded) : "";
    public string UpdatedAt => _result?.UpdatedAt is { } date
        ? InventoryText.Format(_result.FromCache ? "Cached" : "Updated", date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";
    public string StateMessage => !_hasProfile ? InventoryText.Get("Idle") : !ValidRequest ? InventoryText.Get("InvalidContext") :
        _cancelled ? InventoryText.Get("Cancelled") : _filterError.Length > 0 ? LocalizedStatus.Read(_filterError, "Inventory") : State switch
        {
            DataState.Loading => InventoryText.Get("Loading"), DataState.Empty => InventoryText.Get("Empty"),
            DataState.Private => InventoryText.Get("Private"), DataState.RateLimited => InventoryText.Get("RateLimited"),
            DataState.Failed => InventoryText.Get("Failed"), DataState.MissingKey => InventoryText.Get("MissingKey"),
            DataState.Ready when IsFiltering => InventoryText.Get("Filtering"),
            DataState.Ready when Rows.Count == 0 && _result?.Data?.Items.Count > 0 => InventoryText.Get("NoMatches"),
            DataState.Ready when HasCompleteness => InventoryText.Get("PartialReady"),
            DataState.Ready => InventoryText.Get("Ready"),
            _ => _result is null ? InventoryText.Get("NotLoaded") : InventoryText.Get("Unavailable")
        };
    private bool ValidRequest => _hasProfile && InventoryRequestValidation.Validate(CurrentRequest).IsReady;
    private InventoryRequest CurrentRequest => new(_steamId, SelectedApp.AppId, ContextId);
    private static string Interruption(InventorySnapshot snapshot) => InventoryText.Get(snapshot.StopReason switch
    {
        InventoryStopReason.PageLimit => "StopPageLimit", InventoryStopReason.SizeLimit => "StopSizeLimit",
        InventoryStopReason.SourceChanged => "StopSourceChanged", InventoryStopReason.InvalidPagination => "StopInvalidPagination",
        InventoryStopReason.MissingTotal => "StopMissingTotal",
        _ => snapshot.InterruptionState switch
        { DataState.RateLimited => "InterruptedRateLimited", DataState.Private => "InterruptedPrivate", DataState.Unavailable => "InterruptedUnavailable", _ => "InterruptedFailed" }
    });

    /// <summary>Changing a profile or inventory source never sends an inventory request.</summary>
    public void SetProfile(string steamId)
    {
        if (_disposed) return;
        var parsed = ProfileInput.Parse(steamId);
        var valid = parsed.IsReady && parsed.Data is { Kind: ProfileInputKind.SteamId } input && input.Value == steamId;
        if (valid && _hasProfile && _steamId == steamId) return;
        _steamId = valid ? steamId : ""; _hasProfile = valid;
        ResetSource();
    }

    public async Task LoadAsync(bool force, CancellationToken token = default)
    {
        if (_disposed || !ValidRequest) { NotifyAll(); return; }
        ResetSource();
        _state = DataState.Loading;
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = _loadCancellation.Token; var source = _sourceGeneration; var load = _loadGeneration;
        var request = CurrentRequest;
        var progress = new Progress<InventoryProgress>(value =>
        {
            if (!Current() || _state != DataState.Loading) return;
            _pagesLoaded = value.PagesLoaded; _assetsLoaded = value.AssetsLoaded; _reportedAssetCount = value.ReportedAssetCount;
            Notify(nameof(ProgressText));
        });
        NotifyAll();
        try
        {
            var result = await _service.GetInventoryAsync(request, force, progress, cancellation).WaitAsync(cancellation);
            if (!Current()) return;
            // A mismatched response must never be published under another visible profile/context.
            if (result.Data is { } snapshot && snapshot.Request != request)
                result = Outcome<InventorySnapshot>.Failure(DataState.Failed, "Inventory source did not match the request.");
            var statistics = result is { State: DataState.Ready or DataState.Empty, Data: not null }
                ? await Task.Run(() => InventoryStatistics.Calculate(result.Data, cancellation), cancellation) : null;
            if (!Current()) return;
            _result = result; _state = result.State;
            _statistics = statistics;
            NotifyAll();
            await ApplyFiltersAsync();
        }
        catch (OperationCanceledException)
        {
            if (StillSame()) { _state = DataState.Unavailable; _cancelled = true; }
        }
        catch (Exception)
        {
            if (Current()) { _state = DataState.Failed; _result = Outcome<InventorySnapshot>.Failure(DataState.Failed, "Inventory request failed."); }
        }
        finally
        {
            if (StillSame())
            {
                if (cancellation.IsCancellationRequested && _state == DataState.Loading)
                { _state = DataState.Unavailable; _cancelled = true; }
                NotifyAll();
            }
        }
        bool StillSame() => !_disposed && source == _sourceGeneration && load == _loadGeneration;
        bool Current() => StillSame() && !cancellation.IsCancellationRequested;
    }

    public void Cancel()
    {
        if (_disposed || !IsBusy) return;
        _loadCancellation?.Cancel(); ++_loadGeneration;
        _state = DataState.Unavailable; _cancelled = true; NotifyAll();
    }

    public Task ApplyFiltersAsync() => FilteringTask = FilterAsync(false);
    private void QueueFilters() => FilteringTask = FilterAsync(true);
    private async Task FilterAsync(bool debounce)
    {
        if (_disposed) return;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _filterCancellation = new();
        var token = _filterCancellation.Token; var source = _sourceGeneration; var generation = ++_filterGeneration;
        var items = HasSnapshot ? _result!.Data!.Items : Array.Empty<InventoryItem>();
        var filter = new InventoryFilter(Query, SelectedSortIndex is >= 0 and <= 3 ? (InventorySort)SelectedSortIndex : InventorySort.Name, MarketableOnly, TradableOnly);
        IsFiltering = true; _filterError = "";
        try
        {
            if (debounce) await Task.Delay(150, token);
            var rows = await Task.Run(() => InventoryQuery.Apply(items, filter, token).Select(x => new InventoryRow(x)).ToArray(), token);
            if (!Current()) return;
            var selectedId = SelectedItem?.AssetId;
            // ItemsSource replacement may transiently clear Avalonia's two-way selection.
            _publishingRows = true;
            try { Rows = rows; }
            finally { _publishingRows = false; }
            SelectedItem = selectedId is null ? null : rows.FirstOrDefault(x => x.AssetId == selectedId);
            Notify(nameof(SelectedItem)); Notify(nameof(ShownCount));
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (Current()) { _filterError = InventoryText.Get("FilterFailed"); Rows = Array.Empty<InventoryRow>(); SelectedItem = null; }
        }
        finally { if (source == _sourceGeneration && generation == _filterGeneration) { IsFiltering = false; Notify(nameof(StateMessage)); } }
        bool Current() => !_disposed && source == _sourceGeneration && generation == _filterGeneration && !token.IsCancellationRequested;
    }

    private async Task LoadSelectedIconAsync(InventoryRow? row)
    {
        var selection = ++_selectionGeneration;
        _iconCancellation?.Cancel(); _iconCancellation?.Dispose(); _iconCancellation = new();
        var token = _iconCancellation.Token;
        var previous = SelectedIcon; SelectedIcon = null; previous?.Dispose();
        IsIconBusy = false;
        if (_disposed || row?.IconUrl is not { Length: > 0 } url) return;
        IsIconBusy = true;
        try
        {
            var bitmap = await _icons.LoadAsync(url, token);
            if (_disposed || token.IsCancellationRequested || selection != _selectionGeneration) { bitmap?.Dispose(); return; }
            SelectedIcon = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // Optional artwork never hides available item facts.
        finally { if (selection == _selectionGeneration) IsIconBusy = false; }
    }

    public void ShowLocalMessage(string key) => LocalMessage = InventoryText.Get(key);
    private void ResetFilters()
    {
        _query = ""; _marketableOnly = _tradableOnly = false; _selectedSortIndex = 0;
        Notify(nameof(Query)); Notify(nameof(MarketableOnly)); Notify(nameof(TradableOnly)); Notify(nameof(SelectedSortIndex)); QueueFilters();
    }
    private void ResetSource()
    {
        ++_sourceGeneration; ++_loadGeneration; ++_filterGeneration; ++_selectionGeneration;
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = null;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _filterCancellation = null;
        _iconCancellation?.Cancel(); _iconCancellation?.Dispose(); _iconCancellation = null;
        _result = null; _statistics = null; _state = DataState.Unavailable; _cancelled = false;
        _pagesLoaded = _assetsLoaded = 0; _reportedAssetCount = null; _filterError = "";
        _publishingRows = true;
        try { Rows = Array.Empty<InventoryRow>(); }
        finally { _publishingRows = false; }
        _selectedItem = null; Notify(nameof(SelectedItem)); Notify(nameof(HasSelection));
        var icon = SelectedIcon; SelectedIcon = null; icon?.Dispose();
        IsFiltering = false; IsIconBusy = false; LocalMessage = ""; NotifyAll();
    }
    public void Clear()
    {
        if (_disposed) return;
        _steamId = ""; _hasProfile = false; ResetSource();
    }
    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(SteamId), nameof(Source), nameof(HasProfile), nameof(State), nameof(IsBusy), nameof(CanLoad), nameof(HasSnapshot), nameof(Snapshot),
            nameof(IsComplete), nameof(HasCompleteness), nameof(StateMessage), nameof(LoadedAssetCount), nameof(QuantityCount), nameof(TotalAssetCount),
            nameof(MetadataCoverage), nameof(ShownCount), nameof(Completeness), nameof(ProgressText), nameof(UpdatedAt) }) Notify(name);
    }
    public override void RefreshLocalization()
    {
        foreach (var row in Rows) row.RefreshLocalization();
        // Retain the selected icon and source operation even when filtering supplied equivalent rows.
        if (SelectedItem is { } selected && !Rows.Any(row => ReferenceEquals(row, selected)))
            selected.RefreshLocalization();
        Notify(nameof(SelectedItem));
        base.RefreshLocalization();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear(); _disposed = true;
    }
}

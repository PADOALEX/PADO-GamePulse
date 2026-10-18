using GamePulse.Desktop.Services;
using System.Globalization;
using System.Windows.Input;
using GamePulse.Core;
using GamePulse.Desktop.Resources;

namespace GamePulse.Desktop.ViewModels;

public sealed record GameRow(LibraryGame Game) : LocalizedRecord
{
    public uint AppId => Game.AppId;
    public string Name => string.IsNullOrWhiteSpace(Game.Name) ? GamesText.Format("Unnamed", AppId) : Game.Name;
    public string Lifetime => Hours(Game.PlaytimeMinutes);
    public string Recent => Hours(Game.RecentPlaytimeMinutes);
    public string LastPlayed => Game.LastPlayed?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? GamesText.Get("NA");
    public string AccessibleName => GamesText.Format("AccessibleGame", Name, AppId, Lifetime, Recent);
    private static string Hours(long? minutes) => minutes is >= 0
        ? GamesText.Format("Hours", minutes.Value / 60d) : GamesText.Get("NA");
}

/// <summary>Independent progressive requests and cancellable background querying of complete, untruncated lists.</summary>
public sealed class GamesViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _service;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _filterCancellation;
    private long _generation;
    private long _filterGeneration;
    private bool _disposed;
    private bool _hasProfile;
    private bool _showRecent;
    private bool _isFiltering;
    private bool _publishingRows;
    private string _query = "";
    private string _minimumHours = "";
    private string _maximumHours = "";
    private bool _unplayedOnly;
    private int _selectedSortIndex;
    private string _filterError = "";
    private string _localMessage = "";
    private GameRow? _selectedGame;
    private IReadOnlyList<GameRow> _rows = Array.Empty<GameRow>();
    private Outcome<OwnedLibrary>? _owned;
    private Outcome<RecentGames>? _recent;
    private DataState _ownedState = DataState.Unavailable;
    private DataState _recentState = DataState.Unavailable;
    private bool _ownedCancelled;
    private bool _recentCancelled;

    public GamesViewModel(ILibraryService service)
    {
        _service = service;
        ResetFiltersCommand = new RelayCommand(_ => ResetFilters());
    }

    public IReadOnlyList<LocalizedText> SortOptions { get; } = [LocalizedResources.For("Games").Text("MostPlayed"), LocalizedResources.For("Games").Text("Name"), LocalizedResources.For("Games").Text("LeastPlayed"), LocalizedResources.For("Games").Text("RecentlyPlayed")];
    public ICommand ResetFiltersCommand { get; }
    public IReadOnlyList<GameRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public GameRow? SelectedGame
    {
        get => _selectedGame;
        set { if (!_disposed && !_publishingRows && Set(ref _selectedGame, value)) { LocalMessage = ""; Notify(nameof(HasSelection)); } }
    }
    public bool HasSelection => SelectedGame is not null;
    public bool ShowRecent
    {
        get => _showRecent;
        set
        {
            if (!Set(ref _showRecent, value)) return;
            Notify(nameof(ShowOwned));
            Notify(nameof(SelectedModeIndex));
            // This filter makes an ownership claim and is meaningful only for a complete library.
            if (value && _unplayedOnly) { _unplayedOnly = false; Notify(nameof(UnplayedOnly)); }
            SelectedGame = null;
            Rows = Array.Empty<GameRow>();
            NotifyAll();
            QueueFilters();
        }
    }
    public bool ShowOwned { get => !ShowRecent; set { if (value) ShowRecent = false; } }
    public int SelectedModeIndex { get => ShowRecent ? 1 : 0; set => ShowRecent = value == 1; }
    public string Query { get => _query; set { if (Set(ref _query, value)) QueueFilters(); } }
    public string MinimumHours { get => _minimumHours; set { if (Set(ref _minimumHours, value)) QueueFilters(); } }
    public string MaximumHours { get => _maximumHours; set { if (Set(ref _maximumHours, value)) QueueFilters(); } }
    public bool UnplayedOnly { get => _unplayedOnly; set { if (Set(ref _unplayedOnly, value)) QueueFilters(); } }
    public int SelectedSortIndex { get => _selectedSortIndex; set { if (Set(ref _selectedSortIndex, value)) QueueFilters(); } }
    public bool IsFiltering { get => _isFiltering; private set { if (Set(ref _isFiltering, value)) Notify(nameof(ActiveStateMessage)); } }
    public string FilterError { get => LocalizedStatus.Read(_filterError, "Games"); private set { if (Set(ref _filterError, value)) { Notify(nameof(HasFilterError)); Notify(nameof(ActiveStateMessage)); } } }
    public bool HasFilterError => FilterError.Length != 0;
    public string LocalMessage { get => LocalizedStatus.Read(_localMessage, "Games"); private set => Set(ref _localMessage, value); }
    public DataState OwnedState => _ownedState;
    /// <summary>The unfiltered library snapshot shared with local analytics; no additional requests.</summary>
    public Outcome<OwnedLibrary>? OwnedSnapshot => _owned;
    public Outcome<RecentGames>? RecentSnapshot => _recent;
    public bool HasProfile => _hasProfile;
    public DataState RecentState => _recentState;
    public bool IsBusy => _ownedState == DataState.Loading || _recentState == DataState.Loading;
    public bool ActiveLoading => (ShowRecent ? _recentState : _ownedState) == DataState.Loading;
    public string OwnedStatus => StateMessage(_ownedState, false, _ownedCancelled);
    public string RecentStatus => StateMessage(_recentState, true, _recentCancelled);
    public string OwnedUpdatedAt => Timestamp(_owned?.UpdatedAt, _owned?.FromCache == true);
    public string RecentUpdatedAt => Timestamp(_recent?.UpdatedAt, _recent?.FromCache == true);
    public string ActiveUpdatedAt => ShowRecent ? RecentUpdatedAt : OwnedUpdatedAt;
    public bool CanFilterUnplayed => !ShowRecent && _owned.HasData() && _owned!.Data!.IsComplete;
    public string AvailableCount => _owned.HasData()
        ? $"{_owned!.Data!.Games.Count:N0} / {_owned.Data.ReportedCount:N0}" : GamesText.Get("NA");
    public string TotalHours => CompleteKnownLibrary ? GamesText.Format("Hours", _owned!.Data!.Games.Sum(x => (decimal)x.PlaytimeMinutes!.Value) / 60m) : GamesText.Get("NA");
    public string UnplayedCount => CompleteKnownLibrary ? _owned!.Data!.Games.Count(x => x.PlaytimeMinutes == 0).ToString("N0") : GamesText.Get("NA");
    public string PlaytimeCoverage => _owned.HasData() ? GamesText.Format("Coverage",
        _owned!.Data!.Games.Count(x => x.PlaytimeMinutes is >= 0), _owned.Data.Games.Count) : "";
    public string LibraryCompleteness => _owned.HasData() && !_owned!.Data!.IsComplete
        ? GamesText.Format("Partial", _owned.Data.Games.Count, _owned.Data.ReportedCount) : "";
    public string ShownCount => GamesText.Format("Shown", Rows.Count, ActiveGames.Count);
    public string ActiveStateMessage => !_hasProfile ? GamesText.Get("Idle") : ActiveLoading ? GamesText.Get("Loading") :
        HasFilterError && ActiveHasData ? FilterError : IsFiltering ? GamesText.Get("Filtering") :
        ActiveHasData && ActiveGames.Count > 0 && Rows.Count == 0 ? GamesText.Get("NoMatches") : ShowRecent ? RecentStatus : OwnedStatus;
    private bool CompleteKnownLibrary => _owned.HasData() && _owned!.Data!.IsComplete && _owned.Data.Games.All(x => x.PlaytimeMinutes is >= 0);
    private bool ActiveHasData => ShowRecent ? _recent.HasData() : _owned.HasData();
    private IReadOnlyList<LibraryGame> ActiveGames => ShowRecent
        ? _recent.HasData() ? _recent!.Data!.Games : Array.Empty<LibraryGame>()
        : _owned.HasData() ? _owned!.Data!.Games : Array.Empty<LibraryGame>();

    public async Task LoadAsync(string steamId, bool force, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clear();
        _hasProfile = true;
        _ownedState = _recentState = DataState.Loading;
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = _loadCancellation.Token;
        var generation = _generation;
        NotifyAll();
        await Task.WhenAll(LoadOwnedAsync(), LoadRecentAsync());
        // A task already completed when WaitAsync sees cancellation can still return a value.
        // The generation/token guard rejects it; finish its loading state explicitly.
        if (generation == _generation && cancellation.IsCancellationRequested)
        {
            if (_ownedState == DataState.Loading) { _ownedState = DataState.Unavailable; _ownedCancelled = true; }
            if (_recentState == DataState.Loading) { _recentState = DataState.Unavailable; _recentCancelled = true; }
            NotifyAll();
        }

        async Task LoadOwnedAsync()
        {
            try
            {
                var result = await _service.GetLibraryAsync(steamId, force, cancellation).WaitAsync(cancellation);
                if (!IsCurrent(generation, cancellation)) return;
                _owned = result;
                _ownedState = result.State;
                if (!CanFilterUnplayed && _unplayedOnly) { _unplayedOnly = false; Notify(nameof(UnplayedOnly)); }
            }
            catch (OperationCanceledException) { if (generation != _generation) return; _ownedState = DataState.Unavailable; _ownedCancelled = true; }
            catch (Exception) { if (generation != _generation) return; _ownedState = DataState.Failed; }
            if (generation != _generation) return;
            NotifyAll();
            if (!ShowRecent) await ApplyFiltersAsync();
        }

        async Task LoadRecentAsync()
        {
            try
            {
                var result = await _service.GetRecentAsync(steamId, force, cancellation).WaitAsync(cancellation);
                if (!IsCurrent(generation, cancellation)) return;
                _recent = result;
                _recentState = result.State;
            }
            catch (OperationCanceledException) { if (generation != _generation) return; _recentState = DataState.Unavailable; _recentCancelled = true; }
            catch (Exception) { if (generation != _generation) return; _recentState = DataState.Failed; }
            if (generation != _generation) return;
            NotifyAll();
            if (ShowRecent) await ApplyFiltersAsync();
        }
    }

    /// <summary>Apply immediately; also provides a deterministic await point for automation and tests.</summary>
    public Task ApplyFiltersAsync() => FilterAsync(debounce: false);
    private void QueueFilters() => _ = FilterAsync(debounce: true);

    private async Task FilterAsync(bool debounce)
    {
        if (_disposed) return;
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = new CancellationTokenSource();
        var token = _filterCancellation.Token;
        var generation = _generation;
        var filterGeneration = ++_filterGeneration;
        var games = ActiveGames;
        var hasData = ActiveHasData;
        var query = Query;
        var minimum = MinimumHours;
        var maximum = MaximumHours;
        var unplayed = UnplayedOnly;
        var allowUnplayed = CanFilterUnplayed;
        var sortIndex = SelectedSortIndex;
        IsFiltering = true;
        try
        {
            if (debounce) await Task.Delay(180, token);
            if (!hasData)
            {
                if (Current()) { FilterError = ""; Rows = Array.Empty<GameRow>(); SelectedGame = null; Notify(nameof(ShownCount)); }
                return;
            }
            if (!TryHours(minimum, out var min) || !TryHours(maximum, out var max)) { Reject("InvalidHours"); return; }
            if (min > max) { Reject("InvalidRange"); return; }
            if (unplayed && !allowUnplayed) { Reject("UnplayedUnavailable"); return; }
            var sort = sortIndex is >= 0 and <= 3 ? (LibrarySort)sortIndex : LibrarySort.MostPlayed;
            var filter = new LibraryFilter(query, min, max, unplayed, sort);
            // Enumeration, sorting and row projection happen away from the UI thread.
            var rows = await Task.Run(() => LibraryQuery.Apply(games, filter, token).Select(x => new GameRow(x)).ToArray(), token);
            if (!Current()) return;
            FilterError = "";
            var selectedId = SelectedGame?.AppId;
            _publishingRows = true;
            try { Rows = rows; }
            finally { _publishingRows = false; }
            SelectedGame = selectedId is null ? null : rows.FirstOrDefault(row => row.AppId == selectedId);
            Notify(nameof(SelectedGame));
            Notify(nameof(ShownCount));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Current()) Reject("FilterFailed"); }
        finally
        {
            if (filterGeneration == _filterGeneration && generation == _generation)
            { IsFiltering = false; Notify(nameof(ActiveStateMessage)); }
        }

        bool Current() => !_disposed && generation == _generation && filterGeneration == _filterGeneration && !token.IsCancellationRequested;
        void Reject(string key)
        {
            if (!Current()) return;
            FilterError = GamesText.Get(key);
            Rows = Array.Empty<GameRow>();
            SelectedGame = null;
            Notify(nameof(ShownCount));
        }
    }

    public void Clear()
    {
        ++_generation;
        ++_filterGeneration;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = null;
        _hasProfile = false;
        _owned = null;
        _recent = null;
        _ownedState = _recentState = DataState.Unavailable;
        _ownedCancelled = _recentCancelled = false;
        Rows = Array.Empty<GameRow>();
        SelectedGame = null;
        FilterError = LocalMessage = "";
        IsFiltering = false;
        NotifyAll();
    }

    private void ResetFilters()
    {
        _query = _minimumHours = _maximumHours = "";
        _unplayedOnly = false;
        _selectedSortIndex = 0;
        Notify(nameof(Query)); Notify(nameof(MinimumHours)); Notify(nameof(MaximumHours));
        Notify(nameof(UnplayedOnly)); Notify(nameof(SelectedSortIndex));
        QueueFilters();
    }

    public void ShowLocalMessage(string key) => LocalMessage = GamesText.Get(key);
    private bool IsCurrent(long generation, CancellationToken token) => generation == _generation && !token.IsCancellationRequested && !_disposed;
    private static bool TryHours(string value, out double? hours)
    {
        hours = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.CurrentCulture, out var parsed) || !double.IsFinite(parsed) || parsed < 0) return false;
        hours = parsed;
        return true;
    }
    private string StateMessage(DataState state, bool recent, bool cancelled) => GamesText.Get(!_hasProfile ? "Idle" : cancelled ? "Cancelled" : state switch
    {
        DataState.Ready => "Ready", DataState.Empty => recent ? "RecentEmpty" : "OwnedEmpty",
        DataState.Loading => "Loading", DataState.Private => "Private", DataState.MissingKey => "MissingKey",
        DataState.RateLimited => "RateLimited", DataState.Failed => "Failed", _ => "Unavailable"
    });
    private static string Timestamp(DateTimeOffset? value, bool cached) => value.HasValue
        ? GamesText.Format(cached ? "Cached" : "Updated", value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";
    private void NotifyAll()
    {
        foreach (var property in new[] { nameof(OwnedState), nameof(OwnedSnapshot), nameof(RecentSnapshot), nameof(HasProfile), nameof(RecentState), nameof(IsBusy), nameof(ActiveLoading),
            nameof(OwnedStatus), nameof(RecentStatus), nameof(OwnedUpdatedAt), nameof(RecentUpdatedAt), nameof(ActiveUpdatedAt),
            nameof(CanFilterUnplayed), nameof(AvailableCount), nameof(TotalHours), nameof(UnplayedCount), nameof(PlaytimeCoverage),
            nameof(LibraryCompleteness), nameof(ShownCount), nameof(ActiveStateMessage) }) Notify(property);
    }
    public override void RefreshLocalization()
    {
        foreach (var row in Rows) row.RefreshLocalization();
        if (SelectedGame is { } selected && !Rows.Any(row => ReferenceEquals(row, selected)))
            selected.RefreshLocalization();
        Notify(nameof(SelectedGame));
        base.RefreshLocalization();
    }

    public void Dispose() { if (_disposed) return; Clear(); _disposed = true; }
}

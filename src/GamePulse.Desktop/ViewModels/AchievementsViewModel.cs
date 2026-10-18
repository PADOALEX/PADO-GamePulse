using GamePulse.Desktop.Services;
using System.Globalization;
using GamePulse.Core;
using GamePulse.Desktop.Resources;

namespace GamePulse.Desktop.ViewModels;

/// <summary>Compact per-game facts; full achievement payloads are retained only for the selected game.</summary>
public sealed class AchievementGameRow(LibraryGame game) : ObservableObject
{
    public LibraryGame Game { get; } = game;
    public uint AppId => Game.AppId;
    public string Name => string.IsNullOrWhiteSpace(Game.Name) ? AchievementsText.Format("GameFallback", AppId) : Game.Name;
    public DataState State { get; private set; } = DataState.Unavailable;
    public bool IsChecked { get; private set; }
    public bool IsLoading { get; private set; }
    public bool WasCancelled { get; private set; }
    public AchievementCountSnapshot Counts { get; private set; } = new(false, null, null);
    public DateTimeOffset? UpdatedAt { get; private set; }
    public bool FromCache { get; private set; }
    public bool HasCompletion => Counts is { IsComplete: true, TotalCount: > 0, UnlockedCount: not null };
    public double Completion => HasCompletion ? 100d * Counts.UnlockedCount!.Value / Counts.TotalCount!.Value : 0;
    public string Progress => HasCompletion ? AchievementsText.Format("Percent", Completion) : AchievementsText.Get("NA");
    public string AccessibleName => AchievementsText.Format("AccessibleGame", Name, AppId, Status, Progress);
    public string Status
    {
        get
        {
            if (IsLoading) return AchievementsText.Get("LoadingRow");
            if (WasCancelled) return AchievementsText.Get("CancelledShort");
            if (!IsChecked) return AchievementsText.Get("Unchecked");
            if (State == DataState.Ready && HasCompletion)
                return AchievementsText.Format("ReadyShort", Counts.UnlockedCount!.Value, Counts.TotalCount!.Value);
            return AchievementsText.Get(State switch
            {
                DataState.NoAchievements => "NoAchievementsShort", DataState.Private => "PrivateShort",
                DataState.MissingKey => "MissingKeyShort", DataState.RateLimited => "RateLimitedShort",
                DataState.Failed => "FailedShort", DataState.Ready => "PartialShort", _ => "UnavailableShort"
            });
        }
    }

    internal void Begin(bool force)
    {
        if (force) { IsChecked = false; Counts = new(false, null, null); UpdatedAt = null; FromCache = false; }
        IsLoading = true; WasCancelled = false; Raise();
    }
    internal void Complete(Outcome<GameAchievements> result)
    {
        State = result.State; IsLoading = false; WasCancelled = false; IsChecked = true;
        if (result is { State: DataState.Ready, Data: { } data })
        {
            var total = data.TotalCount;
            Counts = new(data.IsComplete, total.HasValue ? data.Achievements.Count(x => x.Unlocked == true) : null, total);
        }
        else Counts = new(false, null, null);
        UpdatedAt = result.UpdatedAt; FromCache = result.FromCache; Raise();
    }
    internal void Cancel()
    {
        IsLoading = false; WasCancelled = true; Raise();
    }
    private void Raise()
    {
        foreach (var property in new[] { nameof(State), nameof(IsChecked), nameof(IsLoading), nameof(WasCancelled),
            nameof(Counts), nameof(HasCompletion), nameof(Completion), nameof(Progress), nameof(Status), nameof(AccessibleName) }) Notify(property);
    }
}

public sealed record AchievementRow(AchievementDetail Achievement) : LocalizedRecord
{
    public string ApiName => Achievement.ApiName;
    public string Name => string.IsNullOrWhiteSpace(Achievement.DisplayName) ? ApiName : Achievement.DisplayName;
    public string Description => string.IsNullOrWhiteSpace(Achievement.Description)
        ? AchievementsText.Get("NoDescription") : Achievement.Description;
    public string Status => AchievementsText.Get(Achievement.Unlocked switch { true => "Unlocked", false => "Locked", _ => "Unknown" });
    public string AccessibleName => AchievementsText.Format("AccessibleAchievement", Name, Status, Description);
    public bool IsUnlocked => Achievement.Unlocked == true;
    public string Hidden => AchievementsText.Get(Achievement.Hidden switch { true => "HiddenYes", false => "HiddenNo", _ => "Unknown" });
    public string UnlockedAt => Achievement.Unlocked == false ? AchievementsText.Get("NotUnlocked") : Achievement.Unlocked == true
        ? Achievement.UnlockedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? AchievementsText.Get("Unknown")
        : AchievementsText.Get("Unknown");
}

/// <summary>Selected-only requests, independent cancellable filters, and bounded selected detail state.</summary>
public sealed class AchievementsViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _libraries;
    private readonly IAchievementService _achievements;
    private CancellationTokenSource? _profileCancellation, _selectionCancellation, _gameFilterCancellation, _filterCancellation;
    private long _profileGeneration, _selectionGeneration, _gameFilterGeneration, _filterGeneration;
    private bool _disposed, _hasProfile, _libraryCancelled, _selectedCancelled, _isGameFiltering, _isFiltering;
    private bool _publishingGameRows, _publishingAchievementRows;
    private string _steamId = "", _gameQuery = "", _query = "", _filterError = "";
    private int _selectedGameSortIndex, _selectedUnlockFilterIndex, _selectedSortIndex;
    private DataState _libraryState = DataState.Unavailable, _selectedState = DataState.Unavailable;
    private Outcome<OwnedLibrary>? _library;
    private Outcome<GameAchievements>? _selectedResult;
    private IReadOnlyList<AchievementGameRow> _allGames = Array.Empty<AchievementGameRow>();
    private IReadOnlyList<AchievementGameRow> _libraryRows = Array.Empty<AchievementGameRow>();
    private IReadOnlyList<AchievementRow> _achievementRows = Array.Empty<AchievementRow>();
    private AchievementGameRow? _selectedGame;
    private AchievementRow? _selectedAchievement;
    private AchievementProgressSummary _progress = new(0, 0, 0, null, null);

    public AchievementsViewModel(ILibraryService libraries, IAchievementService achievements)
    {
        _libraries = libraries; _achievements = achievements;
    }
    public IReadOnlyList<LocalizedText> GameSortOptions { get; } = [LocalizedResources.For("Achievements").Text("GameSortName"), LocalizedResources.For("Achievements").Text("GameSortTime"), LocalizedResources.For("Achievements").Text("GameSortChecked")];
    public IReadOnlyList<LocalizedText> UnlockFilterOptions { get; } = [LocalizedResources.For("Achievements").Text("All"), LocalizedResources.For("Achievements").Text("Unlocked"), LocalizedResources.For("Achievements").Text("Locked")];
    public IReadOnlyList<LocalizedText> SortOptions { get; } = [LocalizedResources.For("Achievements").Text("SortName"), LocalizedResources.For("Achievements").Text("SortUnlocked"), LocalizedResources.For("Achievements").Text("SortLocked"), LocalizedResources.For("Achievements").Text("SortRecent")];
    public IReadOnlyList<AchievementGameRow> LibraryRows { get => _libraryRows; private set => Set(ref _libraryRows, value); }
    public IReadOnlyList<AchievementRow> AchievementRows { get => _achievementRows; private set => Set(ref _achievementRows, value); }
    public string GameQuery { get => _gameQuery; set { if (Set(ref _gameQuery, value)) GameFilteringTask = FilterGamesAsync(true); } }
    public int SelectedGameSortIndex { get => _selectedGameSortIndex; set { if (Set(ref _selectedGameSortIndex, value)) GameFilteringTask = FilterGamesAsync(true); } }
    public string Query { get => _query; set { if (Set(ref _query, value)) FilteringTask = FilterAsync(true); } }
    public int SelectedUnlockFilterIndex { get => _selectedUnlockFilterIndex; set { if (Set(ref _selectedUnlockFilterIndex, value)) FilteringTask = FilterAsync(true); } }
    public int SelectedSortIndex { get => _selectedSortIndex; set { if (Set(ref _selectedSortIndex, value)) FilteringTask = FilterAsync(true); } }
    public AchievementGameRow? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (_disposed || _publishingGameRows || !Set(ref _selectedGame, value)) return;
            SelectionLoadingTask = LoadSelectedAsync(value, false);
        }
    }
    public AchievementRow? SelectedAchievement
    { get => _selectedAchievement; set { if (!_publishingAchievementRows && Set(ref _selectedAchievement, value)) Notify(nameof(HasAchievementSelection)); } }
    public Task SelectionLoadingTask { get; private set; } = Task.CompletedTask;
    public Task GameFilteringTask { get; private set; } = Task.CompletedTask;
    public Task FilteringTask { get; private set; } = Task.CompletedTask;
    public bool HasSelection => SelectedGame is not null;
    public bool HasAchievementSelection => SelectedAchievement is not null;
    public bool CanRefreshSelected => HasSelection && !IsSelectionBusy;
    public DataState LibraryState => _libraryState;
    public DataState SelectedState => _selectedState;
    public bool IsLibraryBusy => LibraryState == DataState.Loading;
    public bool IsSelectionBusy => SelectedState == DataState.Loading;
    public bool IsGameFiltering { get => _isGameFiltering; private set { if (Set(ref _isGameFiltering, value)) Notify(nameof(LibraryMessage)); } }
    public bool IsFiltering { get => _isFiltering; private set { if (Set(ref _isFiltering, value)) Notify(nameof(SelectedMessage)); } }
    public int CheckedGameCount => _allGames.Count(x => x.IsChecked);
    public int AvailableGameCount => _allGames.Count;
    public string Coverage => _library.HasData() ? AchievementsText.Format("Coverage", CheckedGameCount, AvailableGameCount) : AchievementsText.Get("NA");
    public string WeightedCompletion => Percent(_progress.WeightedCompletion);
    public string AverageCompletion => Percent(_progress.AveragePerGameCompletion);
    public int IncludedGames => _progress.IncludedGames;
    public string IncludedGamesText => IncludedGames == 1 ? AchievementsText.Get("IncludedOne") : AchievementsText.Format("Included", IncludedGames);
    public string WeightedNote => AchievementsText.Get("WeightedNote");
    public string AverageNote => AchievementsText.Get("AverageNote");
    public string SelectedGameName => SelectedGame?.Name ?? AchievementsText.Get("Title");
    public string SelectedAppId => SelectedGame is { } game ? $"AppID {game.AppId}" : "";
    private bool HasReadyResult => SelectedState == DataState.Ready && _selectedResult is { State: DataState.Ready, Data: not null };
    public bool HasSelectedProgress => HasReadyResult && SelectedGame?.HasCompletion == true;
    public double ProgressValue => HasSelectedProgress ? SelectedGame!.Completion : 0;
    public string SelectedProgress => HasSelectedProgress ? Percent(ProgressValue) : AchievementsText.Get("NA");
    public string SelectedTotal => SelectedState == DataState.NoAchievements ? "0" : HasReadyResult ? SelectedGame!.Counts.TotalCount?.ToString("N0") ?? AchievementsText.Get("NA") : AchievementsText.Get("NA");
    public string SelectedUnlocked => SelectedState == DataState.NoAchievements ? "0" : HasReadyResult ? SelectedGame!.Counts.UnlockedCount?.ToString("N0") ?? AchievementsText.Get("NA") : AchievementsText.Get("NA");
    public string SelectedTotalsText => AchievementsText.Format("SelectedTotals", SelectedUnlocked, SelectedTotal, SelectedProgress);
    public string ShownCount => _selectedResult is { State: DataState.Ready, Data: not null }
        ? AchievementsText.Format("Shown", AchievementRows.Count, _selectedResult.Data.Achievements.Count) : "";
    public string UpdatedAt => SelectedGame?.UpdatedAt is { } date
        ? AchievementsText.Format(SelectedGame.FromCache ? "Cached" : "Updated", date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";
    public bool HasLibraryCompleteness => _library.HasData() && !_library!.Data!.IsComplete;
    public string LibraryCompleteness => HasLibraryCompleteness ? AchievementsText.Get("PartialLibrary") : "";
    public string LibraryMessage => AchievementsText.Get(!_hasProfile ? "IdleLibrary" : _libraryCancelled ? "CancelledLibrary" : LibraryState switch
    {
        DataState.Loading => "LoadingLibrary", DataState.Empty => "EmptyLibrary", DataState.Private => "PrivateLibrary",
        DataState.Failed => "FailedLibrary", DataState.MissingKey => "MissingKey", DataState.RateLimited => "RateLimited",
        DataState.Ready when LibraryRows.Count == 0 && _allGames.Count > 0 => "NoGamesMatch",
        DataState.Ready => "ReadyLibrary", _ => "UnavailableLibrary"
    }) is var text && LibraryState == DataState.Ready && _hasProfile && !_libraryCancelled && (LibraryRows.Count > 0 || _allGames.Count == 0)
        ? AchievementsText.Format("ReadyLibrary", _allGames.Count) : text;
    public string SelectedMessage => !HasSelection ? AchievementsText.Get("SelectGame") : _selectedCancelled ? AchievementsText.Get("Cancelled") :
        _filterError.Length > 0 ? LocalizedStatus.Read(_filterError, "Achievements") : IsFiltering && SelectedState == DataState.Ready ? AchievementsText.Get("Filtering") :
        SelectedState switch
        {
            DataState.Loading => AchievementsText.Get("Loading"), DataState.NoAchievements => AchievementsText.Get("NoAchievements"),
            DataState.Private => AchievementsText.Get("Private"), DataState.MissingKey => AchievementsText.Get("MissingKey"),
            DataState.RateLimited => AchievementsText.Get("RateLimited"), DataState.Failed => AchievementsText.Get("Failed"),
            DataState.Ready when !HasSelectedProgress => AchievementsText.Get("Partial"),
            DataState.Ready when AchievementRows.Count == 0 && _selectedResult?.Data?.Achievements.Count > 0 => AchievementsText.Get("NoMatches"),
            DataState.Ready => AchievementsText.Format("Ready", SelectedGame!.Counts.UnlockedCount!.Value, SelectedGame.Counts.TotalCount!.Value),
            _ => AchievementsText.Get("Unavailable")
        };
    private static string Percent(double? value) => value is { } number ? AchievementsText.Format("Percent", number) : AchievementsText.Get("NA");

    /// <summary>Loads only the library. It deliberately makes no per-game achievement request or first selection.</summary>
    public async Task LoadAsync(string steamId, bool force, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clear(); _hasProfile = true; _steamId = steamId; _libraryState = DataState.Loading;
        _profileCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = _profileCancellation.Token; var generation = _profileGeneration;
        NotifyLibrary(); NotifySelection();
        try
        {
            var result = await _libraries.GetLibraryAsync(steamId, force, cancellation).WaitAsync(cancellation);
            if (!Current(generation, cancellation)) return;
            _library = result; _libraryState = result.State;
            _allGames = result.HasData() ? result.Data!.Games.Select(x => new AchievementGameRow(x)).ToArray() : Array.Empty<AchievementGameRow>();
            NotifyLibrary();
            await ApplyGameFiltersAsync();
        }
        catch (OperationCanceledException) { if (generation == _profileGeneration) { _libraryState = DataState.Unavailable; _libraryCancelled = true; } }
        catch (Exception) { if (generation == _profileGeneration) _libraryState = DataState.Failed; }
        finally
        {
            if (generation == _profileGeneration)
            {
                if (cancellation.IsCancellationRequested && _libraryState == DataState.Loading)
                { _libraryState = DataState.Unavailable; _libraryCancelled = true; }
                NotifyLibrary();
            }
        }
    }

    public Task RefreshSelectedAsync()
    {
        if (_disposed || SelectedGame is null) return Task.CompletedTask;
        return SelectionLoadingTask = LoadSelectedAsync(SelectedGame, true);
    }
    private async Task LoadSelectedAsync(AchievementGameRow? row, bool force)
    {
        CancelSelectionRequest();
        var generation = _profileGeneration; var selection = _selectionGeneration;
        _selectedResult = null; _selectedState = DataState.Unavailable; _selectedCancelled = false; _filterError = "";
        AchievementRows = Array.Empty<AchievementRow>(); SelectedAchievement = null;
        if (_disposed || row is null || !_hasProfile || !_allGames.Contains(row)) { NotifySelection(); return; }
        // Compact negative results have no large payload. Revisit them without implicitly retrying a failure.
        if (!force && row.IsChecked && row.State != DataState.Ready)
        { _selectedState = row.State; _selectedCancelled = row.WasCancelled; NotifySelection(); return; }
        _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_profileCancellation?.Token ?? CancellationToken.None);
        var token = _selectionCancellation.Token;
        row.Begin(force); _selectedState = DataState.Loading; RecalculateProgress(); NotifySelection();
        try
        {
            var result = await _achievements.GetAchievementsAsync(_steamId, row.AppId, force, token).WaitAsync(token);
            if (!SelectedCurrent()) return;
            row.Complete(result); _selectedState = result.State; _selectedResult = result;
            RecalculateProgress(); NotifySelection();
            await ApplyFiltersAsync();
            if (SelectedGameSortIndex == 2) await ApplyGameFiltersAsync();
        }
        catch (OperationCanceledException)
        { if (SelectedCurrent(ignoreCancellation: true)) { row.Cancel(); _selectedState = DataState.Unavailable; _selectedCancelled = true; } }
        catch (Exception)
        {
            if (!SelectedCurrent()) return;
            row.Complete(Outcome<GameAchievements>.Failure(DataState.Failed, "Achievement request failed."));
            _selectedState = DataState.Failed; RecalculateProgress();
        }
        finally
        {
            if (generation == _profileGeneration && selection == _selectionGeneration)
            {
                if (token.IsCancellationRequested && _selectedState == DataState.Loading)
                { row.Cancel(); _selectedState = DataState.Unavailable; _selectedCancelled = true; }
                NotifySelection();
            }
        }
        bool SelectedCurrent(bool ignoreCancellation = false) => !_disposed && generation == _profileGeneration &&
            selection == _selectionGeneration && (ignoreCancellation || !token.IsCancellationRequested) && ReferenceEquals(SelectedGame, row);
    }
    public void CancelSelected()
    {
        if (_disposed || !IsSelectionBusy) return;
        _selectionCancellation?.Cancel(); SelectedGame?.Cancel();
        _selectedState = DataState.Unavailable; _selectedCancelled = true;
        NotifySelection();
    }
    private void CancelSelectionRequest()
    {
        ++_selectionGeneration; ++_filterGeneration;
        _selectionCancellation?.Cancel(); _selectionCancellation?.Dispose(); _selectionCancellation = null;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _filterCancellation = null;
        foreach (var row in _allGames.Where(x => x.IsLoading)) row.Cancel();
        IsFiltering = false;
    }

    public Task ApplyGameFiltersAsync() => GameFilteringTask = FilterGamesAsync(false);
    private async Task FilterGamesAsync(bool debounce)
    {
        if (_disposed) return;
        _gameFilterCancellation?.Cancel(); _gameFilterCancellation?.Dispose(); _gameFilterCancellation = new();
        var token = _gameFilterCancellation.Token; var profile = _profileGeneration; var generation = ++_gameFilterGeneration;
        var games = _allGames; var query = GameQuery?.Trim() ?? ""; var sort = SelectedGameSortIndex;
        IsGameFiltering = true;
        try
        {
            if (debounce) await Task.Delay(150, token);
            var rows = await Task.Run(() =>
            {
                IEnumerable<AchievementGameRow> result = games.Where(x =>
                { token.ThrowIfCancellationRequested(); return query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.AppId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal); });
                result = sort switch
                {
                    1 => result.OrderByDescending(x => x.Game.PlaytimeMinutes ?? -1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                    2 => result.OrderByDescending(x => x.IsChecked).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
                    _ => result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                };
                var output = result.ThenByIfOrdered().ToArray(); token.ThrowIfCancellationRequested(); return output;
            }, token);
            if (!IsCurrent()) return;
            var selected = SelectedGame;
            // Avalonia clears SelectedItem while replacing ItemsSource. Ignore that transient clear,
            // then republish the current selection without starting another network request.
            _publishingGameRows = true;
            try { LibraryRows = rows; }
            finally { _publishingGameRows = false; }
            if (selected is not null && !rows.Contains(selected)) SelectedGame = null;
            else Notify(nameof(SelectedGame));
            Notify(nameof(LibraryMessage));
        }
        catch (OperationCanceledException) { }
        finally { if (profile == _profileGeneration && generation == _gameFilterGeneration) IsGameFiltering = false; }
        bool IsCurrent() => Current(profile, token) && generation == _gameFilterGeneration;
    }
    public Task ApplyFiltersAsync() => FilteringTask = FilterAsync(false);
    private async Task FilterAsync(bool debounce)
    {
        if (_disposed) return;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _filterCancellation = new();
        var token = _filterCancellation.Token; var profile = _profileGeneration; var selection = _selectionGeneration; var generation = ++_filterGeneration;
        var details = _selectedResult is { State: DataState.Ready, Data: not null } ? _selectedResult.Data.Achievements : Array.Empty<AchievementDetail>();
        var filter = new AchievementFilter(Query ?? "", SelectedUnlockFilterIndex is >= 0 and <= 2 ? (AchievementUnlockFilter)SelectedUnlockFilterIndex : AchievementUnlockFilter.All,
            SelectedSortIndex is >= 0 and <= 3 ? (AchievementSort)SelectedSortIndex : AchievementSort.Name);
        IsFiltering = true; _filterError = "";
        try
        {
            if (debounce) await Task.Delay(150, token);
            var rows = await Task.Run(() => AchievementQuery.Apply(details, filter, token).Select(x => new AchievementRow(x)).ToArray(), token);
            if (!IsCurrent()) return;
            var selectedName = SelectedAchievement?.ApiName;
            _publishingAchievementRows = true;
            try { AchievementRows = rows; }
            finally { _publishingAchievementRows = false; }
            SelectedAchievement = selectedName is null ? null : rows.FirstOrDefault(x => x.ApiName == selectedName);
            Notify(nameof(SelectedAchievement));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (IsCurrent()) { _filterError = AchievementsText.Get("FilterFailed"); AchievementRows = Array.Empty<AchievementRow>(); SelectedAchievement = null; } }
        finally
        {
            if (profile == _profileGeneration && selection == _selectionGeneration && generation == _filterGeneration)
            { IsFiltering = false; Notify(nameof(SelectedMessage)); Notify(nameof(ShownCount)); }
        }
        bool IsCurrent() => Current(profile, token) && selection == _selectionGeneration && generation == _filterGeneration;
    }

    private void RecalculateProgress()
    {
        _progress = AchievementProgress.CalculateCounts(_allGames.Where(x => x.IsChecked).Select(x => x.Counts));
        foreach (var name in new[] { nameof(CheckedGameCount), nameof(Coverage), nameof(WeightedCompletion), nameof(AverageCompletion), nameof(IncludedGames), nameof(IncludedGamesText) }) Notify(name);
    }
    public void Clear()
    {
        ++_profileGeneration; ++_gameFilterGeneration;
        CancelSelectionRequest();
        _profileCancellation?.Cancel(); _profileCancellation?.Dispose(); _profileCancellation = null;
        _gameFilterCancellation?.Cancel(); _gameFilterCancellation?.Dispose(); _gameFilterCancellation = null;
        _hasProfile = false; _steamId = ""; _library = null; _selectedResult = null; _allGames = Array.Empty<AchievementGameRow>();
        _libraryState = _selectedState = DataState.Unavailable; _libraryCancelled = _selectedCancelled = false;
        LibraryRows = Array.Empty<AchievementGameRow>(); AchievementRows = Array.Empty<AchievementRow>();
        _selectedGame = null; Notify(nameof(SelectedGame)); SelectedAchievement = null;
        IsGameFiltering = IsFiltering = false; _filterError = "";
        RecalculateProgress(); NotifyLibrary(); NotifySelection();
    }
    private bool Current(long profile, CancellationToken token) => !_disposed && profile == _profileGeneration && !token.IsCancellationRequested;
    private void NotifyLibrary()
    {
        foreach (var name in new[] { nameof(LibraryState), nameof(IsLibraryBusy), nameof(LibraryMessage), nameof(Coverage), nameof(AvailableGameCount), nameof(HasLibraryCompleteness), nameof(LibraryCompleteness) }) Notify(name);
    }
    private void NotifySelection()
    {
        foreach (var name in new[] { nameof(HasSelection), nameof(CanRefreshSelected), nameof(SelectedState), nameof(IsSelectionBusy), nameof(SelectedMessage),
            nameof(SelectedGameName), nameof(SelectedAppId), nameof(SelectedTotal), nameof(SelectedUnlocked), nameof(SelectedProgress), nameof(ProgressValue),
            nameof(HasSelectedProgress), nameof(UpdatedAt), nameof(ShownCount), nameof(SelectedTotalsText) }) Notify(name);
    }
    public override void RefreshLocalization()
    {
        foreach (var row in LibraryRows) row.RefreshLocalization();
        foreach (var row in AchievementRows) row.RefreshLocalization();
        if (SelectedGame is { } selectedGame && !LibraryRows.Any(row => ReferenceEquals(row, selectedGame)))
            selectedGame.RefreshLocalization();
        if (SelectedAchievement is { } selectedAchievement && !AchievementRows.Any(row => ReferenceEquals(row, selectedAchievement)))
            selectedAchievement.RefreshLocalization();
        Notify(nameof(SelectedGame));
        Notify(nameof(SelectedAchievement));
        base.RefreshLocalization();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear(); _disposed = true;
    }
}

internal static class AchievementGameOrdering
{
    internal static IEnumerable<AchievementGameRow> ThenByIfOrdered(this IEnumerable<AchievementGameRow> source) =>
        source is IOrderedEnumerable<AchievementGameRow> ordered ? ordered.ThenBy(x => x.AppId) : source;
}

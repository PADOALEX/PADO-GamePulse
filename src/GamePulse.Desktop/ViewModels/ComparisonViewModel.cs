using GamePulse.Desktop.Services;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using GamePulse.Core;
using GamePulse.Desktop.Resources;

namespace GamePulse.Desktop.ViewModels;

public sealed record MutualGameRow(uint AppId, string Name, long? MinutesA, long? MinutesB) : LocalizedRecord
{
    public string HoursA => ComparisonFormatting.Hours(MinutesA);
    public string HoursB => ComparisonFormatting.Hours(MinutesB);
    public string AccessibleName => string.Format(CultureInfo.CurrentCulture, ComparisonText.Get("AccessibleMutualGame"), Name, AppId, HoursA, HoursB);
}

/// <summary>Two independent profile scopes; neither side represents the app's owner.</summary>
public sealed class ComparisonViewModel : ObservableObject, IDisposable
{
    private readonly IProfileComparisonService _comparisons;
    private readonly SearchSession _comparisonSession = new();
    private readonly SearchSession _filterSession = new();
    private readonly SearchSession _operationSession = new();
    private ProfileComparison? _comparison;
    private IReadOnlyList<MutualGameRow> _mutualGames = [];
    private string _query = "";
    private string _minimumHours = "";
    private int _sortIndex;
    private DataState _state = DataState.Unavailable;
    private string _status = ComparisonText.NothingSelected;
    private string _filterError = "";
    private string _localMessage = "";
    private bool _isFiltering;
    private bool _disposed;

    public ComparisonViewModel(IProfileLookupService profiles, ILibraryService libraries,
        IProfileComparisonService comparisons)
    {
        _comparisons = comparisons;
        SideA = new(ComparisonText.ProfileA, profiles, libraries);
        SideB = new(ComparisonText.ProfileB, profiles, libraries);
        SideA.PropertyChanged += SideChanged;
        SideB.PropertyChanged += SideChanged;
        CompareCommand = new RelayCommand(_ => _ = CompareAsync(false));
        RefreshCommand = new RelayCommand(_ => _ = CompareAsync(true));
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    public ComparisonSideViewModel SideA { get; }
    public ComparisonSideViewModel SideB { get; }
    public ICommand CompareCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public string Query { get => _query; set { if (Set(ref _query, value ?? "")) _ = ApplyFiltersCoreAsync(true); } }
    public string MinimumHours { get => _minimumHours; set { if (Set(ref _minimumHours, value ?? "")) _ = ApplyFiltersCoreAsync(true); } }
    public int SortIndex { get => _sortIndex; set { if (Set(ref _sortIndex, value)) _ = ApplyFiltersCoreAsync(true); } }
    public IReadOnlyList<LocalizedText> SortOptions { get; } = [LocalizedResources.For("Comparison").Text("CombinedTime"), LocalizedResources.For("Comparison").Text("Name"), LocalizedResources.For("Comparison").Text("TimeA"), LocalizedResources.For("Comparison").Text("TimeB")];
    public IReadOnlyList<MutualGameRow> MutualGames { get => _mutualGames; private set { if (Set(ref _mutualGames, value)) { Notify(nameof(VisibleSummary)); Notify(nameof(HasVisibleGames)); Notify(nameof(ShowNoMatches)); } } }
    public DataState State { get => _state; private set => Set(ref _state, value); }
    public bool HasComparison => _comparison is not null;
    public bool HasVisibleGames => MutualGames.Count > 0;
    public bool ShowNoMatches => HasComparison && MutualGames.Count == 0 && !IsFiltering && FilterError.Length == 0;
    public bool IsBusy => SideA.IsBusy || SideB.IsBusy;
    public bool IsFiltering { get => _isFiltering; private set { if (Set(ref _isFiltering, value)) Notify(nameof(ShowNoMatches)); } }
    public string Status { get => LocalizedStatus.Read(_status, "Comparison"); private set => Set(ref _status, value); }
    public bool HasFilterError => FilterError.Length > 0;
    public string FilterError { get => LocalizedStatus.Read(_filterError, "Comparison"); private set { if (Set(ref _filterError, value)) { Notify(nameof(ShowNoMatches)); Notify(nameof(HasFilterError)); } } }
    public string LocalMessage { get => LocalizedStatus.Read(_localMessage, "Comparison"); private set => Set(ref _localMessage, value); }
    public string VisibleSummary => HasComparison
        ? string.Format(CultureInfo.CurrentCulture, ComparisonText.RowsFormat, MutualGames.Count, _comparison!.MutualGames.Count)
        : ComparisonText.NA;
    public string NoMatches => _comparison?.MutualGames.Count == 0 ? ComparisonText.NoMutual : ComparisonText.NoMatches;

    public async Task CompareAsync(bool forceRefresh)
    {
        if (_disposed) return;
        var (generation, token) = _operationSession.Begin();
        try
        {
            await Task.WhenAll(SideA.SearchAsync(forceRefresh), SideB.SearchAsync(forceRefresh)).WaitAsync(token);
            if (_operationSession.IsCurrent(generation) && !_disposed) await RefreshComparisonAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Cancel()
    {
        _operationSession.Cancel();
        SideA.Cancel();
        SideB.Cancel();
        _comparisonSession.Cancel();
        _filterSession.Cancel();
        IsFiltering = false;
        if (State == DataState.Loading) { State = DataState.Unavailable; Status = ComparisonText.Cancelled; }
    }

    public void ShowLocalMessage(string key) => LocalMessage = ComparisonText.Get(key);

    private void SideChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(ComparisonSideViewModel.IsBusy)) Notify(nameof(IsBusy));
        if (args.PropertyName == nameof(ComparisonSideViewModel.LibraryResult)) _ = RefreshComparisonAsync();
    }

    public async Task RefreshComparisonAsync()
    {
        if (_disposed) return;
        var (generation, token) = _comparisonSession.Begin();
        _filterSession.Cancel();
        _comparison = null;
        Notify(nameof(HasComparison));
        Notify(nameof(NoMatches));
        MutualGames = [];
        IsFiltering = false;
        var libraryA = SideA.LibraryResult;
        var libraryB = SideB.LibraryResult;
        State = DataState.Loading;
        Status = ComparisonText.Waiting;
        try
        {
            // Large libraries are intersected off the UI thread. The generation guard also
            // rejects a completed comparison if either independently editable side changed.
            var result = await Task.Run(() => _comparisons.Compare(libraryA, libraryB), token).WaitAsync(token);
            if (!_comparisonSession.IsCurrent(generation) || _disposed) return;
            State = result.State;
            _comparison = result.HasData() ? result.Data : null;
            Status = result.State switch
            {
                DataState.Private => ComparisonText.Private,
                DataState.Loading => ComparisonText.Waiting,
                DataState.Empty => ComparisonText.NoMutual,
                DataState.Ready => ComparisonText.ComparisonReady,
                _ when libraryA is null && libraryB is null => ComparisonText.NothingSelected,
                _ => ComparisonText.ComparisonUnavailable
            };
            Notify(nameof(HasComparison));
            Notify(nameof(NoMatches));
            Notify(nameof(VisibleSummary));
            await ApplyFiltersCoreAsync(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_comparisonSession.IsCurrent(generation) || _disposed) return;
            State = DataState.Failed;
            Status = ComparisonText.Failed;
        }
    }

    public Task ApplyFiltersAsync() => ApplyFiltersCoreAsync(false);

    private async Task ApplyFiltersCoreAsync(bool debounce)
    {
        if (_disposed) return;
        var (generation, token) = _filterSession.Begin();
        var comparison = _comparison;
        var query = Query.Trim();
        var minimumText = MinimumHours.Trim();
        var sort = SortIndex;
        IsFiltering = true;
        FilterError = "";
        try
        {
            if (debounce) await Task.Delay(150, token);
            double? minimum = null;
            if (minimumText.Length > 0)
            {
                if (!double.TryParse(minimumText, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ||
                    !double.IsFinite(parsed) || parsed < 0)
                {
                    if (_filterSession.IsCurrent(generation)) { FilterError = ComparisonText.FilterInvalid; MutualGames = []; }
                    return;
                }
                minimum = parsed;
            }
            var rows = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                IEnumerable<MutualGame> games = comparison?.MutualGames ?? [];
                games = games.Where(game =>
                {
                    token.ThrowIfCancellationRequested();
                    return (query.Length == 0 || (game.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || game.AppId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal)) &&
                        (minimum is null || (game.MinutesA.HasValue && game.MinutesB.HasValue &&
                            game.MinutesA.Value / 60d >= minimum && game.MinutesB.Value / 60d >= minimum));
                });
                var sorted = sort switch
                {
                    1 => games.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ThenBy(game => game.AppId),
                    2 => games.OrderByDescending(game => game.MinutesA).ThenBy(game => game.AppId),
                    3 => games.OrderByDescending(game => game.MinutesB).ThenBy(game => game.AppId),
                    _ => games.OrderByDescending(game => game.MinutesA.HasValue && game.MinutesB.HasValue
                            ? game.MinutesA.Value / 60d + game.MinutesB.Value / 60d : (double?)null)
                        .ThenBy(game => game.AppId)
                };
                return sorted.Select(game => new MutualGameRow(game.AppId,
                    string.IsNullOrWhiteSpace(game.Name) ? game.AppId.ToString(CultureInfo.InvariantCulture) : game.Name,
                    game.MinutesA, game.MinutesB)).ToArray();
            }, token).WaitAsync(token);
            if (_filterSession.IsCurrent(generation) && ReferenceEquals(comparison, _comparison) && !_disposed)
                MutualGames = rows;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_filterSession.IsCurrent(generation) && !_disposed) FilterError = ComparisonText.Failed;
        }
        finally
        {
            if (_filterSession.IsCurrent(generation) && !_disposed) IsFiltering = false;
        }
    }

    public override void RefreshLocalization()
    {
        SideA.RefreshLocalization();
        SideB.RefreshLocalization();
        foreach (var row in MutualGames) row.RefreshLocalization();
        base.RefreshLocalization();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SideA.PropertyChanged -= SideChanged;
        SideB.PropertyChanged -= SideChanged;
        SideA.Dispose();
        SideB.Dispose();
        _comparisonSession.Dispose();
        _filterSession.Dispose();
        _operationSession.Dispose();
    }
}

/// <summary>Owns cancellation, identity and each optional data module for exactly one side.</summary>
public sealed class ComparisonSideViewModel : ObservableObject, IDisposable
{
    private readonly IProfileLookupService _profiles;
    private readonly ILibraryService _libraries;
    private readonly SearchSession _search = new();
    private SteamProfile? _profile;
    private SteamBans? _bans;
    private string _input = "";
    private string _level = ComparisonText.NA;
    private string _identityStatus = ComparisonText.IdentityHint;
    private string _levelStatus = "";
    private string _bansStatus = "";
    private string _libraryStatus = "";
    private Outcome<OwnedLibrary>? _libraryResult;
    private bool _isBusy;
    private bool _disposed;

    public ComparisonSideViewModel(string label, IProfileLookupService profiles, ILibraryService libraries)
    {
        LabelValue = label;
        _profiles = profiles;
        _libraries = libraries;
        SearchCommand = new RelayCommand(_ => _ = SearchAsync(false));
        RefreshCommand = new RelayCommand(_ => _ = SearchAsync(true));
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    private string LabelValue { get; }
    public string Label => LocalizedStatus.Read(LabelValue, "Comparison");
    public string Input { get => _input; set => Set(ref _input, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool HasProfile => _profile is not null;
    public string DisplayName => string.IsNullOrWhiteSpace(_profile?.DisplayName) ? ComparisonText.UnknownName : _profile.DisplayName;
    public string SteamId => _profile?.SteamId ?? "";
    public string Level { get => LocalizedStatus.Read(_level, "Comparison"); private set => Set(ref _level, value); }
    public string IdentityStatus { get => LocalizedStatus.Read(_identityStatus, "Comparison"); private set => Set(ref _identityStatus, value); }
    public string LevelStatus { get => LocalizedStatus.Read(_levelStatus, "Comparison"); private set => Set(ref _levelStatus, value); }
    public string BansStatus { get => LocalizedStatus.Read(_bansStatus, "Comparison"); private set => Set(ref _bansStatus, value); }
    public string LibraryStatus { get => LocalizedStatus.Read(_libraryStatus, "Comparison"); private set => Set(ref _libraryStatus, value); }
    public string Created => _profile?.CreatedAt?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? ComparisonText.NA;
    public string AccountAge => _profile?.CreatedAt is { } created && created <= DateTimeOffset.UtcNow
        ? string.Format(CultureInfo.CurrentCulture, ComparisonText.YearsFormat, (DateTimeOffset.UtcNow - created).TotalDays / 365.2425)
        : ComparisonText.NA;
    public string Games => CompleteLibrary is { } library ? library.Games.Select(game => game.AppId).Distinct().Count().ToString("N0", CultureInfo.CurrentCulture) : ComparisonText.NA;
    public string Playtime => CompleteLibrary is { } library ? ComparisonFormatting.Hours(TotalMinutes(library.Games)) : ComparisonText.NA;
    public string VacBans => _bans?.VacBanCount?.ToString(CultureInfo.CurrentCulture) ?? ComparisonText.NA;
    public string VacStatus => _bans?.VacBanned switch { true => ComparisonText.Yes, false => ComparisonText.No, _ => ComparisonText.NA };
    public string GameBans => _bans?.GameBanCount?.ToString(CultureInfo.CurrentCulture) ?? ComparisonText.NA;
    public string CommunityBan => _bans?.CommunityBanned switch { true => ComparisonText.Yes, false => ComparisonText.No, _ => ComparisonText.NA };
    public string EconomyBan => string.IsNullOrWhiteSpace(_bans?.EconomyBan) ? ComparisonText.NA : _bans.EconomyBan;
    public Outcome<OwnedLibrary>? LibraryResult { get => _libraryResult; private set { if (Set(ref _libraryResult, value)) { Notify(nameof(Games)); Notify(nameof(Playtime)); } } }
    private OwnedLibrary? CompleteLibrary => LibraryResult.HasData() && LibraryResult!.Data!.IsComplete ? LibraryResult.Data : null;
    public ICommand SearchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task SearchAsync(bool forceRefresh)
    {
        if (_disposed) return;
        var (generation, token) = _search.Begin();
        _profile = null;
        _bans = null;
        NotifyProfile();
        NotifyBans();
        Level = ComparisonText.NA;
        LevelStatus = BansStatus = "";
        LibraryResult = new(DataState.Loading);
        LibraryStatus = ComparisonText.Loading;
        IdentityStatus = ComparisonText.Searching;
        IsBusy = true;
        try
        {
            var found = await _profiles.FindAsync(Input, forceRefresh, token).WaitAsync(token);
            if (!_search.IsCurrent(generation) || _disposed) return;
            if (!found.IsReady)
            {
                IdentityStatus = Describe(found.State);
                LibraryResult = Outcome<OwnedLibrary>.Failure(found.State, "Profile unavailable.");
                LibraryStatus = Describe(found.State);
                return;
            }
            _profile = found.Data;
            NotifyProfile();
            IdentityStatus = _profile!.Visibility is 1 or 2 ? ComparisonText.ProfilePrivate : Stamp(found.UpdatedAt, found.FromCache);
            LevelStatus = BansStatus = ComparisonText.Loading;
            // Independent tasks enrich an already visible identity. Optional failure never hides it.
            await Task.WhenAll(LoadLevelAsync(_profile.SteamId, generation, forceRefresh, token),
                LoadBansAsync(_profile.SteamId, generation, forceRefresh, token),
                LoadLibraryAsync(_profile.SteamId, generation, forceRefresh, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_search.IsCurrent(generation) || _disposed) return;
            IdentityStatus = ComparisonText.Failed;
            LibraryStatus = ComparisonText.Failed;
            LibraryResult = Outcome<OwnedLibrary>.Failure(DataState.Failed, "Profile request failed.");
        }
        finally
        {
            if (_search.IsCurrent(generation) && !_disposed) IsBusy = false;
        }
    }

    private async Task LoadLevelAsync(string id, long generation, bool force, CancellationToken token)
    {
        try
        {
            var result = await _profiles.GetLevelAsync(id, force, token).WaitAsync(token);
            if (!_search.IsCurrent(generation) || _disposed) return;
            Level = result.IsReady ? result.Data!.Value.ToString(CultureInfo.CurrentCulture) : ComparisonText.NA;
            LevelStatus = result.IsReady ? Stamp(result.UpdatedAt, result.FromCache) : Describe(result.State);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (_search.IsCurrent(generation) && !_disposed) LevelStatus = ComparisonText.Failed; }
    }

    private async Task LoadBansAsync(string id, long generation, bool force, CancellationToken token)
    {
        try
        {
            var result = await _profiles.GetBansAsync(id, force, token).WaitAsync(token);
            if (!_search.IsCurrent(generation) || _disposed) return;
            _bans = result.IsReady ? result.Data : null;
            BansStatus = result.IsReady ? Stamp(result.UpdatedAt, result.FromCache) : Describe(result.State);
            NotifyBans();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (_search.IsCurrent(generation) && !_disposed) BansStatus = ComparisonText.Failed; }
    }

    private async Task LoadLibraryAsync(string id, long generation, bool force, CancellationToken token)
    {
        try
        {
            var result = await _libraries.GetLibraryAsync(id, force, token).WaitAsync(token);
            if (!_search.IsCurrent(generation) || _disposed) return;
            LibraryStatus = result.HasData() ? result.Data!.IsComplete
                ? result.State == DataState.Empty ? ComparisonText.Empty : Stamp(result.UpdatedAt, result.FromCache)
                : ComparisonText.Incomplete : Describe(result.State);
            LibraryResult = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_search.IsCurrent(generation) || _disposed) return;
            LibraryStatus = ComparisonText.Failed;
            LibraryResult = Outcome<OwnedLibrary>.Failure(DataState.Failed, "Library request failed.");
        }
    }

    public void Cancel()
    {
        if (!IsBusy) return;
        _search.Cancel();
        IsBusy = false;
        if (LevelStatus == ComparisonText.Loading) LevelStatus = ComparisonText.Cancelled;
        if (BansStatus == ComparisonText.Loading) BansStatus = ComparisonText.Cancelled;
        if (LibraryResult?.State == DataState.Loading)
        {
            LibraryStatus = ComparisonText.Cancelled;
            LibraryResult = Outcome<OwnedLibrary>.Failure(DataState.Unavailable, "Request cancelled.");
        }
        IdentityStatus = ComparisonText.Cancelled;
    }

    private void NotifyProfile()
    {
        foreach (var property in new[] { nameof(HasProfile), nameof(DisplayName), nameof(SteamId), nameof(Created), nameof(AccountAge) }) Notify(property);
    }

    private void NotifyBans()
    {
        foreach (var property in new[] { nameof(VacBans), nameof(VacStatus), nameof(GameBans), nameof(CommunityBan), nameof(EconomyBan) }) Notify(property);
    }

    private static long? TotalMinutes(IEnumerable<LibraryGame> games)
    {
        long total = 0;
        foreach (var game in games.DistinctBy(game => game.AppId))
        {
            if (game.PlaytimeMinutes is not { } minutes || minutes < 0) return null;
            try { total = checked(total + minutes); } catch (OverflowException) { return null; }
        }
        return total;
    }

    private static string Describe(DataState state) => ComparisonText.Get(state switch
    {
        DataState.Private => "Private", DataState.Empty => "Empty", DataState.InvalidInput => "InvalidInput",
        DataState.MissingKey => "MissingKey", DataState.NotFound => "NotFound", DataState.RateLimited => "RateLimited",
        DataState.Unavailable => "Unavailable", DataState.Loading => "Loading", _ => "Failed"
    });

    private static string Stamp(DateTimeOffset? date, bool cached) => date.HasValue
        ? string.Format(CultureInfo.CurrentCulture, ComparisonText.UpdatedFormat, date.Value.ToLocalTime()) +
            (cached ? " · " + ComparisonText.Cached : "")
        : ComparisonText.Ready;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _search.Dispose();
    }
}

internal static class ComparisonFormatting
{
    public static string Hours(long? minutes) => minutes is >= 0
        ? string.Format(CultureInfo.CurrentCulture, ComparisonText.HoursFormat, minutes.Value / 60d) : ComparisonText.NA;
}

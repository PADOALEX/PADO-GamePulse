using System.ComponentModel;
using System.Globalization;
using GamePulse.Core;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;

namespace GamePulse.Desktop.ViewModels;

public sealed record InsightBucketRow(PlaytimeBucket Bucket) : LocalizedRecord
{
    public string Label => InsightsText.Get("Range" + Bucket.Range);
    public string Count => Bucket.Count.ToString("N0", CultureInfo.CurrentCulture);
    public double Share => Bucket.ShareOfAvailableGames;
    public string AccessibleName => InsightsText.Format("BucketAccessible", Label, Bucket.Count, Share);
}

public sealed record InsightGameRow(int Rank, LibraryGame Game) : LocalizedRecord
{
    public uint AppId => Game.AppId;
    public string Name => string.IsNullOrWhiteSpace(Game.Name) ? InsightsText.Format("Unnamed", AppId) : Game.Name;
    public string Hours => InsightsText.Format("Hours", Game.PlaytimeMinutes!.Value / 60m);
    public string AccessibleName => InsightsText.Format("TopGameAccessible", Rank, Name, Hours);
}

/// <summary>Computes insights from Games' existing unfiltered snapshot; never makes network requests.</summary>
public sealed class InsightsViewModel : ObservableObject, IDisposable
{
    private readonly GamesViewModel _games;
    private CancellationTokenSource? _calculationCancellation;
    private Task _refreshTask = Task.CompletedTask;
    private Outcome<OwnedLibrary>? _observedSnapshot;
    private DataState _observedState;
    private bool _observedHasProfile;
    private bool _observed;
    private long _generation;
    private bool _disposed;
    private bool _isAnalyzing;
    private DataState _state = DataState.Unavailable;
    private LibraryInsightSnapshot? _snapshot;
    private IReadOnlyList<InsightBucketRow> _buckets = Array.Empty<InsightBucketRow>();
    private IReadOnlyList<InsightGameRow> _topGames = Array.Empty<InsightGameRow>();
    private InsightGameRow? _selectedGame;
    private string _localMessageKey = "";

    public InsightsViewModel(GamesViewModel games)
    {
        _games = games;
        _games.PropertyChanged += OnLibraryChanged;
        _refreshTask = RefreshAsync();
    }

    public DataState State => _state;
    public bool IsAnalyzing => _isAnalyzing;
    public bool HasData => _snapshot is not null;
    public bool IsLoading => _state == DataState.Loading || _isAnalyzing;
    public IReadOnlyList<InsightBucketRow> Buckets => _buckets;
    public IReadOnlyList<InsightGameRow> TopGames => _topGames;
    public InsightGameRow? SelectedGame
    {
        get => _selectedGame;
        set { if (Set(ref _selectedGame, value)) { _localMessageKey = ""; Notify(nameof(HasSelection)); Notify(nameof(LocalMessage)); } }
    }
    public bool HasSelection => SelectedGame is not null;
    public string LocalMessage => _localMessageKey.Length == 0 ? "" : InsightsText.Get(_localMessageKey);
    public string StatusMessage => InsightsText.Get(!_games.HasProfile ? "Idle" : IsLoading ? "Loading" : _state switch
    {
        DataState.Ready => _snapshot?.IsComplete == true ? "Ready" : "Partial",
        DataState.Empty => "Empty", DataState.Private => "Private", DataState.MissingKey => "MissingKey",
        DataState.RateLimited => "RateLimited", DataState.Failed => "Failed", _ => "Unavailable"
    });
    public string AvailableCount => _snapshot is null ? InsightsText.Get("NA") :
        InsightsText.Format("AvailableCount", _snapshot.AvailableCount, _snapshot.ReportedCount);
    public string TotalHours => _snapshot?.TotalMinutes is { } total ? InsightsText.Format("Hours", total / 60m) : InsightsText.Get("NA");
    public string KnownHours => _snapshot is null ? InsightsText.Get("NA") : InsightsText.Format("Hours", _snapshot.KnownMinutes / 60m);
    public string UnplayedCount => _snapshot?.UnplayedCount?.ToString("N0", CultureInfo.CurrentCulture) ?? InsightsText.Get("NA");
    public string UnplayedShare => _snapshot?.UnplayedShare is { } share ? InsightsText.Format("Percent", share) : InsightsText.Get("NA");
    public string TopThreeShare => _snapshot?.TopThreePlaytimeShare is { } share ? InsightsText.Format("Percent", share) : InsightsText.Get("NA");
    public string Coverage => _snapshot is null ? "" : InsightsText.Format("Coverage", _snapshot.KnownPlaytimeCount, _snapshot.AvailableCount);
    public string CoverageNote => _snapshot is null ? InsightsText.Get("CoverageHint") :
        InsightsText.Get(_snapshot.TotalMinutes.HasValue ? "CompleteNote" : "IncompleteNote");
    public string TopCount => _snapshot is null ? "" : InsightsText.Format("TopCount", _topGames.Count, _snapshot.KnownPlaytimeCount);
    public string UpdatedAt => _observedSnapshot?.UpdatedAt is { } time
        ? InsightsText.Format(_observedSnapshot.FromCache ? "CachedAt" : "UpdatedAt", time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";

    public Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        var result = _games.OwnedSnapshot;
        var state = _games.OwnedState;
        var hasProfile = _games.HasProfile;
        if (_observed && ReferenceEquals(result, _observedSnapshot) && state == _observedState && hasProfile == _observedHasProfile)
            return _refreshTask;
        _observed = true;
        _observedSnapshot = result;
        _observedState = state;
        _observedHasProfile = hasProfile;
        _calculationCancellation?.Cancel();
        _calculationCancellation?.Dispose();
        _calculationCancellation = new CancellationTokenSource();
        var token = _calculationCancellation.Token;
        var generation = ++_generation;
        _snapshot = null;
        _buckets = Array.Empty<InsightBucketRow>();
        _topGames = Array.Empty<InsightGameRow>();
        SelectedGame = null;
        _localMessageKey = "";
        _state = state;
        _isAnalyzing = result.HasData();
        NotifyAll();
        _refreshTask = CalculateAsync();
        return _refreshTask;

        async Task CalculateAsync()
        {
            if (!result.HasData()) return;
            try
            {
                var snapshot = await Task.Run(() => LibraryInsights.Calculate(result!.Data!, token), token);
                if (_disposed || generation != _generation || token.IsCancellationRequested) return;
                _snapshot = snapshot;
                _buckets = snapshot.Buckets.Select(bucket => new InsightBucketRow(bucket)).ToArray();
                _topGames = snapshot.TopGames.Select((game, index) => new InsightGameRow(index + 1, game)).ToArray();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (generation == _generation && !_disposed) _state = DataState.Failed;
            }
            finally
            {
                if (!_disposed && generation == _generation) { _isAnalyzing = false; NotifyAll(); }
            }
        }
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GamesViewModel.OwnedState) or nameof(GamesViewModel.OwnedSnapshot) or nameof(GamesViewModel.HasProfile))
            _ = RefreshAsync();
    }
    public void ShowLocalMessage(string key) { _localMessageKey = key; Notify(nameof(LocalMessage)); }
    public override void RefreshLocalization()
    {
        // Translate presentation properties in place so ItemsSource and two-way selection
        // remain stable, including while this page is visible.
        foreach (var bucket in _buckets) bucket.RefreshLocalization();
        foreach (var game in _topGames) game.RefreshLocalization();
        base.RefreshLocalization();
        Notify(nameof(SelectedGame));
    }
    private void NotifyAll()
    {
        foreach (var name in new[] { nameof(State), nameof(IsAnalyzing), nameof(IsLoading), nameof(HasData), nameof(Buckets),
            nameof(TopGames), nameof(HasSelection), nameof(LocalMessage), nameof(StatusMessage), nameof(AvailableCount),
            nameof(TotalHours), nameof(KnownHours), nameof(UnplayedCount), nameof(UnplayedShare), nameof(TopThreeShare),
            nameof(Coverage), nameof(CoverageNote), nameof(TopCount), nameof(UpdatedAt) }) Notify(name);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _games.PropertyChanged -= OnLibraryChanged;
        _calculationCancellation?.Cancel();
        _calculationCancellation?.Dispose();
    }
}

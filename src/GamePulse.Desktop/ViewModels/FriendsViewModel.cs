using System.Globalization;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using GamePulse.Core;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;

namespace GamePulse.Desktop.ViewModels;

public sealed record FriendRow(SteamFriend Friend) : LocalizedRecord
{
    public string SteamId => Friend.SteamId;
    public string Name => FriendQuery.Name(Friend);
    public bool HasSummary => FriendQuery.HasSummary(Friend);
    public bool IsOnline => FriendQuery.IsOnline(Friend);
    public string Initial => Name.Length > 0 ? StringInfo.GetNextTextElement(Name).ToUpperInvariant() : "?";
    public string Presence => FriendsText.Get(HasSummary ? Friend.Profile!.PersonaState switch
    {
        0 => "Offline", 1 => "Online", 2 => "Busy", 3 => "Away", 4 => "Snooze",
        5 => "LookingToTrade", 6 => "LookingToPlay", _ => "UnknownPresence"
    } : "UnknownPresence");
    public string CurrentGame => HasSummary && !string.IsNullOrWhiteSpace(Friend.Profile!.CurrentGame)
        ? Friend.Profile.CurrentGame : FriendsText.Get("NA");
    public string SummaryStatus => FriendsText.Get(HasSummary ? "SummaryAvailable" : Friend.SummaryState switch
    {
        DataState.RateLimited => "SummaryRateLimited",
        DataState.MissingKey => "SummaryMissingKey",
        DataState.Failed => "SummaryFailed",
        _ => "SummaryMissing"
    });
    public string Subtitle => HasSummary && !string.IsNullOrWhiteSpace(Friend.Profile!.CurrentGame)
        ? FriendsText.Format("Playing", Friend.Profile.CurrentGame) : SteamId;
    public string Visibility => FriendsText.Get(HasSummary ? Friend.Profile!.Visibility switch
    { 1 => "PrivateVisibility", 2 => "FriendsVisibility", 3 => "PublicVisibility", _ => "NA" } : "NA");
    public string FriendsSince => Date(Friend.FriendsSince);
    public string CreatedAt => Date(HasSummary ? Friend.Profile!.CreatedAt : null);
    public string LastLogoff => Date(HasSummary ? Friend.Profile!.LastLogoff : null);
    public string? AvatarUrl => HasSummary ? Friend.Profile!.AvatarUrl : null;
    public string AccessibleName => FriendsText.Format("AccessibleFriend", Name, SteamId, Presence, SummaryStatus);
    private static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? FriendsText.Get("NA");
}

/// <summary>Session-scoped directory with cancellable queries and shared, bounded avatar loading.</summary>
public sealed class FriendsViewModel : ObservableObject, IDisposable
{
    private readonly IFriendsService _service;
    private readonly IAvatarLoader _avatars;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _filterCancellation;
    private CancellationTokenSource? _avatarCancellation;
    private long _generation, _filterGeneration, _selectionGeneration;
    private bool _disposed, _hasProfile, _cancelled, _isFiltering, _publishingRows;
    private string _query = "", _localMessage = "";
    private int _selectedSortIndex;
    private bool _onlineOnly;
    private DataState _state = DataState.Unavailable;
    private Outcome<FriendDirectory>? _directory;
    private IReadOnlyList<FriendRow> _rows = Array.Empty<FriendRow>();
    private FriendRow? _selectedFriend;
    private Bitmap? _selectedAvatar;

    public FriendsViewModel(IFriendsService service, IAvatarLoader avatars)
    {
        _service = service;
        _avatars = avatars;
        ResetFiltersCommand = new RelayCommand(_ => ResetFilters());
    }

    public event Action<FriendAction>? ActionRequested;
    public IAvatarLoader RowThumbnailLoader => _avatars;
    public IReadOnlyList<LocalizedText> SortOptions { get; } = [LocalizedResources.For("Friends").Text("SortStatus"), LocalizedResources.For("Friends").Text("SortName"),
            LocalizedResources.For("Friends").Text("SortNewest"), LocalizedResources.For("Friends").Text("SortOldest")];
    public ICommand ResetFiltersCommand { get; }
    public IReadOnlyList<FriendRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public string Query { get => _query; set { if (Set(ref _query, value)) QueueFilters(); } }
    public int SelectedSortIndex { get => _selectedSortIndex; set { if (Set(ref _selectedSortIndex, value)) QueueFilters(); } }
    public bool OnlineOnly { get => _onlineOnly; set { if (Set(ref _onlineOnly, value)) QueueFilters(); } }
    public DataState State => _state;
    public bool IsBusy => State == DataState.Loading;
    public bool IsFiltering { get => _isFiltering; private set { if (Set(ref _isFiltering, value)) Notify(nameof(StateMessage)); } }
    public bool HasSelection => SelectedFriend is not null;
    public FriendRow? SelectedFriend
    {
        get => _selectedFriend;
        set
        {
            if (_disposed || _publishingRows || !Set(ref _selectedFriend, value)) return;
            Notify(nameof(HasSelection));
            LocalMessage = "";
            AvatarLoadingTask = LoadSelectedAvatarAsync(value);
        }
    }
    public Bitmap? SelectedAvatar { get => _selectedAvatar; private set => Set(ref _selectedAvatar, value); }
    public Task AvatarLoadingTask { get; private set; } = Task.CompletedTask;
    public string LocalMessage { get => LocalizedStatus.Read(_localMessage, "Friends"); private set => Set(ref _localMessage, value); }
    public string DirectoryCount => HasData ? AllFriends.Count.ToString("N0") : FriendsText.Get("NA");
    public string LoadedCount => HasData ? AllFriends.Count(FriendQuery.HasSummary).ToString("N0") : FriendsText.Get("NA");
    public string MissingCount => HasData ? AllFriends.Count(x => !FriendQuery.HasSummary(x)).ToString("N0") : FriendsText.Get("NA");
    public string ShownCount => HasData ? FriendsText.Format("Shown", Rows.Count, AllFriends.Count) : "";
    public string Completeness => HasData && !_directory!.Data!.SummariesComplete
        ? FriendsText.Get("Partial") : "";
    public bool HasCompleteness => Completeness.Length > 0;
    public string UpdatedAt => _directory?.UpdatedAt is { } date
        ? FriendsText.Format(_directory.FromCache ? "Cached" : "Updated", date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "";
    public string StateMessage => FriendsText.Get(!_hasProfile ? "Idle" : _cancelled ? "Cancelled" : State switch
    {
        DataState.Loading => "Loading", DataState.Private => "Private", DataState.Empty => "Empty",
        DataState.MissingKey => "MissingKey", DataState.RateLimited => "RateLimited", DataState.Failed => "Failed",
        DataState.Ready when IsFiltering => "Filtering",
        DataState.Ready when Rows.Count == 0 && AllFriends.Count > 0 => "NoMatches",
        DataState.Ready => "Ready", _ => "Unavailable"
    });
    private bool HasData => _directory is { State: DataState.Ready or DataState.Empty, Data: not null };
    private IReadOnlyList<SteamFriend> AllFriends => HasData ? _directory!.Data!.Friends : Array.Empty<SteamFriend>();

    public async Task LoadAsync(string steamId, bool force, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clear();
        _hasProfile = true;
        _state = DataState.Loading;
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = _loadCancellation.Token;
        var generation = _generation;
        NotifyAll();
        try
        {
            var result = await _service.GetFriendsAsync(steamId, force, cancellation).WaitAsync(cancellation);
            if (!Current(generation, cancellation)) return;
            _directory = result;
            _state = result.State;
            NotifyAll();
            await ApplyFiltersAsync();
        }
        catch (OperationCanceledException)
        {
            if (generation != _generation) return;
            _state = DataState.Unavailable;
            _cancelled = true;
        }
        catch (Exception)
        {
            if (generation != _generation) return;
            _state = DataState.Failed;
        }
        finally
        {
            if (generation == _generation)
            {
                if (cancellation.IsCancellationRequested && _state == DataState.Loading)
                { _state = DataState.Unavailable; _cancelled = true; }
                NotifyAll();
            }
        }
    }

    public Task FilteringTask { get; private set; } = Task.CompletedTask;
    public Task ApplyFiltersAsync() => FilteringTask = FilterAsync(false);
    private void QueueFilters() => FilteringTask = FilterAsync(true);
    private async Task FilterAsync(bool debounce)
    {
        if (_disposed) return;
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = new CancellationTokenSource();
        var token = _filterCancellation.Token;
        var generation = _generation;
        var filterGeneration = ++_filterGeneration;
        var friends = AllFriends;
        var sort = SelectedSortIndex is >= 0 and <= 3 ? (FriendSort)SelectedSortIndex : FriendSort.Status;
        var filter = new FriendFilter(Query, sort, OnlineOnly);
        IsFiltering = true;
        try
        {
            if (debounce) await Task.Delay(150, token);
            var rows = await Task.Run(() => FriendQuery.Apply(friends, filter, token)
                .Select(x => new FriendRow(x)).ToArray(), token);
            if (!IsCurrent()) return;
            var selectedId = SelectedFriend?.SteamId;
            // Replacing ItemsSource can transiently clear the two-way selection in Avalonia.
            // Retain the existing mini-profile and its caller-owned image until the final rows
            // actually exclude the selected friend.
            _publishingRows = true;
            try { Rows = rows; }
            finally { _publishingRows = false; }
            // Keep a mini-profile only while that friend is visible in the filtered directory.
            SelectedFriend = selectedId is null ? null : rows.FirstOrDefault(x => x.SteamId == selectedId);
            Notify(nameof(SelectedFriend));
            Notify(nameof(ShownCount));
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsCurrent()) return;
            Rows = Array.Empty<FriendRow>();
            SelectedFriend = null;
            LocalMessage = FriendsText.Get("FilterFailed");
            Notify(nameof(ShownCount));
        }
        finally
        {
            if (generation == _generation && filterGeneration == _filterGeneration) IsFiltering = false;
        }
        bool IsCurrent() => Current(generation, token) && filterGeneration == _filterGeneration;
    }

    private async Task LoadSelectedAvatarAsync(FriendRow? row)
    {
        var generation = ++_selectionGeneration;
        _avatarCancellation?.Cancel();
        _avatarCancellation?.Dispose();
        _avatarCancellation = new CancellationTokenSource();
        var token = _avatarCancellation.Token;
        var previous = SelectedAvatar;
        SelectedAvatar = null;
        previous?.Dispose();
        if (_disposed || row?.AvatarUrl is not { Length: > 0 } url) return;
        try
        {
            var bitmap = await _avatars.LoadAsync(url, token);
            if (_disposed || token.IsCancellationRequested || generation != _selectionGeneration)
            { bitmap?.Dispose(); return; }
            SelectedAvatar = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { } // An optional avatar must not hide available directory data.
    }

    public void RequestAction(FriendDestination destination)
    {
        if (_disposed || SelectedFriend is not { } friend || !Enum.IsDefined(destination)) return;
        var parsed = ProfileInput.Parse(friend.SteamId);
        if (!parsed.IsReady || parsed.Data?.Kind != ProfileInputKind.SteamId || parsed.Data.Value != friend.SteamId) return;
        ActionRequested?.Invoke(new(friend.SteamId, destination));
    }
    public void ShowLocalMessage(string key) => LocalMessage = FriendsText.Get(key);
    private void ResetFilters()
    {
        _query = ""; _onlineOnly = false; _selectedSortIndex = 0;
        Notify(nameof(Query)); Notify(nameof(OnlineOnly)); Notify(nameof(SelectedSortIndex));
        QueueFilters();
    }
    public void Clear()
    {
        ++_generation; ++_filterGeneration; ++_selectionGeneration;
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = null;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _filterCancellation = null;
        _avatarCancellation?.Cancel(); _avatarCancellation?.Dispose(); _avatarCancellation = null;
        _hasProfile = false; _cancelled = false; _directory = null; _state = DataState.Unavailable;
        Rows = Array.Empty<FriendRow>(); SelectedFriend = null;
        var avatar = SelectedAvatar; SelectedAvatar = null; avatar?.Dispose();
        IsFiltering = false; LocalMessage = "";
        NotifyAll();
    }
    private bool Current(long generation, CancellationToken token) => !_disposed && generation == _generation && !token.IsCancellationRequested;
    private void NotifyAll()
    {
        foreach (var property in new[] { nameof(State), nameof(IsBusy), nameof(StateMessage), nameof(DirectoryCount),
            nameof(LoadedCount), nameof(MissingCount), nameof(ShownCount), nameof(Completeness), nameof(HasCompleteness), nameof(UpdatedAt) }) Notify(property);
    }
    public override void RefreshLocalization()
    {
        foreach (var row in Rows) row.RefreshLocalization();
        // A selected record may be value-equal to a newly filtered row while the mini-profile
        // still observes its original instance. Refresh that presentation without selecting again.
        if (SelectedFriend is { } selected && !Rows.Any(row => ReferenceEquals(row, selected)))
            selected.RefreshLocalization();
        Notify(nameof(SelectedFriend));
        base.RefreshLocalization();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear(); _disposed = true;
        _avatarCancellation?.Dispose(); _avatarCancellation = null;
        ActionRequested = null;
    }
}

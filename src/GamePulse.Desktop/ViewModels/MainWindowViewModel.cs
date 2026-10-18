using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using GamePulse.Core;
using GamePulse.Desktop.Resources;
using GamePulse.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace GamePulse.Desktop.ViewModels;

public sealed record RecentProfile(string SteamId, string Name);

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IProfileLookupService _profiles;
    private readonly AvatarService _avatars;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly SearchSession _search = new();
    private string _searchInput = "";
    private bool _isBusy;
    private bool _isSetupRequired;
    private bool _showSettings;
    private SteamProfile? _profile;
    private Bitmap? _avatar;
    private string _statusTitle;
    private string _statusMessage;
    private string _level = UiText.Get("NA");
    private string _levelStatus = "";
    private string _bansStatus = "";
    private SteamBans? _bans;
    private string _updatedAt = "";
    private bool _disposed;
    private int _selectedPage;
    private bool _isExportBusy;
    private string _actionMessageKey = "";
    private object[] _actionMessageArguments = [];

    public MainWindowViewModel(IProfileLookupService profiles, AvatarService avatars,
        ILogger<MainWindowViewModel> logger, GamesViewModel? games = null, ComparisonViewModel? comparison = null,
        FriendsViewModel? friends = null, AchievementsViewModel? achievements = null, InventoryViewModel? inventory = null,
        InsightsViewModel? insights = null, bool? apiKeyConfigured = null)
    {
        _profiles = profiles;
        _avatars = avatars;
        _logger = logger;
        Games = games;
        Comparison = comparison;
        Friends = friends;
        Achievements = achievements;
        Inventory = inventory;
        Insights = insights ?? (games is null ? null : new InsightsViewModel(games));
        if (Friends is not null) Friends.ActionRequested += OnFriendActionRequested;
        ShowOverviewCommand = new RelayCommand(_ => Navigate(0));
        ShowGamesCommand = new RelayCommand(_ => Navigate(1));
        ShowComparisonCommand = new RelayCommand(_ => Navigate(2));
        ShowFriendsCommand = new RelayCommand(_ => Navigate(3));
        ShowAchievementsCommand = new RelayCommand(_ => Navigate(4));
        ShowInventoryCommand = new RelayCommand(_ => Navigate(5));
        ShowInsightsCommand = new RelayCommand(_ => Navigate(6));
        _isSetupRequired = !(apiKeyConfigured ?? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEPULSE_STEAM_API_KEY")));
        _statusTitle = UiText.StartTitle;
        _statusMessage = UiText.StartMessage;
        SearchCommand = new RelayCommand(_ => _ = SearchAsync(forceRefresh: false));
        RefreshCommand = new RelayCommand(_ => _ = SearchAsync(forceRefresh: true));
        CancelCommand = new RelayCommand(_ => Cancel());
        OpenRecentCommand = new RelayCommand(value =>
        {
            if (value is not RecentProfile recent) return;
            SearchInput = recent.SteamId;
            _ = SearchAsync(false);
        });
        ToggleSettingsCommand = new RelayCommand(_ => { Navigate(0); ShowSettings = !ShowSettings; });
        ToggleThemeCommand = new RelayCommand(_ =>
        {
            if (Application.Current is { } application)
                ThemeManager.Cycle(application);
        });
    }

    public string AppTitle
    {
        get
        {
            var title = Environment.GetEnvironmentVariable("GAMEPULSE_APP_TITLE");
            return !string.IsNullOrWhiteSpace(title) && title.Length <= 80 && !title.Any(char.IsControl)
                ? title : UiText.ProductName;
        }
    }
    public string SearchInput { get => _searchInput; set => Set(ref _searchInput, value); }
    public string AppVersion => "v" + typeof(MainWindowViewModel).Assembly.GetName().Version!.ToString(3);
    public bool CanExportPage => IsGames || IsComparison || IsInventory;
    public bool CanStartExport => CanExportPage && !IsExportBusy;
    public bool IsExportBusy { get => _isExportBusy; set { if (Set(ref _isExportBusy, value)) Notify(nameof(CanStartExport)); } }
    public bool HasActionMessage => _actionMessageKey.Length > 0;
    public string ActionMessage => HasActionMessage ? string.Format(CultureInfo.CurrentCulture, UiText.Get(_actionMessageKey), _actionMessageArguments) : "";
    public void ShowActionMessage(string key, params object[] arguments)
    {
        _actionMessageKey = key;
        _actionMessageArguments = arguments;
        Notify(nameof(ActionMessage)); Notify(nameof(HasActionMessage));
    }
    public GamesViewModel? Games { get; }
    public ComparisonViewModel? Comparison { get; }
    public FriendsViewModel? Friends { get; }
    public AchievementsViewModel? Achievements { get; }
    public InventoryViewModel? Inventory { get; }
    public InsightsViewModel? Insights { get; }
    public bool IsOverview => _selectedPage == 0;
    public bool IsGames => _selectedPage == 1;
    public bool IsComparison => _selectedPage == 2;
    public bool IsFriends => _selectedPage == 3;
    public bool IsAchievements => _selectedPage == 4;
    public bool IsInventory => _selectedPage == 5;
    public bool IsInsights => _selectedPage == 6;
    public bool IsExplorer => !IsComparison;
    public ICommand ShowOverviewCommand { get; }
    public ICommand ShowGamesCommand { get; }
    public ICommand ShowComparisonCommand { get; }
    public ICommand ShowFriendsCommand { get; }
    public ICommand ShowAchievementsCommand { get; }
    public ICommand ShowInventoryCommand { get; }
    public ICommand ShowInsightsCommand { get; }
    private void Navigate(int page)
    {
        _selectedPage = page;
        ShowActionMessage("");
        Notify(nameof(CanExportPage)); Notify(nameof(CanStartExport));
        foreach (var property in new[] { nameof(IsOverview), nameof(IsGames), nameof(IsComparison), nameof(IsExplorer), nameof(IsFriends), nameof(IsAchievements), nameof(IsInventory), nameof(IsInsights) }) Notify(property);
    }
    public async Task NavigateFromFriendAsync(FriendAction action)
    {
        if (_disposed || ProfileInput.Parse(action.SteamId).Data is not { Kind: ProfileInputKind.SteamId } parsed ||
            parsed.Value != action.SteamId) return;
        if (action.Destination == FriendDestination.Explorer)
        {
            Navigate(0);
            SearchInput = action.SteamId;
            await SearchAsync(false);
        }
        else if (Comparison is not null && action.Destination is FriendDestination.CompareA or FriendDestination.CompareB)
        {
            Navigate(2);
            var side = action.Destination == FriendDestination.CompareA ? Comparison.SideA : Comparison.SideB;
            side.Input = action.SteamId;
            await side.SearchAsync(false);
        }
    }
    private async void OnFriendActionRequested(FriendAction action)
    {
        try { await NavigateFromFriendAsync(action); }
        catch (Exception) { _logger.LogWarning("Friend navigation failed; request details suppressed."); }
    }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsSetupRequired { get => _isSetupRequired; private set => Set(ref _isSetupRequired, value); }
    public bool ShowSettings { get => _showSettings; set => Set(ref _showSettings, value); }
    public bool HasProfile => _profile is not null;
    public string StatusTitle { get => LocalizedStatus.Read(_statusTitle, "Strings"); private set => Set(ref _statusTitle, value); }
    public string StatusMessage { get => LocalizedStatus.Read(_statusMessage, "Strings"); private set => Set(ref _statusMessage, value); }
    public Bitmap? Avatar { get => _avatar; private set => Set(ref _avatar, value); }
    public string DisplayName => string.IsNullOrWhiteSpace(_profile?.DisplayName) ? UiText.Get("UnknownName") : _profile.DisplayName;
    public string SteamId => _profile?.SteamId ?? "";
    public string ProfileLink => $"https://steamcommunity.com/profiles/{SteamId}";
    public string Presence => _profile?.PersonaState switch
    {
        0 => UiText.Get("Offline"), 1 => UiText.Get("Online"), 2 => UiText.Get("Busy"),
        3 => UiText.Get("Away"), 4 => UiText.Get("Snooze"), 5 => UiText.Get("LookingToTrade"),
        6 => UiText.Get("LookingToPlay"), null => UiText.Get("NA"), _ => UiText.Get("UnknownPresence")
    };
    public string Visibility => _profile?.Visibility switch
    {
        3 => UiText.Get("Public"), 1 or 2 => UiText.Get("LimitedVisibility"), _ => UiText.Get("NA")
    };
    public string CreatedAt => Date(_profile?.CreatedAt);
    public string LastLogoff => Date(_profile?.LastLogoff);
    public string CurrentGame => string.IsNullOrWhiteSpace(_profile?.CurrentGame) ? UiText.Get("NA") : _profile.CurrentGame;
    public string Level { get => LocalizedStatus.Read(_level, "Strings"); private set => Set(ref _level, value); }
    public string LevelStatus { get => LocalizedStatus.Read(_levelStatus, "Strings"); private set => Set(ref _levelStatus, value); }
    public string BansStatus { get => LocalizedStatus.Read(_bansStatus, "Strings"); private set => Set(ref _bansStatus, value); }
    public string VacBans => _bans?.VacBanCount?.ToString(CultureInfo.CurrentCulture) ?? UiText.Get("NA");
    public string VacStatus => _bans?.VacBanned switch { true => UiText.Get("Yes"), false => UiText.Get("No"), _ => UiText.Get("NA") };
    public string GameBans => _bans?.GameBanCount?.ToString(CultureInfo.CurrentCulture) ?? UiText.Get("NA");
    public string CommunityBan => _bans?.CommunityBanned switch { true => UiText.Get("Yes"), false => UiText.Get("No"), _ => UiText.Get("NA") };
    public string EconomyBan => string.IsNullOrWhiteSpace(_bans?.EconomyBan) ? UiText.Get("NA") : _bans.EconomyBan;
    public string DaysSinceBan => _bans?.DaysSinceLastBan?.ToString(CultureInfo.CurrentCulture) ?? UiText.Get("NA");
    public string UpdatedAt { get => LocalizedStatus.Read(_updatedAt, "Strings"); private set => Set(ref _updatedAt, value); }
    public ObservableCollection<RecentProfile> RecentProfiles { get; } = [];
    public ICommand SearchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleSettingsCommand { get; }

    public async Task SearchAsync(bool forceRefresh)
    {
        if (_disposed) return;
        if (IsComparison) Navigate(0);
        var (generation, token) = _search.Begin();
        ResetProfile();
        IsBusy = true;
        StatusTitle = UiText.Get("SearchingTitle");
        StatusMessage = UiText.Get("SearchingMessage");
        try
        {
            var result = await _profiles.FindAsync(SearchInput, forceRefresh, token);
            if (!_search.IsCurrent(generation)) return;
            if (!result.IsReady)
            {
                if (result.State == DataState.MissingKey) IsSetupRequired = true;
                StatusTitle = UiText.Get("RequestFailedTitle");
                StatusMessage = Describe(result.State);
                // Lookup errors belong to the shared summary/status area, visible from any feature page.
                Navigate(0);
                return;
            }
            IsSetupRequired = false;
            _profile = result.Data;
            Inventory?.SetProfile(_profile!.SteamId);
            NotifyProfile();
            LevelStatus = BansStatus = UiText.Get("Loading");
            StatusTitle = UiText.Get("ReadyTitle");
            StatusMessage = UiText.Get("ReadyMessage");
            UpdatedAt = Stamp(result.UpdatedAt, result.FromCache);
            RememberProfile(_profile!);
            // Identity is already visible; optional failures do not replace or hide it.
            await Task.WhenAll(
                LoadLevelAsync(_profile!.SteamId, generation, forceRefresh, token),
                LoadBansAsync(_profile.SteamId, generation, forceRefresh, token),
                LoadAvatarAsync(_profile.AvatarUrl, generation, token),
                Games?.LoadAsync(_profile.SteamId, forceRefresh, token) ?? Task.CompletedTask,
                Friends?.LoadAsync(_profile.SteamId, forceRefresh, token) ?? Task.CompletedTask,
                Achievements?.LoadAsync(_profile.SteamId, forceRefresh, token) ?? Task.CompletedTask);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Exception text/objects can contain URLs with API query keys. Log only static events.
            _logger.LogWarning("Profile lookup ended with an unexpected failure; details suppressed.");
            if (_search.IsCurrent(generation))
            {
                StatusTitle = UiText.Get("RequestFailedTitle");
                StatusMessage = UiText.Get("Failed");
                Navigate(0);
            }
        }
        finally
        {
            if (_search.IsCurrent(generation)) IsBusy = false;
        }
    }

    public void Cancel()
    {
        if (IsComparison) Comparison?.Cancel();
        if (IsAchievements) Achievements?.CancelSelected();
        if (IsInventory) Inventory?.Cancel();
        if (!IsBusy) return;
        _search.Cancel();
        IsBusy = false;
        if (LevelStatus == UiText.Get("Loading")) LevelStatus = UiText.Get("Cancelled");
        if (BansStatus == UiText.Get("Loading")) BansStatus = UiText.Get("Cancelled");
        StatusTitle = UiText.Get("CancelledTitle");
        StatusMessage = UiText.Get("CancelledMessage");
    }

    public void ShowLocalMessage(string key) => StatusMessage = UiText.Get(key);

    public override void RefreshLocalization()
    {
        if (_disposed) return;
        base.RefreshLocalization();
        Games?.RefreshLocalization();
        Comparison?.RefreshLocalization();
        Friends?.RefreshLocalization();
        Achievements?.RefreshLocalization();
        Inventory?.RefreshLocalization();
        Insights?.RefreshLocalization();
    }

    private async Task LoadLevelAsync(string steamId, long generation, bool force, CancellationToken token)
    {
        try
        {
            var result = await _profiles.GetLevelAsync(steamId, force, token);
            if (!_search.IsCurrent(generation)) return;
            Level = result.IsReady ? result.Data!.Value.ToString(CultureInfo.CurrentCulture) : UiText.Get("NA");
            LevelStatus = result.IsReady ? Stamp(result.UpdatedAt, result.FromCache) : Describe(result.State);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_search.IsCurrent(generation)) LevelStatus = UiText.Get("Failed");
        }
    }

    private async Task LoadBansAsync(string steamId, long generation, bool force, CancellationToken token)
    {
        try
        {
            var result = await _profiles.GetBansAsync(steamId, force, token);
            if (!_search.IsCurrent(generation)) return;
            _bans = result.IsReady ? result.Data : null;
            NotifyBans();
            BansStatus = result.IsReady ? Stamp(result.UpdatedAt, result.FromCache) : Describe(result.State);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_search.IsCurrent(generation)) BansStatus = UiText.Get("Failed");
        }
    }

    private async Task LoadAvatarAsync(string? url, long generation, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await _avatars.LoadAsync(url, token);
            if (!_search.IsCurrent(generation)) { bitmap?.Dispose(); return; }
            Avatar = bitmap;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { bitmap?.Dispose(); }
    }

    private void ResetProfile()
    {
        Games?.Clear();
        Friends?.Clear();
        Achievements?.Clear();
        Inventory?.Clear();
        _profile = null;
        _bans = null;
        var old = Avatar;
        Avatar = null;
        old?.Dispose();
        UpdatedAt = "";
        Level = UiText.Get("NA");
        LevelStatus = BansStatus = "";
        NotifyProfile();
        NotifyBans();
    }

    private void NotifyProfile()
    {
        foreach (var property in new[] { nameof(HasProfile), nameof(DisplayName), nameof(SteamId), nameof(ProfileLink),
                     nameof(Presence), nameof(Visibility), nameof(CreatedAt), nameof(LastLogoff), nameof(CurrentGame) }) Notify(property);
    }

    private void NotifyBans()
    {
        foreach (var property in new[] { nameof(VacBans), nameof(VacStatus), nameof(GameBans), nameof(CommunityBan), nameof(EconomyBan), nameof(DaysSinceBan) }) Notify(property);
    }

    private void RememberProfile(SteamProfile profile)
    {
        var existing = RecentProfiles.FirstOrDefault(x => x.SteamId == profile.SteamId);
        if (existing is not null) RecentProfiles.Remove(existing);
        RecentProfiles.Insert(0, new(profile.SteamId, string.IsNullOrWhiteSpace(profile.DisplayName) ? UiText.Get("UnknownName") : profile.DisplayName));
        while (RecentProfiles.Count > 8) RecentProfiles.RemoveAt(RecentProfiles.Count - 1);
    }

    private static string Describe(DataState state) => UiText.Get(state switch
    {
        DataState.Private => "Private", DataState.Empty => "Empty", DataState.NotFound => "NotFound",
        DataState.InvalidInput => "InvalidInput", DataState.MissingKey => "MissingKey",
        DataState.RateLimited => "RateLimited", DataState.Unavailable => "Unavailable", _ => "Failed"
    });

    private static string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? UiText.Get("NA");
    private static string Stamp(DateTimeOffset? date, bool cached) =>
        $"{UiText.Updated} {Date(date)}{(cached ? " · " + UiText.Get("Cached") : "")}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _search.Dispose();
        Insights?.Dispose();
        Games?.Dispose();
        Comparison?.Dispose();
        if (Friends is not null) Friends.ActionRequested -= OnFriendActionRequested;
        Friends?.Dispose();
        Achievements?.Dispose();
        Inventory?.Dispose();
        Avatar?.Dispose();
        Avatar = null;
    }
}

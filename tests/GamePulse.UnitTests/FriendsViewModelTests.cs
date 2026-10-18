using Avalonia.Media.Imaging;
using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Xunit;

namespace GamePulse.UnitTests;

public class FriendsViewModelTests
{
    [Theory]
    [InlineData(DataState.RateLimited, "temporarily limited")]
    [InlineData(DataState.MissingKey, "API key")]
    [InlineData(DataState.Failed, "could not be loaded")]
    [InlineData(DataState.Unavailable, "unavailable")]
    [InlineData(DataState.Private, "unavailable")]
    public void MissingSummaryPreservesKnownFailureCauseWithoutInferringPresence(DataState state, string expected)
    {
        var row = new FriendRow(new("76561198000000001", null, null, state));
        Assert.Contains(expected, row.SummaryStatus);
        Assert.Equal("Status unavailable", row.Presence);
        Assert.Equal("N/A", row.Visibility);
    }

    [Fact]
    public async Task PartialSummaryDirectoryKeepsMissingIdsAndHonestMetrics()
    {
        var missing = new SteamFriend("76561198000000003", null, null, DataState.Unavailable);
        using var vm = Create(new Friends { Get = (_, _, _) => Task.FromResult(Outcome<FriendDirectory>.Ready(new([FriendQueryTests.Friend(1, "Available", 0), missing], false, 1))) });
        await vm.LoadAsync("A", false, default);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("2", vm.DirectoryCount); Assert.Equal("1", vm.LoadedCount); Assert.Equal("1", vm.MissingCount);
        Assert.Contains("Some summaries", vm.Completeness);
        Assert.True(vm.HasCompleteness);
        var missingRow = vm.Rows.Single(x => x.SteamId == missing.SteamId);
        Assert.Equal(missing.SteamId, missingRow.Name);
        Assert.Equal("Status unavailable", missingRow.Presence);
        Assert.Equal("N/A", missingRow.Visibility); Assert.Equal("N/A", missingRow.CreatedAt);
        vm.Query = missing.SteamId; await vm.ApplyFiltersAsync();
        Assert.Equal(missing.SteamId, Assert.Single(vm.Rows).SteamId);
    }

    [Theory]
    [InlineData(DataState.Private)]
    [InlineData(DataState.Unavailable)]
    [InlineData(DataState.RateLimited)]
    [InlineData(DataState.MissingKey)]
    [InlineData(DataState.Failed)]
    public async Task NonAccessibleDirectoryNeverShowsKnownZero(DataState state)
    {
        using var vm = Create(new Friends { Get = (_, _, _) => Task.FromResult(Outcome<FriendDirectory>.Failure(state, "Safe message")) });
        await vm.LoadAsync("A", false, default);
        Assert.Equal(state, vm.State); Assert.Empty(vm.Rows);
        Assert.Equal("N/A", vm.DirectoryCount); Assert.Equal("N/A", vm.LoadedCount); Assert.Equal("N/A", vm.MissingCount);
        Assert.DoesNotContain("empty friend", vm.StateMessage);
    }

    [Fact]
    public async Task ActuallyEmptyDirectoryShowsKnownZerosAndEmptyState()
    {
        using var vm = Create(new Friends { Get = (_, _, _) => Task.FromResult(new Outcome<FriendDirectory>(DataState.Empty, new([], true, 0))) });
        await vm.LoadAsync("A", false, default);
        Assert.Equal("0", vm.DirectoryCount); Assert.Equal("0", vm.LoadedCount); Assert.Equal("0", vm.MissingCount);
        Assert.Contains("empty friend", vm.StateMessage);
    }

    [Fact]
    public async Task CancellationAndNewProfileSuppressNonCooperativeOlderResponse()
    {
        var old = Pending<Outcome<FriendDirectory>>(); CancellationToken oldToken = default;
        using var vm = Create(new Friends { Get = (id, _, token) =>
        { if (id == "A") { oldToken = token; return old.Task; } return Task.FromResult(Directory(FriendQueryTests.Friend(2, "New profile", 1))); } });
        var first = vm.LoadAsync("A", false, default);
        Assert.True(vm.IsBusy); Assert.Equal("N/A", vm.DirectoryCount);
        await vm.LoadAsync("B", false, default);
        Assert.True(oldToken.IsCancellationRequested);
        old.SetResult(Directory(FriendQueryTests.Friend(1, "Old profile", 1)));
        await first;
        Assert.Equal("New profile", Assert.Single(vm.Rows).Name);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ExplicitCancellationFinishesLoadingWithoutMisleadingEmptyState()
    {
        using var cancel = new CancellationTokenSource();
        using var vm = Create(new Friends { Get = (_, _, _) => Pending<Outcome<FriendDirectory>>().Task });
        var load = vm.LoadAsync("A", false, cancel.Token); cancel.Cancel(); await load;
        Assert.False(vm.IsBusy); Assert.Contains("cancelled", vm.StateMessage);
        Assert.Equal("N/A", vm.DirectoryCount);
    }

    [Fact]
    public async Task ClearCancelsPendingFiltersAndImmediatelyDropsSelectionAndRows()
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows[0]; vm.Query = "Friend";
        var pending = vm.FilteringTask; vm.Clear(); await pending;
        Assert.Empty(vm.Rows); Assert.False(vm.HasSelection); Assert.False(vm.IsFiltering);
        Assert.False(vm.HasCompleteness);
        Assert.Equal("N/A", vm.DirectoryCount); Assert.Contains("Open a Steam profile", vm.StateMessage);
    }

    [Fact]
    public async Task FiltersOnlineSortAndResetUseWholeDirectory()
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default);
        vm.OnlineOnly = true; await vm.ApplyFiltersAsync(); Assert.Equal("Friend A", Assert.Single(vm.Rows).Name);
        vm.Query = "notfound"; await vm.ApplyFiltersAsync(); Assert.Empty(vm.Rows); Assert.Contains("No friends match", vm.StateMessage);
        vm.ResetFiltersCommand.Execute(null); await vm.FilteringTask;
        Assert.Equal(2, vm.Rows.Count); Assert.False(vm.OnlineOnly); Assert.Equal("", vm.Query);
        vm.SelectedSortIndex = 1; await vm.ApplyFiltersAsync(); Assert.Equal("Friend A", vm.Rows[0].Name);
    }

    [Fact]
    public async Task SelectionMadeWhileDebounceIsPendingIsPreserved()
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows[0];
        vm.Query = "Friend"; var pending = vm.FilteringTask;
        var latest = vm.Rows[1]; vm.SelectedFriend = latest;
        await pending;
        Assert.Equal(latest.SteamId, vm.SelectedFriend?.SteamId);
    }

    [Fact]
    public async Task FilteringAwaySelectedFriendClearsMiniProfile()
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows.Single(x => x.Name == "Friend A");
        vm.Query = "Friend B"; await vm.ApplyFiltersAsync();
        Assert.False(vm.HasSelection); Assert.Null(vm.SelectedAvatar);
    }

    [Fact]
    public async Task DirectoryLoadingAndFilteringNeverLoadAvatarsUntilSelected()
    {
        var avatars = new Avatars();
        using var vm = new FriendsViewModel(new Friends { Get = (_, _, _) => Task.FromResult(Directory(FriendQueryTests.Friend(1, "Avatar", 1, "https://avatars.steamstatic.com/a.jpg"))) }, avatars);
        await vm.LoadAsync("A", false, default); await vm.ApplyFiltersAsync(); Assert.Equal(0, avatars.Requests);
        vm.SelectedFriend = vm.Rows[0]; await vm.AvatarLoadingTask; Assert.Equal(1, avatars.Requests);
        await vm.ApplyFiltersAsync(); await vm.AvatarLoadingTask; Assert.Equal(1, avatars.Requests);
    }

    [Fact]
    public async Task ItemsSourceSelectionClearDuringSortingKeepsMiniProfileAndDoesNotReloadAvatar()
    {
        var avatars = new Avatars();
        using var vm = new FriendsViewModel(new Friends { Get = (_, _, _) => Task.FromResult(Directory(
            FriendQueryTests.Friend(1, "Friend A", 1, "https://avatars.steamstatic.com/a.jpg"),
            FriendQueryTests.Friend(2, "Friend B", 0))) }, avatars);
        await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows[0];
        await vm.AvatarLoadingTask;
        var selected = vm.SelectedFriend;
        vm.PropertyChanged += (_, args) =>
        {
            // Avalonia's two-way SelectedItem binding clears while ItemsSource is replaced.
            if (args.PropertyName == nameof(vm.Rows)) vm.SelectedFriend = null;
        };

        vm.SelectedSortIndex = 1;
        await vm.ApplyFiltersAsync();
        await vm.AvatarLoadingTask;

        Assert.Same(selected, vm.SelectedFriend);
        Assert.True(vm.HasSelection);
        Assert.Equal(1, avatars.Requests);
        vm.Query = "Friend B";
        await vm.ApplyFiltersAsync();
        Assert.Null(vm.SelectedFriend);
    }

    [Fact]
    public async Task AvatarSelectionCancelsOlderImageAndIgnoresLateCompletion()
    {
        var old = Pending<Bitmap?>(); CancellationToken oldToken = default;
        var avatars = new Avatars { Get = (url, token) => { if (url!.Contains("old")) { oldToken = token; return old.Task; } return Task.FromResult<Bitmap?>(null); } };
        using var vm = new FriendsViewModel(new Friends { Get = (_, _, _) => Task.FromResult(Directory(
            FriendQueryTests.Friend(1, "Old", 1, "https://avatars.steamstatic.com/old.jpg"),
            FriendQueryTests.Friend(2, "New", 1, "https://avatars.steamstatic.com/new.jpg"))) }, avatars);
        await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows.Single(x => x.Name == "Old"); var loadingOld = vm.AvatarLoadingTask;
        vm.SelectedFriend = vm.Rows.Single(x => x.Name == "New"); await vm.AvatarLoadingTask;
        Assert.True(oldToken.IsCancellationRequested);
        old.SetResult(null); await loadingOld;
        Assert.Equal("New", vm.SelectedFriend?.Name); Assert.Null(vm.SelectedAvatar);
    }

    [Fact]
    public async Task AvatarFailureDoesNotHideDirectoryOrMiniProfile()
    {
        using var vm = new FriendsViewModel(new Friends { Get = (_, _, _) => Task.FromResult(Directory(FriendQueryTests.Friend(1, "Available", null, "https://avatars.steamstatic.com/a.jpg"))) },
            new Avatars { Get = (_, _) => throw new HttpRequestException("Do not show this transport error") });
        await vm.LoadAsync("A", false, default); vm.SelectedFriend = vm.Rows[0]; await vm.AvatarLoadingTask;
        Assert.Equal(DataState.Ready, vm.State); Assert.True(vm.HasSelection); Assert.Null(vm.SelectedAvatar);
        Assert.Equal("", vm.LocalMessage);
    }

    [Theory]
    [InlineData(FriendDestination.Explorer)]
    [InlineData(FriendDestination.CompareA)]
    [InlineData(FriendDestination.CompareB)]
    public async Task SelectedFriendCanNavigateToEverySupportedDestination(FriendDestination destination)
    {
        using var vm = Create(); await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows[0]; FriendAction? actual = null; vm.ActionRequested += value => actual = value;
        vm.RequestAction(destination);
        Assert.Equal(new(vm.SelectedFriend.SteamId, destination), actual);
        vm.Clear(); actual = null; vm.RequestAction(destination); Assert.Null(actual);
    }

    [Theory]
    [InlineData("https://evil.example/76561198000000001")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001/")]
    [InlineData(" 76561198000000001 ")]
    [InlineData("invalid")]
    public void NavigationRejectsUnvalidatedRawIds(string value)
    {
        using var vm = Create(); var called = false; vm.ActionRequested += _ => called = true;
        vm.SelectedFriend = new(new(value, null, null, DataState.Unavailable));
        vm.RequestAction(FriendDestination.Explorer); Assert.False(called);
    }

    [Fact]
    public async Task ExceptionsAreSafeAndForceRefreshFlagReachesService()
    {
        var forceSeen = false;
        using var vm = Create(new Friends { Get = (_, force, _) => { forceSeen = force; throw new HttpRequestException("Secret transport URI"); } });
        await vm.LoadAsync("A", true, default);
        Assert.True(forceSeen); Assert.Equal(DataState.Failed, vm.State);
        Assert.DoesNotContain("Secret", vm.StateMessage);
    }

    [Fact]
    public async Task LocalizationRefreshNotifiesRetainedMiniProfileWithoutReloadingAvatarOrDirectory()
    {
        var requests = 0;
        var avatars = new Avatars();
        using var vm = new FriendsViewModel(new Friends { Get = (_, _, _) =>
        {
            requests++;
            return Task.FromResult(Directory(FriendQueryTests.Friend(1, "Friend A", 1), FriendQueryTests.Friend(2, "Friend B", 0)));
        } }, avatars);
        await vm.LoadAsync("A", false, default);
        vm.SelectedFriend = vm.Rows[0];
        await vm.AvatarLoadingTask;
        var selected = vm.SelectedFriend!;
        var rowNotifications = 0;
        selected.PropertyChanged += (_, _) => rowNotifications++;
        await vm.ApplyFiltersAsync();
        Assert.Same(selected, vm.SelectedFriend);
        Assert.DoesNotContain(vm.Rows, row => ReferenceEquals(row, selected));
        var rowSource = vm.Rows;
        var imageRequests = avatars.Requests;
        var selectedPropertyNotified = false;
        vm.PropertyChanged += (_, args) => selectedPropertyNotified |= args.PropertyName == nameof(vm.SelectedFriend);

        vm.RefreshLocalization();

        Assert.True(rowNotifications > 0);
        Assert.True(selectedPropertyNotified);
        Assert.Same(selected, vm.SelectedFriend);
        Assert.Same(rowSource, vm.Rows);
        Assert.Equal(imageRequests, avatars.Requests);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DisposeCancelsRequestsAndRejectsFurtherLoading()
    {
        CancellationToken captured = default;
        var vm = Create(new Friends { Get = (_, _, token) => { captured = token; return Pending<Outcome<FriendDirectory>>().Task; } });
        var load = vm.LoadAsync("A", false, default); vm.Dispose(); await load;
        Assert.True(captured.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vm.LoadAsync("A", false, default));
    }

    private static FriendsViewModel Create(Friends? friends = null) => new(friends ?? new(), new Avatars());
    private static Outcome<FriendDirectory> Directory(params SteamFriend[] friends) => Outcome<FriendDirectory>.Ready(new(friends, true, 0));
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Friends : IFriendsService
    {
        public Func<string, bool, CancellationToken, Task<Outcome<FriendDirectory>>> Get { get; init; } = (_, _, _) => Task.FromResult(
            Directory(FriendQueryTests.Friend(1, "Friend B", 0), FriendQueryTests.Friend(2, "Friend A", 1)));
        public Task<Outcome<FriendDirectory>> GetFriendsAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Get(steamId, forceRefresh, cancellationToken);
    }
    private sealed class Avatars : IAvatarLoader
    {
        public int Requests { get; private set; }
        public Func<string?, CancellationToken, Task<Bitmap?>> Get { get; init; } = (_, _) => Task.FromResult<Bitmap?>(null);
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken token) { ++Requests; return Get(url, token); }
    }
}

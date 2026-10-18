using Avalonia.Media.Imaging;
using GamePulse.Core;
using GamePulse.Desktop.Services;
using GamePulse.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamePulse.UnitTests;

/// <summary>Root wiring must not turn profile loading or feature navigation into an inventory scan.</summary>
public sealed class InventoryNavigationTests
{
    private const string IdA = "76561198000000001", IdB = "76561198000000002";

    [Fact]
    public async Task SuccessfulSummaryMakesInventoryAvailableWithoutFetchingIt()
    {
        var inventory = new Inventories(); using var avatars = new AvatarService();
        var feature = new InventoryViewModel(inventory, new Icons()); using var vm = Create(new Profiles(), avatars, feature);
        vm.SearchInput = IdA; await vm.SearchAsync(false);
        Assert.Equal(IdA, feature.SteamId); Assert.True(feature.CanLoad); Assert.Empty(inventory.Calls);
        vm.ShowInventoryCommand.Execute(null); Assert.True(vm.IsInventory); Assert.False(vm.IsOverview);
        feature.SelectedAppIndex = 5; feature.ContextId = "7"; feature.Query = "some item";
        await feature.ApplyFiltersAsync(); Assert.Empty(inventory.Calls);
        await feature.LoadAsync(false); Assert.Equal(new InventoryRequest(IdA, 753, "7"), Assert.Single(inventory.Calls));
    }

    [Fact]
    public async Task GlobalEscapeCancelsInventoryEvenWhenRootSummaryIsNoLongerBusy()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); CancellationToken captured = default;
        var inventory = new Inventories { Get = (_, token) => { captured = token; return pending.Task; } };
        using var avatars = new AvatarService(); var feature = new InventoryViewModel(inventory, new Icons());
        using var vm = Create(new Profiles(), avatars, feature); vm.SearchInput = IdA; await vm.SearchAsync(false);
        Assert.False(vm.IsBusy); vm.ShowInventoryCommand.Execute(null); var load = feature.LoadAsync(false);
        Assert.True(feature.IsBusy); vm.CancelCommand.Execute(null); await load.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(captured.IsCancellationRequested); Assert.False(feature.IsBusy); Assert.Empty(feature.Rows);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Empty(feature.Rows);
    }

    [Fact]
    public async Task OpeningAnotherProfileCancelsAndRejectsLateOldInventory()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); CancellationToken captured = default;
        var inventory = new Inventories { Get = (_, token) => { captured = token; return pending.Task; } };
        using var avatars = new AvatarService(); var feature = new InventoryViewModel(inventory, new Icons());
        using var vm = Create(new Profiles(), avatars, feature); vm.SearchInput = IdA; await vm.SearchAsync(false);
        vm.ShowInventoryCommand.Execute(null); var oldLoad = feature.LoadAsync(false);
        vm.SearchInput = IdB; await vm.SearchAsync(false); await oldLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(captured.IsCancellationRequested); Assert.Equal(IdB, feature.SteamId);
        Assert.Empty(feature.Rows); Assert.Single(inventory.Calls);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Equal(IdB, feature.SteamId); Assert.Empty(feature.Rows);
    }

    [Theory]
    [InlineData(DataState.MissingKey)] [InlineData(DataState.NotFound)] [InlineData(DataState.Failed)]
    public async Task FailedGlobalSearchReturnsToVisibleSummaryAndClearsInventory(DataState failure)
    {
        var fail = false; var profiles = new Profiles { Find = input => fail
            ? Outcome<SteamProfile>.Failure(failure, "raw secret key") : Profile(input) };
        var inventory = new Inventories(); using var avatars = new AvatarService();
        var feature = new InventoryViewModel(inventory, new Icons()); using var vm = Create(profiles, avatars, feature);
        vm.SearchInput = IdA; await vm.SearchAsync(false); await feature.LoadAsync(false); Assert.Single(feature.Rows);
        vm.ShowInventoryCommand.Execute(null); fail = true; vm.SearchInput = IdB; await vm.SearchAsync(false);
        Assert.True(vm.IsOverview); Assert.False(vm.IsInventory); Assert.False(feature.HasProfile); Assert.Empty(feature.Rows);
        Assert.DoesNotContain("raw secret", vm.StatusMessage); Assert.Single(inventory.Calls);
    }

    [Fact]
    public async Task DisposingWindowCancelsPendingInventoryAndPreventsFutureLoads()
    {
        var pending = Pending<Outcome<InventorySnapshot>>(); CancellationToken captured = default;
        var inventory = new Inventories { Get = (_, token) => { captured = token; return pending.Task; } };
        using var avatars = new AvatarService(); var feature = new InventoryViewModel(inventory, new Icons());
        var vm = Create(new Profiles(), avatars, feature); vm.SearchInput = IdA; await vm.SearchAsync(false);
        var load = feature.LoadAsync(false); vm.Dispose(); await load.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(captured.IsCancellationRequested); feature.SetProfile(IdB); await feature.LoadAsync(false);
        Assert.Single(inventory.Calls); Assert.False(feature.HasProfile);
        pending.SetResult(Ready(new(IdA, 730, "2"))); Assert.Empty(feature.Rows);
    }

    private static MainWindowViewModel Create(Profiles profiles, AvatarService avatars, InventoryViewModel inventory) =>
        new(profiles, avatars, NullLogger<MainWindowViewModel>.Instance, inventory: inventory);
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Outcome<SteamProfile> Profile(string id) => Outcome<SteamProfile>.Ready(new(id, "Inventory test profile",
        "https://steamcommunity.com/profiles/" + id, null, 3, 1, null, null, null));
    private static Outcome<InventorySnapshot> Ready(InventoryRequest request) => Outcome<InventorySnapshot>.Ready(new(request,
        [new("1", "2", "0", 3, "Inventory test item", "Type", null, true, false, true)], 1, true, 1));
    private sealed class Profiles : IProfileLookupService
    {
        public Func<string, Outcome<SteamProfile>> Find { get; init; } = Profile;
        public Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(Find(input));
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(Outcome<ProfileLevel>.Ready(new(1)));
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(Outcome<SteamBans>.Ready(new(null, null, null, null, null, null)));
    }
    private sealed class Inventories : IInventoryService
    {
        public List<InventoryRequest> Calls { get; } = [];
        public Func<InventoryRequest, CancellationToken, Task<Outcome<InventorySnapshot>>> Get { get; init; } = (request, _) => Task.FromResult(Ready(request));
        public Task<Outcome<InventorySnapshot>> GetInventoryAsync(InventoryRequest request, bool forceRefresh,
            IProgress<InventoryProgress>? progress, CancellationToken cancellationToken)
        { Calls.Add(request); return Get(request, cancellationToken); }
    }
    private sealed class Icons : IInventoryIconLoader
    {
        public Task<Bitmap?> LoadAsync(string? url, CancellationToken token) => Task.FromResult<Bitmap?>(null);
    }
}

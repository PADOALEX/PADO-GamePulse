using GamePulse.Core;
using GamePulse.Infrastructure;
using Xunit;

namespace GamePulse.IntegrationTests;

public sealed class CacheRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_load_cannot_replace_new_forced_refresh_even_when_refresh_fails(bool refreshFails)
    {
        var steam = new ControlledClient();
        var lookup = new ProfileLookupService(steam, new(), TimeProvider.System);
        var first = lookup.FindAsync(ControlledClient.Id, false, default);
        var newer = lookup.FindAsync(ControlledClient.Id, true, default);
        steam.Second.SetResult(refreshFails
            ? Outcome<SteamProfile>.Failure(DataState.Unavailable, "Not available")
            : Profile("New"));
        await newer;
        steam.First.SetResult(Profile("Old"));
        await first;
        var cached = await lookup.FindAsync(ControlledClient.Id, false, default);
        if (refreshFails)
        {
            Assert.False(cached.FromCache);
            Assert.Equal(3, steam.Calls);
        }
        else
        {
            Assert.True(cached.FromCache);
            Assert.Equal("New", cached.Data!.DisplayName);
            Assert.Equal(2, steam.Calls);
        }
    }

    private static Outcome<SteamProfile> Profile(string name) => Outcome<SteamProfile>.Ready(new(
        ControlledClient.Id, name, "https://steamcommunity.com/profiles/" + ControlledClient.Id,
        null, 3, 0, null, null, null));

    private sealed class ControlledClient : ISteamApiClient
    {
        public const string Id = "76561198000000001";
        public int Calls { get; private set; }
        public TaskCompletionSource<Outcome<SteamProfile>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Outcome<SteamProfile>> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Outcome<SteamProfile>> GetProfileAsync(string steamId, CancellationToken cancellationToken) => ++Calls switch
        {
            1 => First.Task,
            2 => Second.Task,
            _ => Task.FromResult(Profile("Reloaded"))
        };
        public Task<Outcome<string>> ResolveVanityAsync(string vanity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Outcome<SteamBans>> GetBansAsync(string steamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

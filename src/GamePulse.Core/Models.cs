namespace GamePulse.Core;

public enum DataState
{
    Loading, Ready, Empty, Private, Unavailable, NotFound, InvalidInput,
    MissingKey, RateLimited, Failed, NoAchievements
}

/// <summary>Messages are safe static descriptions, never exception text or request URIs.</summary>
public sealed record Outcome<T>(DataState State, T? Data = default, string? Message = null,
    DateTimeOffset? UpdatedAt = null, bool FromCache = false)
{
    public bool IsReady => State == DataState.Ready && Data is not null;
    public static Outcome<T> Ready(T data, DateTimeOffset? time = null) =>
        new(DataState.Ready, data, UpdatedAt: time ?? DateTimeOffset.UtcNow);
    public static Outcome<T> Failure(DataState state, string message) => new(state, Message: message);
}

public enum SteamPresence { Offline, Online, Busy, Away, Snooze, LookingToTrade, LookingToPlay }

public sealed record SteamProfile(
    string SteamId, string? DisplayName, string ProfileUrl, string? AvatarUrl,
    int? Visibility, int? PersonaState, DateTimeOffset? CreatedAt, DateTimeOffset? LastLogoff,
    string? CurrentGame);

public sealed record ProfileLevel(int Value);

public sealed record SteamBans(bool? CommunityBanned, bool? VacBanned, int? VacBanCount,
    int? GameBanCount, int? DaysSinceLastBan, string? EconomyBan);

public interface ISteamApiClient
{
    Task<Outcome<string>> ResolveVanityAsync(string vanity, CancellationToken cancellationToken);
    Task<Outcome<SteamProfile>> GetProfileAsync(string steamId, CancellationToken cancellationToken);
    Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, CancellationToken cancellationToken);
    Task<Outcome<SteamBans>> GetBansAsync(string steamId, CancellationToken cancellationToken);
}

public interface IProfileLookupService
{
    Task<Outcome<SteamProfile>> FindAsync(string input, bool forceRefresh, CancellationToken cancellationToken);
    Task<Outcome<ProfileLevel>> GetLevelAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken);
    Task<Outcome<SteamBans>> GetBansAsync(string steamId, bool forceRefresh, CancellationToken cancellationToken);
}

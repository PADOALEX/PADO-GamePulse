using System.Text.Json.Serialization;

namespace GamePulse.Infrastructure;

// Wire contracts stay separate from the domain and UI models. Nullable fields preserve unknown data.
internal sealed class PlayerEnvelope
{
    [JsonPropertyName("response")] public PlayerResponse? Response { get; set; }
}
internal sealed class PlayerResponse
{
    [JsonPropertyName("players")] public PlayerDto[]? Players { get; set; }
}
internal sealed class PlayerDto
{
    [JsonPropertyName("steamid")] public string? SteamId { get; set; }
    [JsonPropertyName("personaname")] public string? Name { get; set; }
    [JsonPropertyName("profileurl")] public string? ProfileUrl { get; set; }
    [JsonPropertyName("avatarfull")] public string? Avatar { get; set; }
    [JsonPropertyName("communityvisibilitystate")] public int? Visibility { get; set; }
    [JsonPropertyName("personastate")] public int? PersonaState { get; set; }
    [JsonPropertyName("timecreated")] public long? Created { get; set; }
    [JsonPropertyName("lastlogoff")] public long? LastLogoff { get; set; }
    [JsonPropertyName("gameextrainfo")] public string? CurrentGame { get; set; }
}
internal sealed class VanityEnvelope
{
    [JsonPropertyName("response")] public VanityResponse? Response { get; set; }
}
internal sealed class VanityResponse
{
    [JsonPropertyName("success")] public int? Success { get; set; }
    [JsonPropertyName("steamid")] public string? SteamId { get; set; }
}
internal sealed class LevelEnvelope
{
    [JsonPropertyName("response")] public LevelResponse? Response { get; set; }
}
internal sealed class LevelResponse
{
    [JsonPropertyName("player_level")] public int? Level { get; set; }
}
internal sealed class BansEnvelope
{
    [JsonPropertyName("players")] public BansDto[]? Players { get; set; }
}
internal sealed class BansDto
{
    [JsonPropertyName("SteamId")] public string? SteamId { get; set; }
    [JsonPropertyName("CommunityBanned")] public bool? CommunityBanned { get; set; }
    [JsonPropertyName("VACBanned")] public bool? VacBanned { get; set; }
    [JsonPropertyName("NumberOfVACBans")] public int? VacBanCount { get; set; }
    [JsonPropertyName("NumberOfGameBans")] public int? GameBanCount { get; set; }
    [JsonPropertyName("DaysSinceLastBan")] public int? DaysSinceLastBan { get; set; }
    [JsonPropertyName("EconomyBan")] public string? EconomyBan { get; set; }
}

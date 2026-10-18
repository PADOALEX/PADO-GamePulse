using System.Text.Json.Serialization;

namespace GamePulse.Infrastructure;

internal sealed record AchievementSchemaEnvelope
{
    [JsonPropertyName("game")] public AchievementSchemaDto? Game { get; init; }
}
internal sealed record AchievementSchemaDto
{
    [JsonPropertyName("gameName")] public string? GameName { get; init; }
    [JsonPropertyName("availableGameStats")] public AvailableStatsDto? Available { get; init; }
}
internal sealed record AvailableStatsDto
{
    [JsonPropertyName("achievements")] public AchievementDefinitionDto?[]? Achievements { get; init; }
}
internal sealed record AchievementDefinitionDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("hidden")] public int? Hidden { get; init; }
}
internal sealed record PlayerAchievementsEnvelope
{
    [JsonPropertyName("playerstats")] public PlayerAchievementsDto? PlayerStats { get; init; }
}
internal sealed record PlayerAchievementsDto
{
    [JsonPropertyName("steamID")] public string? SteamId { get; init; }
    [JsonPropertyName("gameID")] public string? GameId { get; init; }
    [JsonPropertyName("success")] public bool? Success { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("achievements")] public PlayerAchievementDto?[]? Achievements { get; init; }
}
internal sealed record PlayerAchievementDto
{
    [JsonPropertyName("apiname")] public string? ApiName { get; init; }
    [JsonPropertyName("achieved")] public int? Achieved { get; init; }
    [JsonPropertyName("unlocktime")] public long? UnlockTime { get; init; }
}

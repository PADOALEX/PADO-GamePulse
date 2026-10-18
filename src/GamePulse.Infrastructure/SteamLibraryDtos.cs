using System.Text.Json.Serialization;

namespace GamePulse.Infrastructure;

internal sealed class LibraryEnvelope
{
    [JsonPropertyName("response")] public LibraryResponse? Response { get; set; }
}

internal sealed class LibraryResponse
{
    [JsonPropertyName("game_count")] public int? GameCount { get; set; }
    [JsonPropertyName("total_count")] public int? TotalCount { get; set; }
    [JsonPropertyName("games")] public LibraryGameDto?[]? Games { get; set; }
}

internal sealed class LibraryGameDto
{
    [JsonPropertyName("appid")] public uint? AppId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("playtime_forever")] public long? PlaytimeMinutes { get; set; }
    [JsonPropertyName("playtime_2weeks")] public long? RecentPlaytimeMinutes { get; set; }
    [JsonPropertyName("rtime_last_played")] public long? LastPlayed { get; set; }
}

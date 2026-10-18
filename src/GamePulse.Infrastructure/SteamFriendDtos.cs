using System.Text.Json.Serialization;

namespace GamePulse.Infrastructure;

internal sealed class FriendListEnvelope
{
    [JsonPropertyName("friendslist")] public FriendListResponse? FriendsList { get; set; }
}

internal sealed class FriendListResponse
{
    [JsonPropertyName("friends")] public FriendDto[]? Friends { get; set; }
}

internal sealed class FriendDto
{
    [JsonPropertyName("steamid")] public string? SteamId { get; set; }
    [JsonPropertyName("relationship")] public string? Relationship { get; set; }
    [JsonPropertyName("friend_since")] public long? FriendsSince { get; set; }
}

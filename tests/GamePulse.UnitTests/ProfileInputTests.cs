using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public class ProfileInputTests
{
    [Theory]
    [InlineData("76561198000000001", ProfileInputKind.SteamId, "76561198000000001")]
    [InlineData("  76561198000000001  ", ProfileInputKind.SteamId, "76561198000000001")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001/?l=uk#info", ProfileInputKind.SteamId, "76561198000000001")]
    [InlineData("http://www.steamcommunity.com/id/a_valid-slug/", ProfileInputKind.Vanity, "a_valid-slug")]
    [InlineData("steamcommunity.com/id/someone", ProfileInputKind.Vanity, "someone")]
    [InlineData("https://STEAMCOMMUNITY.COM/id/someone?l=en", ProfileInputKind.Vanity, "someone")]
    [InlineData("valid_slug", ProfileInputKind.Vanity, "valid_slug")]
    public void SupportsIdentifiersAndNormalizesUrls(string input, ProfileInputKind kind, string value)
    {
        var result = ProfileInput.Parse(input);
        Assert.True(result.IsReady);
        Assert.Equal(kind, result.Data!.Kind);
        Assert.Equal(value, result.Data.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Player Display Name")]
    [InlineData("Довільне ім'я")]
    [InlineData("12345")]
    [InlineData("76561197960265728")]
    [InlineData("99999999999999999")]
    [InlineData("9999999999999999999999999999999999")]
    [InlineData("https://steamcommunity.com.evil.test/id/user")]
    [InlineData("https://evil.test/steamcommunity.com/id/user")]
    [InlineData("https://steamcommunity.com@evil.test/id/user")]
    [InlineData("https://user:password@steamcommunity.com/id/user")]
    [InlineData("https://steamcommunity.com:444/id/user")]
    [InlineData("file://steamcommunity.com/id/user")]
    [InlineData("ftp://steamcommunity.com/id/user")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://steamcommunity.com/id/user/games")]
    [InlineData("https://steamcommunity.com/id/a%2fb")]
    [InlineData("https://steamcommunity.com/profiles/123")]
    [InlineData("https://steamcommunity.com/id/")]
    [InlineData("https://steamcommunity.com/gid/something")]
    public void RejectsInvalidIdsDisplayNamesAndUnsafeUrls(string? input)
    {
        var result = ProfileInput.Parse(input);
        Assert.Equal(DataState.InvalidInput, result.State);
        Assert.Contains("Display names are not unique", result.Message);
    }

    [Fact]
    public void PlainSingleWordIsOnlyAVanityCandidateNotDisplayNameLookup()
    {
        var result = ProfileInput.Parse("Player");
        Assert.Equal(ProfileInputKind.Vanity, result.Data!.Kind);
        // ResolveVanityURL decides whether this exact slug exists. No global nickname search.
    }

    [Fact]
    public void RejectsUnboundedInput()
    {
        Assert.Equal(DataState.InvalidInput, ProfileInput.Parse(new string('a', 10000)).State);
    }
}

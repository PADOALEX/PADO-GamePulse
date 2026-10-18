using System.Globalization;
using System.Text.RegularExpressions;

namespace GamePulse.Core;

public enum ProfileInputKind { SteamId, Vanity }
public sealed record ProfileInput(ProfileInputKind Kind, string Value)
{
    private static readonly Regex VanityPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    public static Outcome<ProfileInput> Parse(string? input)
    {
        var value = input?.Trim() ?? "";
        if (value.Length == 0 || value.Length > 512)
            return Invalid();
        if (value.Contains("://", StringComparison.Ordinal) ||
            value.StartsWith("steamcommunity.com/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("www.steamcommunity.com/", StringComparison.OrdinalIgnoreCase))
        {
            if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                (uri.IdnHost != "steamcommunity.com" && uri.IdnHost != "www.steamcommunity.com") ||
                !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                return Invalid();
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2) return Invalid();
            value = Uri.UnescapeDataString(segments[1]);
            if (segments[0] == "profiles") return ParseId(value);
            if (segments[0] != "id" || !VanityPattern.IsMatch(value)) return Invalid();
            return Outcome<ProfileInput>.Ready(new(ProfileInputKind.Vanity, value));
        }
        if (value.All(char.IsAsciiDigit)) return ParseId(value);
        return VanityPattern.IsMatch(value)
            ? Outcome<ProfileInput>.Ready(new(ProfileInputKind.Vanity, value))
            : Invalid();
    }

    private static Outcome<ProfileInput> ParseId(string value)
    {
        // Public individual Steam account: universe 1, type Individual, desktop instance 1.
        // Low 32 bits are the account id. Reject clan, lobby, malformed and zero-account IDs.
        if (value.Length != 17 || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            (id >> 56) != 1 || ((id >> 52) & 15) != 1 || ((id >> 32) & 0xFFFFF) != 1 || (id & uint.MaxValue) == 0)
            return Invalid();
        return Outcome<ProfileInput>.Ready(new(ProfileInputKind.SteamId, value));
    }

    private static Outcome<ProfileInput> Invalid() => Outcome<ProfileInput>.Failure(DataState.InvalidInput,
        "Enter a SteamID64, Steam profile URL or vanity slug. Display names are not unique and cannot be searched globally.");
}

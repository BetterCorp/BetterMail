using BetterMail.Core;

namespace BetterMail.Google;

public sealed record GoogleOptions(string ClientId, string ClientSecret)
{
    public static GoogleOptions Create() => new(
        BuildCredential.Require<GoogleOptions>("BETTERMAIL_GOOGLE_CLIENT_ID"),
        BuildCredential.Require<GoogleOptions>("BETTERMAIL_GOOGLE_CLIENT_SECRET"));
}

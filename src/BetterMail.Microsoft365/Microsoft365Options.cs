using BetterMail.Core;

namespace BetterMail.Microsoft365;

public sealed record Microsoft365Options(string ClientId, string DataDirectory)
{
    public static Microsoft365Options Create(string dataDirectory) => new(
        BuildCredential.Require<Microsoft365Options>("BETTERMAIL_MICROSOFT_CLIENT_ID"),
        dataDirectory);
}

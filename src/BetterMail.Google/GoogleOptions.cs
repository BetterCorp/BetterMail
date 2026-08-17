namespace BetterMail.Google;

public sealed record GoogleOptions(string ClientId)
{
    public const string DefaultClientId =
        "976869680096-gma6gll9j3js4mkfjl206po3i4tdim7u.apps.googleusercontent.com";

    public static GoogleOptions Create()
    {
        var clientId = Environment.GetEnvironmentVariable("BETTERMAIL_GOOGLE_CLIENT_ID");
        return new(string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim());
    }
}

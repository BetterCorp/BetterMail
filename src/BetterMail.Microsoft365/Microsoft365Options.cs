namespace BetterMail.Microsoft365;

public sealed record Microsoft365Options(string ClientId, string DataDirectory)
{
    public const string DefaultClientId = "c6346912-5a92-47da-99fc-f073ec93d05c";

    public static Microsoft365Options Create(string dataDirectory)
    {
        var clientId = Environment.GetEnvironmentVariable("BETTERMAIL_MICROSOFT_CLIENT_ID");
        return new Microsoft365Options(
            string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId,
            dataDirectory);
    }
}

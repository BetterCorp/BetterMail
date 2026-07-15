namespace BetterMail.App;

public sealed class AppInfo
{
    private static readonly Version? AssemblyVersion = typeof(AppInfo).Assembly.GetName().Version;

    public string Version { get; } = AssemblyVersion?.ToString(3) ?? "Unknown";
    public Uri RepositoryUri { get; } = new("https://github.com/BetterCorp/BetterMail");
    public Uri ReleasesUri { get; } = new("https://github.com/BetterCorp/BetterMail/releases");
    public Uri LicenseUri { get; } = new("https://github.com/BetterCorp/BetterMail/blob/master/LICENSE");
}

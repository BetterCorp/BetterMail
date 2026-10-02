using BetterMail.App;

namespace BetterMail.Tests;

public sealed class LinuxDesktopIntegrationTests
{
    [Fact]
    public void LauncherKeepsItsIconAndTracksMovedAppImage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-launcher-" + Guid.NewGuid());
        try
        {
            var appImage = Path.Combine(directory, "Original Mail.AppImage");
            LinuxDesktopIntegration.Install(appImage, directory);
            var desktopPath = Path.Combine(directory, "applications", "bettermail.desktop");
            var iconPath = Path.Combine(directory, "icons", "hicolor", "512x512", "apps", "BetterMail.png");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(iconPath)[..8]);
            Assert.Contains("StartupWMClass=BetterMail", File.ReadAllText(desktopPath));
            Assert.Contains("MimeType=x-scheme-handler/mailto;x-scheme-handler/bettermail;", File.ReadAllText(desktopPath));
            Assert.Contains("Icon=" + iconPath.Replace("\\", "\\\\"), File.ReadAllText(desktopPath));

            var moved = Path.Combine(directory, "Moved Mail.AppImage");
            LinuxDesktopIntegration.Install(moved, directory);
            var desktop = File.ReadAllText(desktopPath);
            Assert.DoesNotContain("Original Mail.AppImage", desktop);
            Assert.Contains("Moved Mail.AppImage\" %u", desktop);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(desktopPath)!));
            Assert.False(File.Exists(Path.Combine(directory, "applications", "mimeapps.list")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DesktopExecEscapesSpecialCharacters()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-launcher-" + Guid.NewGuid());
        try
        {
            LinuxDesktopIntegration.Install(Path.Combine(directory, "Mail $`\"%\\.AppImage"), directory);
            var desktop = File.ReadAllText(Path.Combine(directory, "applications", "bettermail.desktop"));
            Assert.Contains("Mail \\\\$\\\\`\\\\\"%%\\\\\\\\.AppImage\" %u", desktop);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("relative.AppImage")]
    [InlineData("/mail\nExec=other")]
    public void InvalidExecutableDoesNotWriteLauncher(string executable)
    {
        Assert.Throws<ArgumentException>(() => LinuxDesktopIntegration.Install(executable, Path.GetTempPath()));
    }
}

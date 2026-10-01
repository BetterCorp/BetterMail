using Avalonia.Controls;
using BetterMail.App;

namespace BetterMail.Tests;

public sealed class ExternalLinkTests
{
    [Theory]
    [InlineData("https://example.test/path")]
    [InlineData("http://example.test/path")]
    [InlineData("mailto:person@example.test")]
    public void ExternalNavigationIsCancelledEvenWithoutAProcessHandle(string address)
    {
        var args = new WebViewNavigationStartingEventArgs { Request = new Uri(address) };
        var opened = new List<Uri>();
        ReadOnlyWebViewLinks.CancelNavigation(args, uri =>
        {
            Assert.True(args.Cancel); // Cancel before invoking the OS handler.
            opened.Add(uri);
        });
        Assert.True(args.Cancel);
        Assert.Equal(args.Request, Assert.Single(opened));
    }

    [Fact]
    public void FailedBrowserLaunchNeverFallsBackToNavigation()
    {
        var args = new WebViewNavigationStartingEventArgs { Request = new Uri("https://example.test") };
        ReadOnlyWebViewLinks.CancelNavigation(args, _ => throw new InvalidOperationException("No browser"));
        Assert.True(args.Cancel);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("file:///tmp/fictional-message.html")]
    [InlineData("data:text/html,fictional")]
    public void ApplicationDocumentsDoNotOpenBrowserTabs(string address)
    {
        var args = new WebViewNavigationStartingEventArgs { Request = new Uri(address) };
        ReadOnlyWebViewLinks.CancelNavigation(args, _ => throw new Xunit.Sdk.XunitException("Unexpected launch"));
        Assert.False(args.Cancel);
    }
}

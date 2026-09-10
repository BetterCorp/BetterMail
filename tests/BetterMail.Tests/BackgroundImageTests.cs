using System.Net;
using System.Text;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class BackgroundImageTests
{
    [Theory]
    [InlineData("https://example.com/image.png", true)]
    [InlineData("https://example.com:443/image.png", true)]
    [InlineData("https://example.com:80/image.png", false)]
    [InlineData("https://example.com:8443/image.png", false)]
    [InlineData("http://example.com/image.png", false)]
    [InlineData("http://example.com:443/image.png", false)]
    [InlineData("file:///image.png", false)]
    [InlineData("/image.png", false)]
    public void ArtworkRequiresHttpsOnPort443(string url, bool allowed) =>
        Assert.Equal(allowed, PublicImageHttp.IsAllowedUri(new Uri(url, UriKind.RelativeOrAbsolute)));

    [Theory]
    [InlineData("http://example.com/image.png")]
    [InlineData("https://example.com:8443/image.png")]
    [InlineData("//example.com:80/image.png")]
    public async Task UnsafeRedirectTargetsAreRejectedBeforeRequest(string location)
    {
        var target = new Uri(new Uri("https://example.com/original.png"), location);
        Assert.False(PublicImageHttp.IsAllowedUri(target));
        Assert.Null(await PublicImageHttp.GetAsync(target, 1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExternalContactImagesDefaultOffForNewAndExistingPreferences()
    {
        Assert.False(new AppPreferences().ContactImagesEnabled);
        Assert.False(JsonSerializer.Deserialize<AppPreferences>("{}")!.ContactImagesEnabled);
        Assert.True(JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(new AppPreferences(ContactImagesEnabled: true)))!.ContactImagesEnabled);
        Assert.False(new MainWindowViewModel(null, "data", _ => { }, _ => { }, null).ContactImagesEnabled);
    }

    [Theory]
    [InlineData("192.0.0.0/24")]
    [InlineData("192.0.2.0/24")]
    [InlineData("192.88.99.0/24")]
    [InlineData("198.51.100.0/24")]
    [InlineData("203.0.113.0/24")]
    [InlineData("198.18.0.0/15")]
    [InlineData("224.0.0.0/4")]
    [InlineData("240.0.0.0/4")]
    [InlineData("2001::/23")]
    [InlineData("2001:db8::/32")]
    [InlineData("2002::/16")]
    [InlineData("3fff::/20")]
    [InlineData("64:ff9b::/96")]
    [InlineData("5f00::/16")]
    public void ReservedRangesRejectFirstAndLastAddressesIncludingMappedV4(string cidr)
    {
        var network = IPNetwork.Parse(cidr);
        var first = network.BaseAddress;
        var bytes = first.GetAddressBytes();
        for (var bit = network.PrefixLength; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
        var last = new IPAddress(bytes);
        Assert.False(PublicImageHttp.IsPublicAddress(first));
        Assert.False(PublicImageHttp.IsPublicAddress(last));
        if (bytes.Length == 4)
        {
            Assert.False(PublicImageHttp.IsPublicAddress(first.MapToIPv6()));
            Assert.False(PublicImageHttp.IsPublicAddress(last.MapToIPv6()));
        }
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.2.3.4", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void ImageRequestsRejectPrivateDestinations(string address, bool allowed) =>
        Assert.Equal(allowed, PublicImageHttp.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("v=BIMI1; l=https://example.com/logo.svg", "https://example.com/logo.svg")]
    [InlineData("v=BIMI1; l=http://example.com/logo.svg", null)]
    [InlineData("v=BIMI1; l=https://user:password@example.com/logo.svg", null)]
    [InlineData("v=DMARC1; l=https://example.com/logo.svg", null)]
    public void BimiUsesOnlyHttpsLogoRecords(string record, string? expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { Answer = new[] { new { type = 16, data = "\"" + record + "\"" } } }));
        Assert.Equal(expected, BackgroundImages.BimiLogo(document.RootElement)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<image href='https://example.com/track'/>")]
    [InlineData("<style>@import 'https://example.com/track';</style>")]
    [InlineData("<rect style='fill:red'/>")]
    [InlineData("<rect fill='url(https://example.com/track)'/>")]
    [InlineData("<use href='#cycle'/>")]
    public void SvgArtworkCannotLoadExternalResourcesOrActiveContent(string content) =>
        Assert.Null(BackgroundImages.SafeSvg(Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg'>{content}</svg>")));

    [Fact]
    public void StaticBimiLogoRasterizesToBoundedPng()
    {
        var result = BackgroundImages.Normalize(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><rect width='64' height='64' fill='#345678'/></svg>"), allowSvg: true);
        Assert.NotNull(result);
        Assert.True(result.Length < 65536);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, result.Take(4));
    }

    [Fact]
    public async Task CacheReusesArtworkAndNegativeResults()
    {
        var calls = 0;
        var request = new ImageRequest(Guid.NewGuid().ToString(), _ => { calls++; return Task.FromResult<byte[]?>(null); });
        await BackgroundImages.GetAsync(request, TestContext.Current.CancellationToken);
        await BackgroundImages.GetAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CanceledImageDoesNotPoisonLaterRequest()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = Guid.NewGuid().ToString();
        var request = new ImageRequest(key, async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; });
        var pending = BackgroundImages.GetAsync(request, cancel.Token);
        await entered.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(new byte[] { 1 }, await BackgroundImages.GetAsync(new(key, _ => Task.FromResult<byte[]?>([1])), TestContext.Current.CancellationToken));
    }
}

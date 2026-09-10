using System.Net;
using System.Text;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class BackgroundImageTests
{
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

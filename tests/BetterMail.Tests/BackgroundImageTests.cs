using System.Net;
using System.Text;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class BackgroundImageTests
{
    [Fact]
    public void MailImagePreferenceDefaultsOffAndIsIndependentOfPeople()
    {
        Assert.False(new AppPreferences().MailSenderImagesEnabled);
        Assert.False(JsonSerializer.Deserialize<AppPreferences>("{\"ContactImagesEnabled\":true}")!.MailSenderImagesEnabled);
        var preferences = JsonSerializer.Deserialize<AppPreferences>(JsonSerializer.Serialize(new AppPreferences(MailSenderImagesEnabled: true)))!;
        Assert.True(preferences.MailSenderImagesEnabled);
        Assert.False(preferences.ContactImagesEnabled);
        var vm = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        vm.MailSenderImagesEnabled = true;
        Assert.False(vm.ContactImagesEnabled);
        Assert.Contains(nameof(vm.MailSenderImagesEnabled), changes);
        vm.ContactImagesEnabled = true;
        vm.MailSenderImagesEnabled = false;
        Assert.True(vm.ContactImagesEnabled);
    }

    [Theory]
    [InlineData("gmail.com")]
    [InlineData("GOOGLEMAIL.COM")]
    [InlineData("outlook.com")]
    [InlineData("hotmail.co.uk")]
    [InlineData("yahoo.co.jp")]
    [InlineData("icloud.com")]
    [InlineData("proton.me")]
    [InlineData("fastmail.com")]
    [InlineData("gmx.de")]
    [InlineData("qq.com")]
    public async Task SharedMailServicesOnlyRequestGravatarEvenWhenNoImageExists(string domain)
    {
        var requests = new List<Uri>();
        var result = await BackgroundImages.ContactAsync("person@" + domain, TestContext.Current.CancellationToken,
            (uri, _, _, _) => { requests.Add(uri); return Task.FromResult<byte[]?>(null); });
        Assert.Null(result);
        var request = Assert.Single(requests);
        Assert.Equal("www.gravatar.com", request.Host);
        Assert.StartsWith("/avatar/", request.AbsolutePath);
    }

    [Theory]
    [InlineData("company-business.com")]
    [InlineData("gmail.com.company-business.com")]
    public async Task CustomDomainsRetainDomainArtworkFallbacks(string domain)
    {
        var requests = new List<Uri>();
        await BackgroundImages.ContactAsync("person@" + domain, TestContext.Current.CancellationToken,
            (uri, _, _, _) => { requests.Add(uri); return Task.FromResult<byte[]?>(null); });
        Assert.Equal(new[] { "cloudflare-dns.com", "www.gravatar.com", domain }, requests.Select(uri => uri.Host));
    }

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
    public async Task PrefetchDoesNotEvictWhenForegroundFillsCacheDuringDownload()
    {
        // Isolate the shared cache so the test can reproduce the exact 255 -> 256 race.
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var cache = (System.Collections.IDictionary)typeof(BackgroundImages).GetField("Cache", flags)!.GetValue(null)!;
        var gate = typeof(BackgroundImages).GetField("Gate", flags)!.GetValue(null)!;
        System.Collections.DictionaryEntry[] saved;
        lock (gate)
        {
            saved = cache.Keys.Cast<object>().Select(key => new System.Collections.DictionaryEntry(key, cache[key])).ToArray();
            cache.Clear();
        }
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<byte[]?>? background = null;
        try
        {
            for (var i = 0; i < 255; i++)
                await BackgroundImages.GetAsync(new("race-" + i, _ => Task.FromResult<byte[]?>([1])), token);
            background = BackgroundImages.GetAsync(new("race-background", async ct =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return [2];
            }), token, background: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await BackgroundImages.GetAsync(new("race-final", _ => Task.FromResult<byte[]?>([3])), token);
            release.SetResult();
            await background;
            lock (gate)
            {
                Assert.Equal(256, cache.Count);
                Assert.True(cache.Contains("race-0"));
                Assert.True(cache.Contains("race-final"));
                Assert.False(cache.Contains("race-background"));
            }
        }
        finally
        {
            release.TrySetResult();
            if (background is not null) try { await background; } catch (OperationCanceledException) { }
            lock (gate)
            {
                cache.Clear();
                foreach (var entry in saved) cache.Add(entry.Key, entry.Value);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhotoPassOnlyFetchesMissingImagesAndSharesResultsWithVisibleRows(bool found)
    {
        var calls = 0;
        var request = new ImageRequest("contact:" + Guid.NewGuid(), _ =>
        {
            calls++;
            return Task.FromResult<byte[]?>(found ? [1, 2, 3] : null);
        });
        var token = TestContext.Current.CancellationToken;
        await BackgroundImages.PrefetchAsync([request, request], token);
        await BackgroundImages.PrefetchAsync([request], token);
        var visible = await BackgroundImages.GetAsync(request, token);
        Assert.Equal(1, calls);
        Assert.Equal(found ? new byte[] { 1, 2, 3 } : null, visible);
    }

    [Fact]
    public async Task PhotoPassCancellationStopsActiveLookupAndRemainingQueue()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCalls = 0;
        var first = new ImageRequest("contact:" + Guid.NewGuid(), async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        });
        var next = new ImageRequest("contact:" + Guid.NewGuid(), _ => { nextCalls++; return Task.FromResult<byte[]?>(null); });
        var pass = BackgroundImages.PrefetchAsync([first, next], cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        Assert.Equal(0, nextCalls);
    }

    [Fact]
    public async Task PhotoPassYieldsWhenInteractiveSlotsAreBusy()
    {
        using var cancellation = new CancellationTokenSource();
        var started = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Enumerable.Range(0, 4).Select(_ => BackgroundImages.GetAsync(
            new("foreground:" + Guid.NewGuid(), async token =>
            {
                if (Interlocked.Increment(ref started) == 4) entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }), cancellation.Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var calls = 0;
            await BackgroundImages.PrefetchAsync([new("contact:" + Guid.NewGuid(), _ =>
            {
                calls++;
                return Task.FromResult<byte[]?>(null);
            })], TestContext.Current.CancellationToken);
            Assert.Equal(0, calls);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var task in work) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
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

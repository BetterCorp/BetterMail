using BetterMail.App;

namespace BetterMail.Tests;

public sealed class RecipientDirectoryCacheTests
{
    private static readonly RecipientSuggestion Ada = new("Ada Lovelace", "ada@example.test", "Contacts");
    private static readonly RecipientSuggestion Grace = new("Grace Hopper", "grace@example.test", "Mail history");
    private static Task<IReadOnlyList<RecipientSuggestion>> Ready(params RecipientSuggestion[] values) =>
        Task.FromResult<IReadOnlyList<RecipientSuggestion>>(values);

    [Fact]
    public async Task SavedContactIsImmediatelySearchableWhileHistoryIsBlocked()
    {
        var history = new TaskCompletionSource<IReadOnlyList<RecipientSuggestion>>();
        var cache = new RecipientDirectoryCache(() => Ready(Ada), () => history.Task);
        var notifications = 0;
        cache.Changed += () => notifications++;
        var refresh = cache.RefreshAsync();
        var search = cache.SearchAsync("ada", CancellationToken.None);
        Assert.True(search.IsCompletedSuccessfully);
        Assert.Equal(Ada, Assert.Single(await search));
        Assert.False(refresh.IsCompleted);
        history.SetResult([Grace]);
        await refresh;
        Assert.Equal(2, notifications);
        Assert.Equal(Grace, Assert.Single(await cache.SearchAsync("grace", CancellationToken.None)));
    }

    [Fact]
    public async Task RefreshKeepsBothContactsAndHistoryAvailableWithoutWaitingForStorage()
    {
        var contacts = Ready(Ada);
        var history = Ready(Grace);
        var cache = new RecipientDirectoryCache(() => contacts, () => history);
        await cache.RefreshAsync();
        var blocked = new TaskCompletionSource<IReadOnlyList<RecipientSuggestion>>();
        contacts = blocked.Task;
        var refresh = cache.RefreshAsync();
        foreach (var query in new[] { "ada", "grace", "missing" })
        {
            var search = cache.SearchAsync(query, CancellationToken.None);
            Assert.True(search.IsCompletedSuccessfully);
            Assert.Equal(query == "missing" ? 0 : 1, (await search).Count);
        }
        blocked.SetResult([Ada with { DisplayName = "Updated Ada" }]);
        await refresh;
        Assert.Equal("Updated Ada", Assert.Single(await cache.SearchAsync("ada", CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task FailedRefreshRetainsRecipientsAndCanRecover()
    {
        var contacts = Ready(Ada);
        var cache = new RecipientDirectoryCache(() => contacts, () => Ready(Grace));
        await cache.RefreshAsync();
        contacts = Task.FromException<IReadOnlyList<RecipientSuggestion>>(new IOException("Offline local store"));
        await cache.RefreshAsync();
        Assert.Equal(Ada, Assert.Single(await cache.SearchAsync("ada", CancellationToken.None)));
        contacts = Ready(Ada with { DisplayName = "Recovered Ada" });
        await cache.RefreshAsync();
        Assert.Equal("Recovered Ada", Assert.Single(await cache.SearchAsync("ada", CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task CancellingAQueryDoesNotCancelDirectoryWarmup()
    {
        var contacts = new TaskCompletionSource<IReadOnlyList<RecipientSuggestion>>();
        var cache = new RecipientDirectoryCache(() => contacts.Task, () => Ready());
        using var cancellation = new CancellationTokenSource();
        var search = cache.SearchAsync("ada", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        contacts.SetResult([Ada]);
        Assert.Equal(Ada, Assert.Single(await cache.SearchAsync("ada", CancellationToken.None)));
    }

    [Fact]
    public async Task SavedContactsWinOverDuplicateHistoryAndResultsStayBounded()
    {
        var cache = new RecipientDirectoryCache(() => Ready(Ada), () => Ready(
            [Ada with { DisplayName = "Old Ada", Address = "ADA@example.test" },
             .. Enumerable.Range(0, 30).Select(i => new RecipientSuggestion($"Person {i}", $"person{i}@example.test", "History"))]));
        await cache.RefreshAsync();
        Assert.Equal(Ada, Assert.Single(await cache.SearchAsync("ada", CancellationToken.None)));
        Assert.Equal(20, (await cache.SearchAsync("example", CancellationToken.None)).Count);
    }
}

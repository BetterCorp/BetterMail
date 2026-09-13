using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class AdvancedSearchTests
{
    [Fact]
    public async Task WorkspaceSearchIncludesSharedParentAndLimitsAcrossAllAccounts()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-account-search-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('C', 64));
            await store.InitializeAsync(token);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            foreach (var id in new[] { "a", "b", "c" })
            {
                var account = new MailAccount("microsoft365", id, "tenant", id + "@example.test", id, ProviderCapabilities.Mail | ProviderCapabilities.Files);
                vm.Accounts.Add(account);
                var files = Enumerable.Range(0, id == "a" ? 60 : 1).Select(i => new CloudFile(id + i, "Budget", 1, null, id)).ToArray();
                await store.UpsertWorkspaceItemsAsync("drive-file", id, "all", files, file => file.ProviderId, file => file.Name, token);
            }
            var combined = await store.SearchWorkspaceItemsAsync<CloudFile>("drive-file", "Budget", 40, cancellationToken: token, accountIds: ["a", "b"]);
            Assert.Equal(40, combined.Count);
            Assert.Contains(combined, file => file.AccountId == "b");
            Assert.DoesNotContain(combined, file => file.AccountId == "c");
            var reversed = await store.SearchWorkspaceItemsAsync<CloudFile>("drive-file", "Budget", 40, cancellationToken: token, accountIds: ["b", "a"]);
            Assert.Equal(combined.Select(file => file.ProviderId), reversed.Select(file => file.ProviderId));
            var shared = new Mailbox("b", "team@example.test", "Team", IsShared: true);
            vm.Mailboxes.Add(shared);
            vm.SearchText = "Budget type:mail type:drive account:" + shared.Id;
            await ((AsyncCommand)vm.SearchCommand).ExecuteAsync();
            Assert.Null(vm.SearchError);
            Assert.Contains(vm.GlobalSearchResults, result => result.Value is CloudFile { AccountId: "b" });
            Assert.DoesNotContain(vm.GlobalSearchResults, result => result.Value is CloudFile { AccountId: "a" or "c" });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void EmptySearchFocusKeepsAdvancedFilterReachable()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        vm.OpenSearchInput();
        Assert.True(vm.IsGlobalSearchOpen);
        Assert.True(vm.IsSearchEditing);
        Assert.Contains("Advanced filter", vm.SearchNotice);
    }

    [Fact]
    public void RepeatedSelectionsRoundTripAndRejectContradictoryStates()
    {
        var query = SearchQuery.Parse("budget type:mail type:drive account:a account:b in:Inbox in:Projects notin:Old category:Finance category:Projects is:unread is:flagged");
        Assert.True(query.Includes("Mail")); Assert.True(query.Includes("Drive")); Assert.False(query.Includes("People"));
        var restored = SearchQuery.Parse(query.Serialize());
        foreach (var key in new[] { "type", "account", "in", "notin", "category", "is" }) Assert.Equal(query.Values(key), restored.Values(key));
        Assert.Throws<FormatException>(() => SearchQuery.Parse("is:read is:unread"));
        Assert.Throws<FormatException>(() => SearchQuery.Parse("type:Mails"));
        Assert.False(SearchQuery.Parse("type:drive in:Projects notin:Old").HasMailFilters);
    }

    [Fact]
    public void BadgesKeepExactTextAndDistinguishInvalidFiltersFromValidOnes()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        const string raw = "budget type:Mails has:attachments subject:{quarterly review}";
        vm.SearchText = raw;
        Assert.True(vm.ShowSearchBadges);
        Assert.False(vm.SearchBadges.Single(item => item.Text == "type:Mails").IsValid);
        Assert.True(vm.SearchBadges.Single(item => item.Text == "has:attachments").IsValid);
        Assert.False(vm.SearchBadges[0].IsFilter);
        vm.IsSearchEditing = true; Assert.False(vm.ShowSearchBadges); Assert.Equal(raw, vm.SearchText);
        vm.IsSearchEditing = false; Assert.True(vm.ShowSearchBadges); Assert.Equal(raw, vm.SearchText);
        vm.SearchText = "type:mail account:missing";
        Assert.True(vm.SearchBadges[0].IsValid); Assert.False(vm.SearchBadges[1].IsValid);
        vm.SearchText = "subject:{unfinished phrase";
        Assert.False(Assert.Single(vm.SearchBadges).IsValid);
    }

    [Fact]
    public async Task MailAndDriveFilteringApplyAccountsPathsAndCategoriesBeforeTheLimit()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-advanced-search-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('B', 64));
            await store.InitializeAsync(token);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            foreach (var id in new[] { "a", "b", "c" })
            {
                var account = new MailAccount("microsoft365", id, "tenant", id + "@example.test", id, ProviderCapabilities.Mail | ProviderCapabilities.Files);
                var mailbox = new Mailbox(id, account.EmailAddress, id);
                await store.SaveAccountAsync(account, token); await store.SaveMailboxAsync(mailbox, token);
                vm.Accounts.Add(account); vm.Mailboxes.Add(mailbox);
                foreach (var folder in new[] { new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 0, "inbox"),
                    new MailFolder(mailbox.Id, "old", "Old", 0, 0, ParentProviderId: "inbox"),
                    new MailFolder(mailbox.Id, "child", "Child", 0, 0, ParentProviderId: "old") })
                    vm.Folders.Add(new(folder, id));
                var message = new MailMessage(mailbox.Id, id + "-match", null, null, "inbox", "Budget", new("Person", "person@example.test"), [],
                    DateTimeOffset.Now, "Budget", "Budget", false, false, true, MailImportance.Normal, [id == "a" ? "Finance" : "Projects"], null, IsFlagged: true);
                await store.ApplySyncPageAsync(id, new([message, message with { ProviderId = id + "-excluded", FolderId = "child" }], null, false), token);
                await store.UpsertWorkspaceItemsAsync("drive-file", id, "index", new[] {
                    new CloudFile(id + "-file", "Budget", 10, null, id, ParentPath: "/drive/root:/Projects/Active"),
                    new CloudFile(id + "-old-file", "Budget", 10, null, id, ParentPath: "/drive/root:/Projects/Old/Child") }, item => item.ProviderId, item => item.Name, token);
            }
            vm.SearchText = "Budget type:mail type:drive account:a account:b category:Finance category:Projects is:unread is:flagged notin:{Inbox/Old}";
            await ((AsyncCommand)vm.SearchCommand).ExecuteAsync();
            Assert.Null(vm.SearchError);
            var mails = vm.GlobalSearchResults.Where(item => item.Value is MailMessage).Select(item => (MailMessage)item.Value!).ToArray();
            Assert.Equal(new[] { "a-match", "b-match" }, mails.Select(item => item.ProviderId).Order().ToArray());
            Assert.All(vm.GlobalSearchResults.Where(item => item.Value is CloudFile), item => Assert.NotEqual("c", ((CloudFile)item.Value!).AccountId));
            Assert.Contains(vm.GlobalSearchResults, item => item.Value is CloudFile);
            vm.SearchText = "Budget type:drive account:a account:b in:Projects notin:{Projects/Old}";
            await ((AsyncCommand)vm.SearchCommand).ExecuteAsync();
            Assert.Null(vm.SearchError);
            Assert.Equal(new[] { "a-file", "b-file" }, vm.GlobalSearchResults.Select(item => ((CloudFile)item.Value!).ProviderId).Order().ToArray());
            var unrelated = Enumerable.Range(0, 600).Select(index => new CloudFile("unrelated-" + index, "Budget", 10, null, "a", ParentPath: "/Other")).ToArray();
            await store.UpsertWorkspaceItemsAsync("drive-file", "a", "index", unrelated, item => item.ProviderId, item => item.Name, token);
            var limited = await store.SearchFilteredDriveFilesAsync(SearchQuery.Parse("Budget type:drive in:{a::Projects} notin:{a::Projects/Old}"), ["a", "b"], 1, token);
            Assert.Equal("a-file", Assert.Single(limited).ProviderId);
            Assert.Empty(await store.SearchFilteredDriveFilesAsync(SearchQuery.Parse("type:drive notin:{/}"), ["a"], 1, token));
            Assert.Equal(new[] { "Finance", "Projects" }, await store.GetMailCategoriesAsync(token));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("a::Inbox", "a", "Inbox", true)]
    [InlineData("a::Inbox", "b", "Inbox", false)]
    [InlineData("Inbox/Old", "a", "Inbox/Old/Child", true)]
    [InlineData("Inbox/Old", "a", "Inbox/Older", false)]
    public void FolderMatchingRespectsOwnersAndPathBoundaries(string choice, string owner, string actual, bool match) =>
        Assert.Equal(match, MainWindowViewModel.SearchPathMatches(choice, owner, actual, true));
}

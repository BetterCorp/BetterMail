using System.Reflection;
using BetterMail.Core;
using Microsoft.Data.Sqlite;

namespace BetterMail.Tests;

public sealed class SearchReadTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task SearchesReadCommittedMailWhileSyncWriterAndOtherReadersAreBlocked()
    {
        await WithStoreAsync(async (store, mailbox, token) =>
        {
            var gate = Field<SemaphoreSlim>(store, "_gate");
            var folderGate = Field<SemaphoreSlim>(store, "_folderReadGate");
            var workspaceGate = Field<SemaphoreSlim>(store, "_workspaceReadGate");
            await gate.WaitAsync(token);
            await folderGate.WaitAsync(token);
            await workspaceGate.WaitAsync(token);
            try
            {
                using var transaction = Field<SqliteConnection>(store, "_connection").BeginTransaction();
                using var command = transaction.Connection!.CreateCommand();
                command.CommandText = "UPDATE messages SET subject = 'Uncommitted';";
                await command.ExecuteNonQueryAsync(token);
                foreach (var search in new[]
                {
                    store.SearchAsync("Budget", cancellationToken: token),
                    store.SearchMailboxAsync(mailbox.Id, "Budget", cancellationToken: token),
                    store.SearchMailboxAsync(mailbox.Id, "", cancellationToken: token),
                    store.SearchFilteredMailAsync(SearchQuery.Parse("Budget type:mail"),
                        [new(mailbox.Id, "inbox")], cancellationToken: token)
                })
                {
                    var messages = await search.WaitAsync(TimeSpan.FromSeconds(5), token);
                    Assert.Equal("Budget", Assert.Single(messages).Subject);
                }
            }
            finally { workspaceGate.Release(); folderGate.Release(); gate.Release(); }
        });
    }

    [Fact]
    public async Task SearchesQueuedBeforeIndexMigrationUseTheCommittedIndexWhenTheyRun()
    {
        await WithStoreAsync(async (store, mailbox, token) =>
        {
            // Warm the search connection, then migrate its pending reads across the schema change.
            Assert.Single(await store.SearchAsync("Budget", cancellationToken: token));
            Assert.True(await store.RunMaintenanceBatchAsync(token));
            var searchGate = Field<SemaphoreSlim>(store, "_searchReadGate");
            await searchGate.WaitAsync(token);
            Task<IReadOnlyList<MailMessage>> mailboxSearch;
            Task<IReadOnlyList<MailMessage>> filteredSearch;
            try
            {
                mailboxSearch = store.SearchMailboxAsync(mailbox.Id, "Budget", cancellationToken: token);
                filteredSearch = store.SearchFilteredMailAsync(SearchQuery.Parse("Budget type:mail"),
                    [new(mailbox.Id, "inbox")], cancellationToken: token);
                Assert.True(await store.RunMaintenanceBatchAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            finally { searchGate.Release(); }
            Assert.Single(await mailboxSearch.WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Single(await filteredSearch.WaitAsync(TimeSpan.FromSeconds(5), token));
        });
    }

    [Fact]
    public async Task LegacySearchSnapshotSurvivesIndexRemovalWhileTheQueryIsBeingBuilt()
    {
        await WithStoreAsync(async (store, _, token) =>
        {
            Assert.True(await store.RunMaintenanceBatchAsync(token));
            var snapshotReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var search = QueryAsync(store, optimized =>
            {
                Assert.False(optimized);
                snapshotReady.SetResult();
                release.Wait(TimeSpan.FromSeconds(5), token);
                return "WHERE rowid IN (SELECT rowid FROM message_search WHERE message_search MATCH 'Budget')";
            }, token);
            try
            {
                await snapshotReady.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.True(await store.RunMaintenanceBatchAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            finally { release.Set(); }
            Assert.Single(await search.WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Single(await store.SearchAsync("Budget", cancellationToken: token));
        });
    }

    [Fact]
    public async Task CancellingExecutingSqlInterruptsSearchWithoutBlockingWritesOrTheNextSearch()
    {
        await WithStoreAsync(async (store, mailbox, token) =>
        {
            await store.SearchAsync("Budget", cancellationToken: token);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Field<SqliteConnection>(store, "_searchReadConnection").CreateFunction("review_started", () =>
            {
                started.TrySetResult();
                return 1;
            });
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var obsolete = QueryAsync(store, _ => """
                WHERE rowid IN (
                    WITH RECURSIVE numbers(n) AS (
                        VALUES(review_started()) UNION ALL SELECT n + 1 FROM numbers WHERE n < 100000000
                    ) SELECT sum(n) FROM numbers
                )
                """, cancelled.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                // An executing search must not retain the writer gate, even before cancellation.
                await store.SaveMailboxAsync(mailbox, token).WaitAsync(TimeSpan.FromSeconds(5), token);
                var next = store.SearchAsync("Budget", cancellationToken: token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete.WaitAsync(TimeSpan.FromSeconds(5), token));
                Assert.Single(await next.WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            finally { cancelled.Cancel(); }
        });
    }

    [Fact]
    public async Task CancellingAQueuedSearchDoesNotPoisonTheFollowingSearch()
    {
        await WithStoreAsync(async (store, _, token) =>
        {
            var gate = Field<SemaphoreSlim>(store, "_searchReadGate");
            await gate.WaitAsync(token);
            try
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                var obsolete = store.SearchAsync("Budget", cancellationToken: cancelled.Token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
            }
            finally { gate.Release(); }
            Assert.Single(await store.SearchAsync("Budget", cancellationToken: token));
        });
    }

    private static Task<IReadOnlyList<MailMessage>> QueryAsync(EncryptedMailStore store, Func<bool, string> where, CancellationToken token) =>
        (Task<IReadOnlyList<MailMessage>>)typeof(EncryptedMailStore).GetMethod("QuerySearchMessagesAsync", PrivateInstance)!
            .Invoke(store, [where, 500, false, token, Array.Empty<(string Name, object Value)>()])!;

    private static T Field<T>(EncryptedMailStore store, string name) =>
        (T)typeof(EncryptedMailStore).GetField(name, PrivateInstance)!.GetValue(store)!;

    private static async Task WithStoreAsync(Func<EncryptedMailStore, Mailbox, CancellationToken, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-search-read-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('D', 64));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            await store.SaveFoldersAsync(mailbox.Id, [new(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")], token);
            var message = new MailMessage(mailbox.Id, "message", null, null, "inbox", "Budget",
                new("Sender", "sender@example.test"), [], DateTimeOffset.UtcNow, "Budget", "Budget",
                false, false, false, MailImportance.Normal, [], null);
            await store.ApplySyncPageAsync("seed", new([message], null, false), token);
            await test(store, mailbox, token);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

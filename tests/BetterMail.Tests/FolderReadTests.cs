using System.Reflection;
using System.Security.Cryptography;
using BetterMail.Core;
using Microsoft.Data.Sqlite;

namespace BetterMail.Tests;

public sealed class FolderReadTests
{
    [Fact]
    public async Task FolderPageReadsCommittedSnapshotWithoutWaitingForSyncWriter()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-folder-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            var message = new MailMessage(mailbox.Id, "message", "thread", null, "inbox", "Committed subject",
                new("Sender", "sender@example.com"), [], DateTimeOffset.UtcNow, "Preview", "Body", false, false, false, MailImportance.Normal, [], null);
            await store.ApplySyncPageAsync("seed", new([message], null, false), token);
            var gate = (SemaphoreSlim)typeof(EncryptedMailStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            var writer = (SqliteConnection)typeof(EncryptedMailStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            // Hold both the actual sync gate and an uncommitted write transaction. The old
            // single-connection implementation cannot finish the page read until release.
            await gate.WaitAsync(token);
            try
            {
                await using var transaction = writer.BeginTransaction();
                await using var command = writer.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE messages SET subject='Uncommitted subject';";
                await command.ExecuteNonQueryAsync(token);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var page = await store.GetMessagesPageAsync([new(mailbox.Id, "inbox")], cancellationToken: token)
                    .WaitAsync(TimeSpan.FromSeconds(5), token);
                TestContext.Current.TestOutputHelper!.WriteLine($"Folder page during active writer: {timer.ElapsedMilliseconds} ms");
                Assert.Equal("Committed subject", Assert.Single(page.Messages).Subject);
                Assert.Null(page.Messages[0].Body);
                await transaction.CommitAsync(token);
            }
            finally { gate.Release(); }
            var latest = await store.GetMessagesPageAsync([new(mailbox.Id, "inbox")], cancellationToken: token);
            Assert.Equal("Uncommitted subject", Assert.Single(latest.Messages).Subject);
            // New navigation cancels an obsolete read waiting behind a previous folder read.
            var readerGate = (SemaphoreSlim)typeof(EncryptedMailStore).GetField("_folderReadGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            await readerGate.WaitAsync(token);
            try
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                var pending = store.GetMessagesPageAsync([], cancellationToken: cancelled.Token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            finally { readerGate.Release(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

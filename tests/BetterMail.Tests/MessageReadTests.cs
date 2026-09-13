using System.Reflection;
using System.Diagnostics;
using BetterMail.App;
using BetterMail.Core;
using Microsoft.Data.Sqlite;

namespace BetterMail.Tests;

public sealed class MessageReadTests
{
    [Fact]
    public async Task FullBodyAndThreadLoadWhileSyncAndFolderNavigationAreBlocked()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-message-read-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('E', 64));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            var body = "<p>Full cached message</p>" + string.Concat(Enumerable.Repeat("<p>Details beyond the preview.</p>", 400));
            var message = new MailMessage(mailbox.Id, "message", "thread", null, "inbox", "Subject",
                new("Sender", "sender@example.test"), [], DateTimeOffset.UtcNow, "Short preview", body, true, true, false, MailImportance.Normal, [], null);
            var reply = message with { ProviderId = "reply", ReceivedAt = message.ReceivedAt.AddMinutes(1) };
            await store.ApplySyncPageAsync("seed", new([message, reply], null, false), token);
            var gate = Field<SemaphoreSlim>(store, "_gate");
            var folders = Field<SemaphoreSlim>(store, "_folderReadGate");
            var writer = Field<SqliteConnection>(store, "_connection");
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            vm.Accounts.Add(account); vm.Mailboxes.Add(mailbox);
            await gate.WaitAsync(token);
            await folders.WaitAsync(token);
            try
            {
                await using var transaction = writer.BeginTransaction();
                await using var command = writer.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE messages SET subject='Not committed yet';";
                await command.ExecuteNonQueryAsync(token);
                var clock = Stopwatch.StartNew();
                var cached = await store.GetMessageAsync(mailbox.Id, message.ProviderId, token).WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.Equal(body, cached!.Body);
                Assert.Equal("Subject", cached.Subject);
                var thread = await store.GetThreadMessagesAsync(ConversationThread.ThreadIdentity(message), token).WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.Equal(2, thread.Count);
                Assert.All(thread, item => Assert.Null(item.Body));
                vm.Messages.Add(message with { Body = null });
                vm.SelectedMessage = vm.Messages[0];
                async Task WaitForBodyAsync()
                {
                    while (vm.ConversationThread.SelectedMessage?.Message.Body != body ||
                           vm.ConversationThread.SelectedMessage?.BodyHtml.Contains("Full cached message", StringComparison.Ordinal) != true)
                        await Task.Delay(10, token);
                }
                await WaitForBodyAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.Equal(body, vm.SelectedMessage!.Body);
                TestContext.Current.TestOutputHelper!.WriteLine($"Full body, thread and reading pane while writer/folder gates held: {clock.ElapsedMilliseconds} ms");
                await transaction.CommitAsync(token);
            }
            finally
            {
                vm.SelectedMessage = null;
                folders.Release(); gate.Release();
            }
            Assert.Equal("Not committed yet", (await store.GetMessageAsync(mailbox.Id, message.ProviderId, token))!.Subject);
            var readerGate = Field<SemaphoreSlim>(store, "_messageReadGate");
            await readerGate.WaitAsync(token);
            try
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                var obsolete = store.GetMessageAsync(mailbox.Id, message.ProviderId, cancelled.Token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
            }
            finally { readerGate.Release(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static T Field<T>(EncryptedMailStore store, string name) =>
        (T)typeof(EncryptedMailStore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
}

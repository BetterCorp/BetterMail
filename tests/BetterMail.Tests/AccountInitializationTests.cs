using System.Collections.Concurrent;
using System.Reflection;
using BetterMail.App;
using BetterMail.Core;
using BetterMail.Microsoft365;
using Microsoft.Identity.Client.Extensions.Msal;

namespace BetterMail.Tests;

public sealed class AccountInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousCacheWaitReturnsControlAndCanBeCancelledOrTimedOut(bool timeout)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-account-init-" + Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var options = new Microsoft365Options("11111111-1111-1111-1111-111111111111", directory);
            // Simulate a native secure-store call that cannot itself accept cancellation.
            var initialization = Microsoft365AuthService.CreateAsync(options, _ =>
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10), token);
                var cache = MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("test.cache", directory)
                    .WithUnprotectedFile().Build()).GetAwaiter().GetResult();
                exited.TrySetResult();
                return Task.FromResult(cache);
            }, timeout ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10), cancelled.Token);
            Assert.False(initialization.IsCompleted);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (timeout)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => initialization.WaitAsync(TimeSpan.FromSeconds(5), token));
                Assert.Contains("timed out", error.Message);
                Assert.Contains("cached mail is available", error.Message);
                Assert.Contains("Re-authenticate", error.Message);
            }
            else
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            Assert.False(exited.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            // Wait for the fake native call to finish before cleaning up its temporary files.
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CachedMailIsAccessibleBeforeProviderSetupCompletesAndAfterItFails()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-local-startup-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('D', 64));
            await store.InitializeAsync(token);
            var account = new MailAccount("unavailable-provider", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            await store.SaveFoldersAsync(mailbox.Id, [new(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")], token);
            var message = new MailMessage(mailbox.Id, "message", null, null, "inbox", "Cached mail",
                new("Sender", "sender@example.test"), [], DateTimeOffset.UtcNow, "Preview", "Body",
                false, true, false, MailImportance.Normal, [], null);
            await store.ApplySyncPageAsync("seed", new([message], null, false), token);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            var gates = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(MainWindowViewModel)
                .GetField("_providerInitializationLocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var gate = gates.GetOrAdd(account.ProviderId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            Task initialization;
            try
            {
                initialization = vm.InitializeAsync();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(vm.ShowFullScreenLoader) && !vm.ShowFullScreenLoader) ready.TrySetResult();
                };
                if (!vm.ShowFullScreenLoader) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.False(initialization.IsCompleted);
                Assert.False(vm.IsBusy);
                Assert.Equal("Cached mail", Assert.Single(vm.Messages).Subject);
                vm.SearchText = "Cached type:mail";
                await ((AsyncCommand)vm.SearchCommand).ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.Contains(vm.GlobalSearchResults, item => item.Value is MailMessage);
            }
            finally { gate.Release(); }
            await initialization.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(vm.ShowFullScreenLoader);
            Assert.Contains("not installed", vm.Error);
            Assert.Equal("Cached mail", Assert.Single(vm.Messages).Subject);
            vm.SelectedMessage = null;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

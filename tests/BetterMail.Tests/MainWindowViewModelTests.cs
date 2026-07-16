using BetterMail.App;
using BetterMail.Core;
using Avalonia.Input;

namespace BetterMail.Tests;

public sealed class MainWindowViewModelTests
{
    [Theory]
    [InlineData(390, 0)]
    [InlineData(719, 0)]
    [InlineData(720, 1)]
    [InlineData(1199, 1)]
    [InlineData(1200, 2)]
    public void ChoosesResponsiveShellLayout(double width, int expected) =>
        Assert.Equal((ResponsiveLayoutMode)expected, MainWindow.LayoutModeFor(width));

    [Theory]
    [InlineData(839, false)]
    [InlineData(840, true)]
    public void KeepsInlineMailActionsOnlyWhenTheyFit(double width, bool expected) =>
        Assert.Equal(expected, MainWindow.UsesInlineMailActions(width));

    [Theory]
    [InlineData(Key.Escape, false, true, false, false, 1)]
    [InlineData(Key.Escape, true, false, true, false, 2)]
    [InlineData(Key.Escape, false, false, true, false, 0)]
    [InlineData(Key.Back, true, false, false, false, 3)]
    [InlineData(Key.Back, true, false, false, true, 0)]
    [InlineData(Key.Delete, true, false, false, false, 4)]
    [InlineData(Key.Delete, true, false, false, true, 0)]
    [InlineData(Key.Back, false, false, false, false, 0)]
    public void RoutesShellKeysOnlyToTheirSafeContext(
        Key key,
        bool isMailModule,
        bool isSettingsOpen,
        bool hasMailBackStage,
        bool isTextInput,
        int expected) =>
        Assert.Equal(expected, (int)MainWindow.ShellActionFor(
            key, isMailModule, isSettingsOpen, hasMailBackStage, isTextInput));

    [Fact]
    public async Task ShowsOnboardingAfterStartupWithoutAnAccount()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);

        Assert.False(viewModel.ShowOnboarding);
        Assert.True(viewModel.ShowFullScreenLoader);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.ShowOnboarding);
        Assert.False(viewModel.ShowFullScreenLoader);
    }

    [Fact]
    public async Task SettingsShowsLocalAndCloudMailboxStatistics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-stats-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [new(mailbox.Id, "inbox", "Inbox", 3, 10, "inbox")], cancellationToken);
            await store.ApplySyncPageAsync(
                "stats",
                new MailSyncPage([Message(mailbox.Id, "inbox", "Local mail", "Body")], null, false),
                cancellationToken);
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, new RecordingProvider());
            await viewModel.InitializeAsync();

            viewModel.OpenSettingsCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.HasMailStatistics && !viewModel.IsLoadingMailStatistics, cancellationToken);

            var statistics = Assert.Single(viewModel.MailStatistics);
            Assert.Equal(1, statistics.SyncedMessages);
            Assert.Equal(10, statistics.CloudMessages);
            Assert.Equal(1, statistics.SyncedUnread);
            Assert.Equal(3, statistics.CloudUnread);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DismissesTheCurrentError()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, "Copy me");

        viewModel.DismissError();

        Assert.False(viewModel.HasError);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public void LoadsRemotePicturesOnlyAfterExplicitAction()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null)
        {
            SelectedMessage = Message(
                "mailbox",
                "inbox",
                "Pictures",
                "<p>Safe text</p><img src='https://images.example/banner.png'>")
        };

        Assert.True(viewModel.HasBlockedRemoteContent);
        Assert.DoesNotContain("images.example", Decode(viewModel.SelectedMessageBodyUri));

        viewModel.AllowRemoteContentCommand.Execute(null);

        Assert.False(viewModel.HasBlockedRemoteContent);
        Assert.Contains("https://images.example/banner.png", Decode(viewModel.SelectedMessageBodyUri));
    }

    [Fact]
    public void PersistsPerSenderSignaturesAndUsesTheSavedDefaultSender()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true, CanSendAs: true);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(primary);
        viewModel.Mailboxes.Add(shared);
        viewModel.ConfigureSenderPreferences(
            "Legacy signature",
            shared.Id,
            new Dictionary<string, string> { [shared.Id] = "Shared signature" });

        var sharedSettings = Assert.Single(viewModel.SenderSettings, sender => sender.MailboxId == shared.Id);
        var primarySettings = Assert.Single(viewModel.SenderSettings, sender => sender.MailboxId == primary.Id);
        Assert.True(sharedSettings.IsDefault);
        Assert.Contains("Shared signature", sharedSettings.NewMailSignature.Html);
        Assert.Contains("Legacy signature", primarySettings.NewMailSignature.Html);

        primarySettings.NewMailSignature = sharedSettings.NewMailSignature;
        viewModel.SetDefaultSenderCommand.Execute(primarySettings);
        ComposeRequest? requested = null;
        viewModel.ComposeRequested += request => requested = request;
        viewModel.ComposeCommand.Execute(null);

        Assert.Equal(primary.Id, viewModel.DefaultSenderMailboxId);
        Assert.Contains("Shared signature", viewModel.GetSenderSignatures()[primary.Id]);
        Assert.Equal(primary.Id, requested?.MailboxId);
        Assert.Equal(account.AccountId, requested?.AccountId);
        Assert.Empty(requested?.Body ?? "");
    }

    [Fact]
    public void NewPrimaryAndSharedMailboxesUseTheReadOnlyBetterMailDefault()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true, CanSendAs: true);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(primary);
        viewModel.Mailboxes.Add(shared);
        viewModel.ConfigureSenderPreferences("", null, null, [], new Dictionary<string, MailboxSignaturePreferences>());

        Assert.True(viewModel.Signatures[0].IsReadOnly);
        Assert.Equal(SignatureCatalog.DefaultId, viewModel.Signatures[0].Id);
        foreach (var sender in viewModel.SenderSettings)
        {
            Assert.Equal(SignatureCatalog.DefaultId, sender.NewMailSignature.Id);
            Assert.Equal(SignatureCatalog.DefaultId, sender.ReplySignature.Id);
            Assert.Equal(SignatureCatalog.DefaultId, sender.ReplyAllSignature.Id);
            Assert.Equal(SignatureCatalog.DefaultId, sender.ForwardSignature.Id);
        }
    }

    [Fact]
    public void SignatureTemplateSelectionShowsTheRealHtmlBeforeCreating()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var template = Assert.Single(
            viewModel.SignatureTemplates,
            static candidate => candidate.Id == "professional");

        viewModel.SelectedSignatureTemplate = template;

        Assert.Contains("Product Director", Decode(viewModel.SelectedSignatureTemplatePreviewUri));
        viewModel.CreateSignatureFromTemplateCommand.Execute(null);
        Assert.Equal("Professional", viewModel.SelectedSignature?.Name);
        Assert.Contains("Product Director", viewModel.SelectedSignature?.Html);
    }

    [Fact]
    public async Task ReplyAllExcludesEveryLinkedAddressAndSafelyDeduplicatesRecipients()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var second = account with { AccountId = "second", EmailAddress = "other-me@example.com" };
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Accounts.Add(second);
        viewModel.Mailboxes.Add(mailbox);
        viewModel.Mailboxes.Add(shared);
        viewModel.SelectedMessage = Message(mailbox.Id, "inbox", "RE: Planning", "Body") with
        {
            From = new MailAddress("Sender", "sender@example.com"),
            To =
            [
                new("Me", "ME@example.com"),
                new("Shared", "shared@example.com"),
                new("Other me", "other-me@example.com"),
                new("Sender duplicate", "SENDER@example.com"),
                new("Colleague", "colleague@example.com"),
                new("Colleague duplicate", "COLLEAGUE@example.com"),
                new("Invalid", "not an address")
            ],
            Cc =
            [
                new("Shared Cc", "SHARED@example.com"),
                new("Colleague duplicate from Cc", "colleague@example.com"),
                new("Cc Person", "cc@example.com")
            ],
            IsRead = true
        };
        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;

        viewModel.ReplyAllCommand.Execute(null);
        await WaitUntilAsync(() => request is not null, cancellationToken);

        Assert.Equal("sender@example.com", request!.To);
        Assert.Equal("colleague@example.com; cc@example.com", request.Cc);
        Assert.Empty(request.Bcc);
        Assert.Equal("RE: Planning", request.Subject);
        Assert.Equal(account.AccountId, request.AccountId);
        Assert.Equal(mailbox.Id, request.MailboxId);
        var recipients = $"{request.To};{request.Cc};{request.Bcc}";
        Assert.DoesNotContain("me@example.com", recipients, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared@example.com", recipients, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other-me@example.com", recipients, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not an address", recipients, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplyAllDoesNotOpenComposeWhenOnlyLinkedRecipientsRemain()
    {
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);
        viewModel.SelectedMessage = Message(mailbox.Id, "inbox", "Subject", "Body") with
        {
            From = new MailAddress("Me", "me@example.com"),
            To = [new("Me", "ME@example.com")],
            IsRead = true
        };
        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;

        viewModel.ReplyAllCommand.Execute(null);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Null(request);
        Assert.Contains("no external recipients", viewModel.Error);
    }

    [Fact]
    public async Task ConversationSelectionReconcilesStableRowsAndRoutesExistingActions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);
        var first = Message(mailbox.Id, "inbox", "Thread", "<p>First</p>") with
        {
            ProviderId = "first",
            ConversationId = "thread",
            IsRead = true
        };
        var second = Message(mailbox.Id, "inbox", "RE: Thread", "<p>Second</p>") with
        {
            ProviderId = "second",
            ConversationId = "thread",
            IsRead = true
        };
        viewModel.Messages.Add(first);
        viewModel.Messages.Add(second);
        viewModel.SelectedMessage = first;

        var thread = Assert.Single(viewModel.ConversationThread.Threads);
        var secondItem = thread.Messages[1];
        viewModel.ConversationThread.SelectMessageCommand.Execute(secondItem);
        Assert.Same(first, viewModel.SelectedMessage);
        Assert.Same(secondItem, viewModel.ConversationThread.SelectedMessage);

        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;
        viewModel.ConversationThread.ForwardCommand.Execute(null);
        await WaitUntilAsync(() => request is not null, cancellationToken);
        Assert.Equal("Fwd: RE: Thread", request!.Subject);
    }

    [Fact]
    public async Task RapidSelectionCancelsTheOldReadDelayAndKeepsTheNewSelection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-rapid-read-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 2, 2, "inbox");
            var first = Message(mailbox.Id, "inbox", "First", "First body") with
            {
                ProviderId = "first",
                ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var second = Message(mailbox.Id, "inbox", "Second", "Second body") with
            {
                ProviderId = "second",
                ReceivedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync("test", new MailSyncPage([first, second], null, false), cancellationToken);

            var provider = new RecordingProvider();
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider, TimeSpan.FromMilliseconds(80));
            await viewModel.InitializeAsync();
            var oldSelection = viewModel.SelectedMessage!;
            var newSelection = viewModel.Messages.Single(message => message.ProviderId != oldSelection.ProviderId);
            viewModel.SelectedMessage = newSelection;

            await WaitUntilAsync(
                () => provider.MarkedReadIds.Count == 1 && viewModel.SelectedMessage?.IsRead == true,
                cancellationToken);

            Assert.Equal(newSelection.ProviderId, Assert.Single(provider.MarkedReadIds));
            Assert.Equal(newSelection.ProviderId, viewModel.SelectedMessage?.ProviderId);
            Assert.True(viewModel.SelectedMessage?.IsRead);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GlobalSearchStreamsEveryWorkspaceCategoryWithoutBusyOverlay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-global-search-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var secondAccount = new MailAccount("microsoft365", "second", "tenant", "other@example.com", "Other", ProviderCapabilities.Mail);
            var secondMailbox = new Mailbox(secondAccount.AccountId, secondAccount.EmailAddress, secondAccount.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveAccountAsync(secondAccount, cancellationToken);
            await store.SaveMailboxAsync(secondMailbox, cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([
                    Message(mailbox.Id, "inbox", "Planning mail", "Planning body"),
                    Message(secondMailbox.Id, "inbox", "Planning update", "Planning body")
                ], null, false),
                cancellationToken);
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null,
                new RecordingProvider(), workspaceProvider: new FakeWorkspaceProvider());
            viewModel.Accounts.Add(account);
            viewModel.Accounts.Add(secondAccount);

            viewModel.SearchText = "Planning";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning && viewModel.GlobalSearchResults.Count >= 6, cancellationToken);

            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.IsGlobalSearchOpen);
            Assert.Equal(
                ["Calendar", "Mail", "Notes", "OneDrive", "People", "To Do"],
                viewModel.GlobalSearchResults.Select(result => result.Category).Distinct().Order().ToArray());
            Assert.All(viewModel.GlobalSearchResults, result => Assert.False(string.IsNullOrWhiteSpace(result.AccountGroup)));
            var mailResults = viewModel.GlobalSearchResults.Where(result => result.Category == "Mail").ToArray();
            Assert.Equal(2, mailResults.Length);
            var mailResult = Assert.Single(mailResults, result => result.AccountGroup.Contains(account.EmailAddress));
            Assert.Contains(account.EmailAddress, mailResult.AccountGroup);
            Assert.DoesNotContain(account.EmailAddress, mailResult.Subtitle);
            Assert.True(mailResult.StartsAccountGroup);

            viewModel.OpenGlobalSearchGroupCommand.Execute(mailResult);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchOpen, cancellationToken);
            Assert.Single(viewModel.Messages);
            Assert.Equal(mailbox.Id, viewModel.Messages[0].MailboxId);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task KeyboardNavigationChangesSelectionWithoutFetchingOrReplacingRows()
    {
        var provider = new RecordingProvider();
        var viewModel = new MainWindowViewModel(
            null, "data", _ => { }, _ => { }, null, provider);
        var messages = new[]
        {
            Message("mailbox", "inbox", "First", "One") with { ProviderId = "first", IsRead = true },
            Message("mailbox", "inbox", "Second", "Two") with { ProviderId = "second", IsRead = true },
            Message("mailbox", "inbox", "Third", "Three") with { ProviderId = "third", IsRead = true }
        };
        foreach (var message in messages)
        {
            viewModel.Messages.Add(message);
        }
        viewModel.SelectedMessage = messages[1];

        viewModel.SelectNextMessageCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.SelectedMessage?.ProviderId == "third",
            TestContext.Current.CancellationToken);
        viewModel.SelectPreviousMessageCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.SelectedMessage?.ProviderId == "second",
            TestContext.Current.CancellationToken);

        Assert.Equal(messages, viewModel.Messages);
        Assert.Equal(0, provider.GetMessageCalls);
    }

    [Fact]
    public async Task MarksSelectedUnreadMessageAsReadAfterDelay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-read-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 1, 1, "inbox");
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([Message(mailbox.Id, "inbox", "Unread", "<b>Body</b>")], null, false),
                cancellationToken);

            var provider = new RecordingProvider();
            var viewModel = new MainWindowViewModel(
                store,
                directory,
                _ => { },
                _ => { },
                null,
                provider,
                TimeSpan.FromMilliseconds(60));

            await viewModel.InitializeAsync();
            var bodyRefreshes = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.SelectedMessageBodyUri))
                {
                    bodyRefreshes++;
                }
            };
            await WaitUntilAsync(() => provider.MarkedRead && viewModel.SelectedMessage?.IsRead == true, cancellationToken);

            Assert.True(provider.MarkedRead);
            Assert.True(viewModel.SelectedMessage?.IsRead);
            Assert.Equal(0, bodyRefreshes);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncUpdatesRowsWithoutReplacingTheSelectedMessageBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-sync-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox");
            var original = Message(mailbox.Id, "inbox", "Original", "<b>Stable body</b>") with
            {
                ProviderId = "message",
                ConversationId = "thread",
                IsRead = true
            };
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync("test", new MailSyncPage([original], null, false), cancellationToken);

            var updated = original with { Subject = "Updated", IsFlagged = true };
            var sent = original with
            {
                ProviderId = "sent",
                FolderId = "sentitems",
                Subject = "Sent reply",
                ReceivedAt = original.ReceivedAt.AddMinutes(1)
            };
            var provider = new BlockingSyncProvider(inbox, updated, sent);
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();
            var bodyRefreshes = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.SelectedMessageBodyUri))
                {
                    bodyRefreshes++;
                }
            };
            var transientSelectionClears = 0;
            viewModel.Messages.CollectionChanged += (_, args) =>
            {
                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace &&
                    args.OldItems?.OfType<MailMessage>().Any(message => message.ProviderId == "message") == true)
                {
                    viewModel.SelectedMessage = null;
                    transientSelectionClears += viewModel.SelectedMessage is null ? 1 : 0;
                }
            };

            viewModel.SyncCommand.Execute(null);
            viewModel.SyncCommand.Execute(null);
            await provider.Entered.Task.WaitAsync(cancellationToken);

            Assert.True(viewModel.IsSyncing);
            Assert.False(viewModel.IsBusy);
            Assert.Equal("message", viewModel.SelectedMessage?.ProviderId);

            provider.Release.TrySetResult();
            await WaitUntilAsync(() => !viewModel.IsSyncing && viewModel.SelectedMessage?.Subject == "Updated", cancellationToken);

            Assert.Equal(1, provider.SyncCalls);
            Assert.Equal(1, provider.MaxConcurrent);
            Assert.True(viewModel.SelectedMessage?.IsFlagged);
            Assert.Equal(2, viewModel.ConversationThread.SelectedThread?.Messages.Count);
            Assert.Contains(viewModel.ConversationThread.SelectedThread!.Messages, item => item.Message.ProviderId == "sent");
            Assert.Equal(0, bodyRefreshes);
            Assert.Equal(0, transientSelectionClears);

            viewModel.SearchText = "Updated";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning && viewModel.GlobalSearchResults.Count == 1, cancellationToken);
            var searchSnapshot = viewModel.GlobalSearchResults.ToArray();
            viewModel.CloseGlobalSearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchOpen, cancellationToken);

            await WaitUntilAsync(() => viewModel.SyncCommand.CanExecute(null), cancellationToken);
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => provider.SyncCalls == 2 && !viewModel.IsSyncing, cancellationToken);

            Assert.False(viewModel.IsGlobalSearchOpen);
            Assert.Equal(searchSnapshot, viewModel.GlobalSearchResults);
            viewModel.ClearGlobalSearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.HasSearchText, cancellationToken);
            Assert.Equal("", viewModel.SearchText);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NavigatesFoldersSelectsMessagesAndRendersTheirBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-ui-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 1, 1, "inbox");
            var archive = new MailFolder(mailbox.Id, "archive", "Archive", 0, 1);
            var projects = new MailFolder(mailbox.Id, "projects", "Projects", 0, 1);
            var client = new MailFolder(mailbox.Id, "client", "Client", 0, 1, ParentProviderId: projects.ProviderId);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox, archive, projects, client], cancellationToken);
            await store.ApplySyncPageAsync("test", new MailSyncPage(
            [
                Message(mailbox.Id, "inbox", "Inbox message", "<b>Inbox body</b>"),
                Message(mailbox.Id, "archive", "Archive message", "<b>Archive body</b>")
            ], null, false), cancellationToken);

            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            await viewModel.InitializeAsync();
            Assert.Equal("Inbox message", viewModel.SelectedMessage?.Subject);
            var group = Assert.Single(viewModel.FolderGroups);
            var projectsNode = Assert.Single(group.Folders, node => node.Item.ProviderId == "projects");
            Assert.Equal("client", Assert.Single(projectsNode.Children).Item.ProviderId);

            var archiveItem = Assert.Single(viewModel.Folders, folder => folder.ProviderId == "archive");
            viewModel.SelectFolderCommand.Execute(archiveItem);
            await WaitUntilAsync(() => viewModel.CurrentFolderName == "Archive", cancellationToken);

            Assert.Equal("Archive message", viewModel.SelectedMessage?.Subject);
            var html = Decode(viewModel.SelectedMessageBodyUri);
            Assert.Contains("<b>Archive body</b>", html);

            viewModel.ShowUnifiedInboxCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.CurrentFolderName == "Inbox", cancellationToken);
            Assert.Equal("Inbox message", viewModel.SelectedMessage?.Subject);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FlagsAndArchivesTheSelectedMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-actions-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 1, 1, "inbox");
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([Message(mailbox.Id, "inbox", "Action message", "<b>Body</b>")], null, false),
                cancellationToken);

            var provider = new RecordingProvider();
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();

            viewModel.ToggleFlagCommand.Execute(null);
            await WaitUntilAsync(() => provider.Flagged == true && viewModel.SelectedMessage?.IsFlagged == true, cancellationToken);

            provider.MoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.ArchiveCommand.Execute(null);
            await WaitUntilAsync(() => provider.MoveDestination == "archive", cancellationToken);
            Assert.True(viewModel.IsMailActionRunning);
            Assert.Equal("Archiving...", viewModel.MailActionStatus);
            provider.MoveRelease.SetResult();
            await WaitUntilAsync(() => provider.MarkedRead && provider.MoveDestination == "archive" && viewModel.Messages.Count == 0, cancellationToken);

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsMailActionRunning);
            Assert.Null(viewModel.SelectedMessage);
            Assert.Empty(await store.GetMessagesAsync(cancellationToken: cancellationToken));
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GlobalMailSearchExcludesArchivesUntilRequested()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-archive-search-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox");
            var archive = new MailFolder(mailbox.Id, "archive-id", "Archive", 0, 1, "archive");
            var onlineArchive = new MailFolder(mailbox.Id, "online-archive", "Online Archive - Person", 0, 1);
            await store.ApplySyncPageAsync("test", new MailSyncPage([
                Message(mailbox.Id, inbox.ProviderId, "Needle inbox", "Needle"),
                Message(mailbox.Id, archive.ProviderId, "Needle archive", "Needle"),
                Message(mailbox.Id, onlineArchive.ProviderId, "Needle online archive", "Needle")
            ], null, false), cancellationToken);

            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            viewModel.Accounts.Add(account);
            viewModel.Mailboxes.Add(mailbox);
            foreach (var folder in new[] { inbox, archive, onlineArchive })
            {
                viewModel.Folders.Add(new MailFolderItem(folder, mailbox.DisplayName));
            }
            viewModel.SelectedSearchScope = "Mail";
            viewModel.SearchText = "Needle";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning, cancellationToken);

            Assert.Single(viewModel.GlobalSearchResults);
            viewModel.IncludeArchivedMailInSearch = true;
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning && viewModel.GlobalSearchResults.Count == 3, cancellationToken);
            Assert.Equal(3, viewModel.GlobalSearchResults.Count);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OpensMicrosoft365WorkspaceModules()
    {
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var workspace = new FakeWorkspaceProvider();
        var viewModel = new MainWindowViewModel(
            null,
            "data",
            _ => { },
            _ => { },
            null,
            new RecordingProvider(),
            workspaceProvider: workspace);
        viewModel.Accounts.Add(account);

        viewModel.ShowCalendarCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ActiveModule == "Calendar" &&
                  viewModel.CalendarWorkspace?.CalendarGroups.Count == 1 &&
                  viewModel.CalendarWorkspace.DayColumns.SelectMany(day => day.Events).Count() == 1,
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsCalendarModule);
        Assert.False(viewModel.IsMailModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var calendarWorkspace = viewModel.CalendarWorkspace;
        Assert.Same(calendarWorkspace, viewModel.ActiveWorkspace);

        var second = account with { AccountId = "second", EmailAddress = "second@example.com" };
        viewModel.Accounts.Add(second);
        viewModel.ShowCalendarCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.CalendarWorkspace?.CalendarGroups.Count == 2,
            TestContext.Current.CancellationToken);
        Assert.Same(calendarWorkspace, viewModel.CalendarWorkspace);

        viewModel.Accounts.Remove(second);
        viewModel.ShowCalendarCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.CalendarWorkspace?.CalendarGroups.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.Same(calendarWorkspace, viewModel.CalendarWorkspace);

        viewModel.ShowNotesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ActiveModule == "Notes" &&
                  viewModel.NotesWorkspace?.AccountRoots.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsNotesModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var notesWorkspace = viewModel.NotesWorkspace;
        Assert.Same(notesWorkspace, viewModel.ActiveWorkspace);

        viewModel.Accounts.Add(second);
        viewModel.ShowNotesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.NotesWorkspace?.AccountRoots.Count == 2,
            TestContext.Current.CancellationToken);
        Assert.Same(notesWorkspace, viewModel.NotesWorkspace);

        viewModel.Accounts.Remove(second);
        viewModel.ShowNotesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.NotesWorkspace?.AccountRoots.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.Same(notesWorkspace, viewModel.NotesWorkspace);

        viewModel.ShowTasksCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ActiveModule == "To Do" &&
                  viewModel.TasksWorkspace?.AccountGroups.Count == 1 &&
                  viewModel.TasksWorkspace.VisibleTasks.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsTasksModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var tasksWorkspace = viewModel.TasksWorkspace;
        Assert.Same(tasksWorkspace, viewModel.ActiveWorkspace);

        viewModel.Accounts.Add(second);
        viewModel.ShowTasksCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.TasksWorkspace?.AccountGroups.Count == 2,
            TestContext.Current.CancellationToken);
        Assert.Same(tasksWorkspace, viewModel.TasksWorkspace);

        viewModel.Accounts.Remove(second);
        viewModel.ShowTasksCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.TasksWorkspace?.AccountGroups.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.Same(tasksWorkspace, viewModel.TasksWorkspace);
    }

    [Fact]
    public async Task IntegratesStableDriveWorkspaceAndAttachesUsingOwningAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var account = new MailAccount(
            "microsoft365", "drive-account", "tenant", "drive@example.com", "Drive",
            ProviderCapabilities.Mail | ProviderCapabilities.Files);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var workspace = new FakeWorkspaceProvider();
        var viewModel = new MainWindowViewModel(
            null,
            "data",
            _ => { },
            _ => { },
            null,
            new RecordingProvider(),
            workspaceProvider: workspace);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);

        viewModel.ShowFilesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.DriveWorkspace?.CurrentItems.Count == 1,
            cancellationToken);

        Assert.True(viewModel.IsFilesModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var driveWorkspace = viewModel.DriveWorkspace!;
        var second = account with { AccountId = "second-drive", EmailAddress = "second@example.com" };
        viewModel.Accounts.Add(second);
        await viewModel.RefreshConnectedAccountsAsync();
        Assert.Same(driveWorkspace, viewModel.DriveWorkspace);
        Assert.Equal(2, driveWorkspace.Roots.Count);
        viewModel.Accounts.Remove(second);
        await viewModel.RefreshConnectedAccountsAsync();
        Assert.Same(driveWorkspace, viewModel.DriveWorkspace);
        Assert.Single(driveWorkspace.Roots);

        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;
        driveWorkspace.SelectedItem = Assert.Single(driveWorkspace.CurrentItems);
        driveWorkspace.ChooseItemCommand.Execute(null);
        await WaitUntilAsync(() => request is not null, cancellationToken);

        Assert.Equal(account.AccountId, request!.AccountId);
        Assert.Equal(mailbox.Id, request.MailboxId);
        var attachment = Assert.Single(request.Attachments!);
        Assert.Equal("plan.txt", attachment.Name);
        Assert.Equal("plan", System.Text.Encoding.UTF8.GetString(attachment.ContentBytes));
        Assert.Equal(1, workspace.DownloadCount);

        request = null;
        var oversized = driveWorkspace.SelectedProviderItem! with
        {
            Item = driveWorkspace.SelectedProviderItem.Item with
            {
                Size = DraftAttachment.MaximumSizeBytes + 1
            }
        };
        await viewModel.AttachDriveItemAsync(oversized, cancellationToken);
        Assert.Null(request);
        Assert.Contains("150 MB", viewModel.Error);
        Assert.Equal(1, workspace.DownloadCount);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await viewModel.AttachDriveItemAsync(driveWorkspace.SelectedProviderItem, cancelled.Token);
        Assert.Null(request);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcilesDraftsAtSyncBoundariesAndCompletesMappedSendAndDeleteOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-draft-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "draft-account", "tenant", "draft@example.com", "Draft",
                ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            var local = new LocalDraft(
                "stable-local-id",
                account.AccountId,
                mailbox.Id,
                "recipient@example.com",
                "",
                "",
                "First version",
                "Body",
                [],
                DateTimeOffset.UtcNow);
            await store.SaveLocalDraftAsync(local, cancellationToken);
            var storedMessage = Message(mailbox.Id, "inbox", "Keep selected", "Body");
            await store.ApplySyncPageAsync(
                mailbox.Id,
                new MailSyncPage([storedMessage], null, false),
                cancellationToken);

            var provider = new LifecycleDraftProvider();
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();
            await WaitUntilAsync(
                () => viewModel.Drafts.SingleOrDefault()?.ProviderDraftId is not null,
                cancellationToken);

            var selected = Assert.Single(viewModel.Messages);
            viewModel.SelectedMessage = selected;
            await viewModel.SaveLocalDraftAsync(local with
            {
                Subject = "Second version",
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1)
            });
            Assert.Equal(0, provider.UpdateCount);

            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(
                () => !viewModel.IsSyncing && provider.UpdateCount == 1 &&
                      viewModel.Drafts.Single().Subject == "Second version",
                cancellationToken);
            Assert.NotNull(viewModel.SelectedMessage);
            Assert.Equal(selected.ProviderId, viewModel.SelectedMessage.ProviderId);

            var sender = new ComposeSender(account, mailbox);
            await viewModel.SendDraftAsync(
                sender,
                local.Id,
                new DraftMessage(
                    "Second version",
                    [new("Recipient", "recipient@example.com")],
                    "Body",
                    false));
            Assert.Equal(1, provider.SendDraftCount);
            Assert.Equal(0, provider.SendCount);
            Assert.Empty(viewModel.Drafts);
            Assert.Empty(await store.GetLocalDraftsAsync(cancellationToken));

            var deleteDraft = local with
            {
                Id = "delete-once",
                Subject = "Delete me",
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(2)
            };
            await viewModel.SaveLocalDraftAsync(deleteDraft);
            await WaitUntilAsync(
                () => viewModel.SyncCommand.CanExecute(null),
                cancellationToken);
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(
                () => !viewModel.IsSyncing &&
                      viewModel.Drafts.SingleOrDefault()?.ProviderDraftId is not null,
                cancellationToken);
            await viewModel.DeleteLocalDraftAsync(deleteDraft.Id);
            await viewModel.DeleteLocalDraftAsync(deleteDraft.Id);
            Assert.Equal(1, provider.DeleteCount);
            Assert.Empty(await store.GetLocalDraftsAsync(cancellationToken));

            await viewModel.SaveLocalDraftAsync(local with
            {
                Id = "invalid-recipient",
                To = "bob",
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(3)
            });
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
            Assert.True(string.IsNullOrWhiteSpace(viewModel.Error));
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AggregatesSavedAndDiscoveredPeopleWhileKeepingAccountFailuresLocal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-people-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var good = new MailAccount(
                "microsoft365", "good", "tenant", "good@example.com", "Good",
                ProviderCapabilities.Mail | ProviderCapabilities.Contacts);
            var bad = new MailAccount(
                "microsoft365", "bad", "tenant", "bad@example.com", "Bad",
                ProviderCapabilities.Mail | ProviderCapabilities.Contacts);
            var mailbox = new Mailbox(good.AccountId, good.EmailAddress, good.DisplayName);
            var badMailbox = new Mailbox(bad.AccountId, bad.EmailAddress, bad.DisplayName);
            await store.SaveAccountAsync(good, cancellationToken);
            await store.SaveAccountAsync(bad, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveMailboxAsync(badMailbox, cancellationToken);
            await store.SaveFoldersAsync(
                mailbox.Id,
                [new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 2, "inbox")],
                cancellationToken);
            await store.ApplySyncPageAsync(
                "people",
                new MailSyncPage(
                [
                    Message(mailbox.Id, "inbox", "Known", "Known") with
                    {
                        ProviderId = "known-message",
                        From = new MailAddress("Known Person", "known@example.com"),
                        IsRead = true
                    },
                    Message(mailbox.Id, "inbox", "New", "New") with
                    {
                        ProviderId = "new-message",
                        From = new MailAddress("New Person", "new@example.com"),
                        IsRead = true
                    }
                ],
                null,
                false),
                cancellationToken);

            var workspace = new AggregatedContactsProvider(good.AccountId, bad.AccountId);
            var viewModel = new MainWindowViewModel(
                store,
                directory,
                _ => { },
                _ => { },
                null,
                new RecordingProvider(),
                workspaceProvider: workspace);
            await viewModel.InitializeAsync();

            viewModel.ShowContactsCommand.Execute(null);
            await WaitUntilAsync(
                () => viewModel.People.Count == 2 && viewModel.HasPeopleErrors,
                cancellationToken);

            var saved = Assert.Single(viewModel.People, static person => person.IsSaved);
            var discovered = Assert.Single(viewModel.People, static person => !person.IsSaved);
            Assert.Equal("K", saved.AvatarText);
            Assert.Equal("N", discovered.AvatarText);
            Assert.Equal("known@example.com", saved.EmailText);
            Assert.Equal("new@example.com", discovered.EmailText);
            Assert.Contains(good.EmailAddress, discovered.ProvenanceText);
            Assert.Contains(bad.EmailAddress, viewModel.PeopleErrorText);
            Assert.Contains("re-authenticate", viewModel.PeopleErrorText);
            Assert.False(viewModel.IsBusy);

            viewModel.EditContactCommand.Execute(saved);
            await WaitUntilAsync(() => viewModel.IsEditingContact, cancellationToken);
            viewModel.ContactName = "Known Updated";
            viewModel.ContactEmails = "known@example.com; second@example.com";
            viewModel.SaveContactCommand.Execute(null);
            await WaitUntilAsync(
                () => workspace.Updated &&
                    viewModel.People.Any(person => person.IsSaved && person.DisplayName == "Known Updated"),
                cancellationToken);

            var updated = Assert.Single(viewModel.People, static person => person.IsSaved);
            viewModel.RequestDeleteContactCommand.Execute(updated);
            await WaitUntilAsync(() => viewModel.IsConfirmingContactDelete, cancellationToken);
            viewModel.ConfirmDeleteContactCommand.Execute(null);
            await WaitUntilAsync(
                () => workspace.Deleted && viewModel.People.All(static person => !person.IsSaved),
                cancellationToken);

            viewModel.NewContactCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.IsCreatingContact, cancellationToken);
            viewModel.ContactName = "Created Person";
            viewModel.ContactEmails = "created@example.com";
            viewModel.SaveContactCommand.Execute(null);
            await WaitUntilAsync(
                () => workspace.Created && viewModel.People.Any(person => person.DisplayName == "Created Person"),
                cancellationToken);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static MailMessage Message(string mailboxId, string folderId, string subject, string body) => new(
        mailboxId, subject, null, null, folderId, subject,
        new MailAddress("Sender", "sender@example.com"), [], DateTimeOffset.UtcNow,
        body, body, true, false, false, MailImportance.Normal, [], null);

    private static string Decode(Uri uri) => System.Text.Encoding.UTF8.GetString(
        Convert.FromBase64String(uri.OriginalString.Split(',')[1]));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(20, cancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class RecordingProvider : IMailProvider
    {
        public bool MarkedRead { get; private set; }
        public List<string> MarkedReadIds { get; } = [];
        public bool? Flagged { get; private set; }
        public string? MoveDestination { get; private set; }
        public int GetMessageCalls { get; private set; }
        public TaskCompletionSource? MoveRelease { get; set; }

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            Mailbox mailbox,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([]);

        public Task<MailSyncPage> SyncFolderAsync(
            MailAccount account,
            Mailbox mailbox,
            string folderId,
            string? cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailSyncPage([], cursor, false));

        public Task MarkReadAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            MarkedRead = isRead;
            if (isRead)
            {
                MarkedReadIds.Add(messageId);
            }
            return Task.CompletedTask;
        }

        public Task<MailMessage> GetMessageAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            GetMessageCalls++;
            return Task.FromResult(Message(mailbox.Id, "inbox", "(no subject)", ""));
        }

        public async Task MoveMessageAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            string destinationFolderId,
            CancellationToken cancellationToken = default)
        {
            MoveDestination = destinationFolderId;
            if (MoveRelease is not null)
            {
                await MoveRelease.Task.WaitAsync(cancellationToken);
            }
        }

        public Task SetFlaggedAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            bool isFlagged,
            CancellationToken cancellationToken = default)
        {
            Flagged = isFlagged;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);

        public Task SendAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class BlockingSyncProvider(MailFolder folder, params MailMessage[] messages) : IMailProvider
    {
        private int _concurrent;
        public int SyncCalls { get; private set; }
        public int MaxConcurrent { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([folder]);

        public async Task<MailSyncPage> SyncFolderAsync(
            MailAccount account,
            Mailbox mailbox,
            string folderId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new MailSyncPage(messages, cursor, false);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(messages[0]);
        public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
        public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeWorkspaceProvider : IWorkspaceProvider
    {
        public int DownloadCount { get; private set; }

        public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarInfo>>(
                [new("calendar", "Calendar", "#0F6CBD", true, account.AccountId)]);

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
            MailAccount account,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>(
                [new("event", "calendar", "Planning", from.AddHours(1), from.AddHours(2), "Room 1")]);

        public Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContactInfo>>(
                query.Contains("Planning", StringComparison.OrdinalIgnoreCase)
                    ? [new("contact", "Planning Person", ["planning@example.com"], account.AccountId)]
                    : []);

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>(
                [new("planning-task", "default", "Planning task", null, false, account.AccountId)]);

        public Task<IReadOnlyList<TaskListInfo>> GetTaskListsAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskListInfo>>(
                [new($"list-{account.AccountId}", "Tasks", account.AccountId, "defaultList")]);

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account,
            TaskListInfo list,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>(
                [new($"task-{account.AccountId}", list.ProviderId, "Follow up", null, false, account.AccountId)]);

        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudFile>>(
                [new("planning-file", "Planning.docx", 1024, null, account.AccountId, account.ProviderId, "Documents")]);

        public Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(
            MailAccount account,
            CloudDriveItem? parent = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudDriveItem>>(
                parent is null
                    ?
                    [
                        new(
                            $"file-{account.AccountId}",
                            "plan.txt",
                            4,
                            false,
                            null,
                            null,
                            account.AccountId,
                            account.ProviderId,
                            "text/plain",
                            "Documents")
                    ]
                    : []);

        public async Task DownloadFileAsync(
            MailAccount account,
            CloudDriveItem file,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCount++;
            await destination.WriteAsync("plan"u8.ToArray(), cancellationToken);
        }

        public Task<IReadOnlyList<NoteInfo>> GetNotesAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteInfo>>(
                [new("planning-note", "Planning note", DateTimeOffset.UtcNow, null, account.AccountId, account.ProviderId)]);
    }

    private sealed class LifecycleDraftProvider : IMailProvider
    {
        private readonly List<CloudDraft> _drafts = [];
        private int _version;

        public bool SupportsCloudDrafts => true;
        public int UpdateCount { get; private set; }
        public int SendDraftCount { get; private set; }
        public int SendCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(
            MailAccount account,
            Mailbox mailbox,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudDraft>>(_drafts.ToArray());

        public Task<CloudDraft> CreateDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            var created = Cloud(account, mailbox, $"server-{Guid.NewGuid():N}", draft);
            _drafts.Add(created);
            return Task.FromResult(created);
        }

        public Task<CloudDraft> UpdateDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            var updated = Cloud(account, mailbox, draftId, draft);
            _drafts[_drafts.FindIndex(candidate => candidate.ProviderId == draftId)] = updated;
            return Task.FromResult(updated);
        }

        public Task DeleteDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            _drafts.RemoveAll(candidate => candidate.ProviderId == draftId);
            return Task.CompletedTask;
        }

        public Task SendDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            CancellationToken cancellationToken = default)
        {
            SendDraftCount++;
            _drafts.RemoveAll(candidate => candidate.ProviderId == draftId);
            return Task.CompletedTask;
        }

        public Task SendAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.CompletedTask;
        }

        private CloudDraft Cloud(
            MailAccount account,
            Mailbox mailbox,
            string id,
            DraftMessage message) =>
            new(
                id,
                account.AccountId,
                mailbox.Id,
                message,
                DateTimeOffset.UtcNow.AddMilliseconds(++_version),
                $"etag-{_version}");

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task<MailSyncPage> SyncFolderAsync(
            MailAccount account, Mailbox mailbox, string folderId, string? cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailSyncPage([], null, false));
        public Task MarkReadAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isRead,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<MailMessage>(new NotSupportedException());
        public Task MoveMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFlaggedAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isFlagged,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
    }

    private sealed class AggregatedContactsProvider(string goodAccountId, string badAccountId) : IWorkspaceProvider
    {
        private ContactInfo? _contact = new(
            "saved-contact",
            "Known Person",
            ["known@example.com"],
            goodAccountId);

        public bool Updated { get; private set; }
        public bool Deleted { get; private set; }
        public bool Created { get; private set; }

        public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarInfo>>([]);

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
            MailAccount account,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);

        public Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default)
        {
            if (account.AccountId == badAccountId)
            {
                return Task.FromException<IReadOnlyList<ContactInfo>>(
                    new InvalidOperationException("Consent expired."));
            }
            IReadOnlyList<ContactInfo> contacts = _contact is null ? [] : [_contact];
            return Task.FromResult(contacts);
        }

        public Task<ContactInfo> UpdateContactAsync(
            MailAccount account,
            string contactId,
            ContactDraft draft,
            CancellationToken cancellationToken = default)
        {
            Updated = true;
            _contact = new ContactInfo(contactId, draft.DisplayName, draft.EmailAddresses, account.AccountId);
            return Task.FromResult(_contact);
        }

        public Task<ContactInfo> CreateContactAsync(
            MailAccount account,
            ContactDraft draft,
            CancellationToken cancellationToken = default)
        {
            Created = true;
            _contact = new ContactInfo("created-contact", draft.DisplayName, draft.EmailAddresses, account.AccountId);
            return Task.FromResult(_contact);
        }

        public Task DeleteContactAsync(
            MailAccount account,
            ContactInfo contact,
            CancellationToken cancellationToken = default)
        {
            Deleted = true;
            _contact = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>([]);

        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudFile>>([]);

        public Task<IReadOnlyList<NoteInfo>> GetNotesAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteInfo>>([]);
    }
}

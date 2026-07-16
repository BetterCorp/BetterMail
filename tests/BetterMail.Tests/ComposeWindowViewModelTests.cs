using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class ComposeWindowViewModelTests
{
    [Fact]
    public async Task RecipientPickerSearchesAddsAndRemovesBadges()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new ComposeWindowViewModel(
            [account], [mailbox], new ComposeRequest(), (_, _, _) => Task.CompletedTask,
            searchRecipients: (_, _) => Task.FromResult<IReadOnlyList<RecipientSuggestion>>([
                new("Ada Lovelace", "ada@example.com", "Saved contact")
            ]));

        viewModel.ToField.Query = "ada";
        for (var attempt = 0; attempt < 100 && !viewModel.ToField.IsSearchOpen; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var suggestion = Assert.Single(viewModel.ToField.Suggestions);
        viewModel.ToField.AddSuggestionCommand.Execute(suggestion);

        var token = Assert.Single(viewModel.ToField.Tokens);
        Assert.Equal("Ada Lovelace <ada@example.com>", viewModel.To);
        viewModel.ToField.RemoveTokenCommand.Execute(token);
        Assert.Empty(viewModel.ToField.Tokens);
    }

    [Fact]
    public void ParsesMultipleRecipientsAndRejectsInvalidAddresses()
    {
        var recipients = ComposeWindowViewModel.ParseRecipients("Alice <alice@example.com>; bob@example.com");

        Assert.Equal(2, recipients.Count);
        Assert.Equal("Alice", recipients[0].Name);
        Assert.Equal("bob@example.com", recipients[1].Address);
        Assert.Throws<FormatException>(() => ComposeWindowViewModel.ParseRecipients("not an address"));
    }

    [Fact]
    public async Task SendsCcBccAndAttachments()
    {
        DraftMessage? sent = null;
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new ComposeWindowViewModel(
            [account],
            [mailbox],
            new ComposeRequest("to@example.com", "Subject", "Body"),
            (_, _, draft) =>
            {
                sent = draft;
                return Task.CompletedTask;
            })
        {
            Cc = "cc@example.com",
            Bcc = "bcc@example.com"
        };
        viewModel.AddAttachment(new DraftAttachment("notes.txt", "text/plain", "hello"u8.ToArray()));

        viewModel.SendCommand.Execute(null);
        for (var attempt = 0; attempt < 50 && sent is null; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(sent);
        Assert.True(sent.IsHtml);
        Assert.Contains("Body", sent.Body);
        Assert.Equal("cc@example.com", Assert.Single(sent.Cc!).Address);
        Assert.Equal("bcc@example.com", Assert.Single(sent.Bcc!).Address);
        Assert.Equal("notes.txt", Assert.Single(sent.Attachments!).Name);
    }

    [Fact]
    public async Task AutosavesAndHandsStableLocalIdToTheSendOwner()
    {
        LocalDraft? saved = null;
        string? deleted = null;
        string? sentId = null;
        var sent = false;
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new ComposeWindowViewModel(
            [account],
            [mailbox],
            new ComposeRequest(DraftId: "draft-one"),
            (_, id, _) =>
            {
                sentId = id;
                sent = true;
                return Task.CompletedTask;
            },
            draft =>
            {
                saved = draft;
                return Task.CompletedTask;
            },
            id =>
            {
                deleted = id;
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(20));

        viewModel.To = "to@example.com";
        viewModel.Subject = "Autosaved";
        viewModel.Body = "Still typing";
        for (var attempt = 0; attempt < 50 && saved is null; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(saved);
        Assert.Equal(account.AccountId, saved.AccountId);
        Assert.Equal(mailbox.Id, saved.MailboxId);
        Assert.Equal("Saved", viewModel.DraftStatus);

        viewModel.SendCommand.Execute(null);
        for (var attempt = 0; attempt < 50 && !sent; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(sent);
        Assert.Equal(saved.Id, sentId);
        Assert.Null(deleted);
    }

    [Fact]
    public void AcceptsGraphLargeAttachmentsAndRejectsFilesOver150Mb()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new ComposeWindowViewModel(
            [account], [mailbox], new ComposeRequest(), (_, _, _) => Task.CompletedTask);

        Assert.True(viewModel.ValidateAttachmentSize("large.zip", DraftAttachment.MaximumSizeBytes));
        Assert.False(viewModel.ValidateAttachmentSize("too-large.zip", DraftAttachment.MaximumSizeBytes + 1));
        Assert.Contains("150 MB", viewModel.Error);
    }

    [Fact]
    public void UsesTheSelectedSenderSignatureAndDoesNotDuplicateSavedDraftSignatures()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true, CanSendAs: true);
        SignatureContent Signature(ComposeSender sender, ComposeIntent intent) => new(
            sender.Mailbox.Id,
            sender.Mailbox.Id == shared.Id ? $"Shared {intent} signature" : $"Primary {intent} signature");
        var viewModel = new ComposeWindowViewModel(
            [account],
            [primary, shared],
            new ComposeRequest(Body: "Hello", AccountId: account.AccountId, MailboxId: shared.Id),
            (_, _, _) => Task.CompletedTask,
            signatureForSender: Signature);

        Assert.Contains("Shared NewMail signature", viewModel.Body);
        viewModel.SelectedSender = Assert.Single(viewModel.Senders, sender => sender.Mailbox.Id == primary.Id);
        Assert.Contains("Primary NewMail signature", viewModel.Body);
        Assert.DoesNotContain("Shared NewMail signature", viewModel.Body);

        var reopened = new ComposeWindowViewModel(
            [account],
            [primary, shared],
            new ComposeRequest(
                Body: viewModel.Body,
                DraftId: "saved",
                AccountId: account.AccountId,
                MailboxId: primary.Id),
            (_, _, _) => Task.CompletedTask,
            signatureForSender: Signature);
        reopened.SelectedSender = Assert.Single(reopened.Senders, sender => sender.Mailbox.Id == shared.Id);

        Assert.Contains("Hello", reopened.Body);
        Assert.Equal(1, reopened.Body.Split("Primary NewMail signature").Length - 1);
    }

    [Theory]
    [InlineData(ComposeIntent.Reply)]
    [InlineData(ComposeIntent.ReplyAll)]
    [InlineData(ComposeIntent.Forward)]
    public void PlacesActionSpecificSignatureBeforeQuotedContent(ComposeIntent intent)
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new ComposeWindowViewModel(
            [account],
            [mailbox],
            new ComposeRequest(Body: "Quoted content", IsHtml: true, Intent: intent),
            (_, _, _) => Task.CompletedTask,
            signatureForSender: (_, composeIntent) => new("signature", $"<strong>{composeIntent}</strong>"));

        Assert.True(viewModel.Body.IndexOf(intent.ToString(), StringComparison.Ordinal) <
                    viewModel.Body.IndexOf("Quoted content", StringComparison.Ordinal));
    }
}

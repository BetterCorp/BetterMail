using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class RecipientMoveTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public async Task MovingBetweenEveryRecipientFieldPreservesNamesPendingTextAndSavedAndSentRecipients(int sourceIndex, int targetIndex)
    {
        LocalDraft? saved = null;
        DraftMessage? sent = null;
        var account = new MailAccount("microsoft365", "account", "tenant", "author@example.com", "Author", ProviderCapabilities.Mail);
        var vm = new ComposeWindowViewModel([account], [new(account.AccountId, account.EmailAddress, "Author")],
            new ComposeRequest(), (_, _, draft) => { sent = draft; return Task.CompletedTask; },
            draft => { saved = draft; return Task.CompletedTask; }, autosaveDelay: TimeSpan.Zero);
        var source = vm.RecipientFields[sourceIndex];
        var target = vm.RecipientFields[targetIndex];
        source.SetSerialized("\"Doe, Jane\" <jane@example.com>; stays@example.com");
        target.SetSerialized("already@example.com");
        source.Query = "source-query@example.com";
        target.Query = "target-query@example.com";
        var token = source.Tokens[0];

        Assert.True(vm.MoveRecipient(source, target, token));
        Assert.DoesNotContain(token, source.Tokens);
        Assert.Same(token, target.Tokens[1]);
        Assert.Equal("source-query@example.com", source.Query);
        Assert.Equal("target-query@example.com", target.Query);
        Assert.NotNull(saved);
        var savedFields = new[] { saved.To, saved.Cc, saved.Bcc };
        Assert.DoesNotContain("jane@example.com", savedFields[sourceIndex]);
        var moved = Assert.Single(ComposeWindowViewModel.ParseRecipients(savedFields[targetIndex]), recipient => recipient.Address == "jane@example.com");
        Assert.Equal("Doe, Jane", moved.Name);
        if (vm.ToField.Tokens.Count == 0 && vm.ToField.Query.Length == 0) vm.To = "required@example.com";
        await ((AsyncCommand)vm.SendCommand).ExecuteAsync();
        Assert.NotNull(sent);
        var sentFields = new[] { sent.To, sent.Cc!, sent.Bcc! };
        Assert.DoesNotContain(sentFields[sourceIndex], recipient => recipient.Address == "jane@example.com");
        Assert.Contains(sentFields[targetIndex], recipient => recipient.Address == "jane@example.com" && recipient.Name == "Doe, Jane");
    }

    [Fact]
    public void MovingToAnExistingAddressMergesTheDuplicateIgnoringCase()
    {
        var vm = new ComposeWindowViewModel([], [], new ComposeRequest(To: "Existing <JANE@example.com>", Cc: "Jane <jane@example.com>"),
            (_, _, _) => Task.CompletedTask);
        var existing = Assert.Single(vm.ToField.Tokens);
        Assert.True(vm.MoveRecipient(vm.CcField, vm.ToField, Assert.Single(vm.CcField.Tokens)));
        Assert.Empty(vm.CcField.Tokens);
        Assert.Same(existing, Assert.Single(vm.ToField.Tokens));
    }

    [Fact]
    public void RejectsSameFieldStaleTokensForeignFieldsAndBusyCompose()
    {
        var vm = new ComposeWindowViewModel([], [], new ComposeRequest(Cc: "Jane <jane@example.com>"), (_, _, _) => Task.CompletedTask);
        var token = Assert.Single(vm.CcField.Tokens);
        var other = new ComposeWindowViewModel([], [], new ComposeRequest(), (_, _, _) => Task.CompletedTask);
        Assert.False(vm.MoveRecipient(vm.CcField, vm.CcField, token));
        Assert.False(vm.MoveRecipient(vm.CcField, other.ToField, token));
        Assert.False(vm.MoveRecipient(other.CcField, vm.ToField, token));
        Assert.False(vm.MoveRecipient(vm.CcField, vm.ToField, token with { }));
        vm.IsUploadingAttachment = true;
        Assert.False(vm.MoveRecipient(vm.CcField, vm.ToField, token));
        vm.IsUploadingAttachment = false;
        Assert.True(vm.MoveRecipient(vm.CcField, vm.ToField, token));
        Assert.False(vm.MoveRecipient(vm.CcField, vm.BccField, token));
        Assert.Empty(vm.CcField.Tokens);
        Assert.Empty(vm.BccField.Tokens);
        Assert.Same(token, Assert.Single(vm.ToField.Tokens));
    }

    [Fact]
    public async Task RejectsRecipientMovesWhileSending()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "author@example.com", "Author", ProviderCapabilities.Mail);
        var release = new TaskCompletionSource();
        var vm = new ComposeWindowViewModel([account], [new(account.AccountId, account.EmailAddress, "Author")],
            new ComposeRequest(To: "required@example.com", Cc: "jane@example.com"), (_, _, _) => release.Task);
        var sending = ((AsyncCommand)vm.SendCommand).ExecuteAsync();
        try
        {
            Assert.True(vm.IsSending);
            Assert.False(vm.MoveRecipient(vm.CcField, vm.ToField, Assert.Single(vm.CcField.Tokens)));
            Assert.Equal("jane@example.com", vm.Cc);
        }
        finally { release.SetResult(); await sending; }
    }
}

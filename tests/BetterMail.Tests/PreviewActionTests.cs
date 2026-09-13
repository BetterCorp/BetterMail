using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class PreviewActionTests
{
    [Fact]
    public void WindowActionPreferencesPersistIndependentlyAndDefaultToStayOpen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-preview-settings-" + Guid.NewGuid());
        try
        {
            var vm = new MainWindowViewModel(null, directory, _ => { }, _ => { }, null);
            Assert.All(vm.PreviewActionSettings, setting => Assert.False(vm.ShouldClosePreview(setting.Action)));
            vm.ConfigurePreviewActions(["Delete", "Reply", "unrecognized"]);
            AppPreferencesStore.Save(directory, new(ClosePreviewAfterActions: vm.GetClosePreviewActions()));
            var restored = new MainWindowViewModel(null, directory, _ => { }, _ => { }, null);
            restored.ConfigurePreviewActions(AppPreferencesStore.Load(directory).ClosePreviewAfterActions);
            Assert.True(restored.ShouldClosePreview(ConversationAction.Delete));
            Assert.True(restored.ShouldClosePreview(ConversationAction.Reply));
            Assert.False(restored.ShouldClosePreview(ConversationAction.Forward));
            Assert.False(restored.ShouldClosePreview(ConversationAction.ViewHeaders));
            restored.PreviewActionSettings.Single(item => item.Action == ConversationAction.Delete).Behavior = "Stay open";
            Assert.False(restored.ShouldClosePreview(ConversationAction.Delete));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(ConversationAction.Reply)]
    [InlineData(ConversationAction.ReplyAll)]
    [InlineData(ConversationAction.Forward)]
    public async Task ComposeAcceptanceFollowsComposerOpeningAndRejectsMissingAccounts(ConversationAction action)
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        var message = new MailMessage("box", "id", null, null, "inbox", "Subject", new MailAddress("Sender", "sender@example.test"),
            [], DateTimeOffset.Now, "Body", "Body", false, true, false, MailImportance.Normal, [], null);
        var opened = false;
        var accepted = false;
        vm.ComposeRequested += _ => opened = true;
        var request = new ConversationActionRequest(action, message, Accepted: () => { Assert.True(opened); accepted = true; });
        await vm.HandlePreviewActionAsync(request);
        Assert.False(accepted);
        vm.Accounts.Add(new("microsoft365", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Mail));
        await vm.HandlePreviewActionAsync(request);
        Assert.True(accepted);
    }

    [Fact]
    public async Task UnavailableMoveDoesNotClosePreview()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        var message = new MailMessage("box", "id", null, null, "inbox", "Subject", new MailAddress("Sender", "sender@example.test"),
            [], DateTimeOffset.Now, "Body", "Body", false, true, false, MailImportance.Normal, [], null);
        var accepted = false;
        await vm.HandlePreviewActionAsync(new(ConversationAction.Delete, message, Accepted: () => accepted = true));
        Assert.False(accepted);
    }
}

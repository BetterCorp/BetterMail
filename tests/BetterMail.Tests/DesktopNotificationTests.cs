using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class DesktopNotificationTests
{
    [Fact]
    public void NotifiesNewInboxMailOnceWithUnambiguousSharedMailboxContext()
    {
        var service = new RecordingNotificationService();
        var coordinator = new NewMailNotificationCoordinator(service);
        var context = Context(shared: true);
        var existing = Message(context, "existing", "Existing");
        var added = Message(context, "added", "Quarterly report");

        coordinator.Prime(context, [existing]);
        coordinator.Observe(context, [added, existing], enabled: true);
        coordinator.Observe(context, [added, existing], enabled: true);

        var notification = Assert.Single(service.Notifications);
        Assert.Equal(context.Account.EmailAddress, notification.AccountAddress);
        Assert.Equal(context.Mailbox.Address, notification.MailboxAddress);
        Assert.Equal(context.Folder.DisplayName, notification.FolderName);
        Assert.True(notification.IsSharedMailbox);
        Assert.Equal("Sender", notification.Sender);
        Assert.Equal("Quarterly report", notification.Subject);
    }

    [Fact]
    public void MetadataUpdatesAndDisabledArrivalDoNotNotifyLater()
    {
        var service = new RecordingNotificationService();
        var coordinator = new NewMailNotificationCoordinator(service);
        var context = Context(shared: false);
        var existing = Message(context, "existing", "Original");

        coordinator.Prime(context, [existing]);
        coordinator.Observe(
            context,
            [existing with { Subject = "Updated", IsRead = true, IsFlagged = true, ETag = "new" }],
            enabled: true);
        coordinator.Observe(
            context,
            [existing, Message(context, "while-disabled", "Silent")],
            enabled: false);
        coordinator.Observe(
            context,
            [existing, Message(context, "while-disabled", "Silent")],
            enabled: true);

        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void FirstObservationIsOnlyABaseline()
    {
        var service = new RecordingNotificationService();
        var coordinator = new NewMailNotificationCoordinator(service);
        var context = Context(shared: false);

        coordinator.Observe(
            context,
            [Message(context, "history-one", "Old"), Message(context, "history-two", "Older")],
            enabled: true);

        Assert.Empty(service.Notifications);
        Assert.True(coordinator.IsPrimed(context));
    }

    [Fact]
    public void NotificationSettingDefaultsOnAndPersistsOff()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-notifications-{Guid.NewGuid():N}");
        try
        {
            Assert.True(new AppPreferences().DesktopNotificationsEnabled);
            Assert.Equal("All mail", new AppPreferences().MailSyncRange);
            AppPreferencesStore.Save(
                directory,
                new AppPreferences(DesktopNotificationsEnabled: false));

            Assert.False(AppPreferencesStore.Load(directory).DesktopNotificationsEnabled);
            Assert.Equal("All mail", AppPreferencesStore.Load(directory).MailSyncRange);

            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");
            Assert.True(AppPreferencesStore.Load(directory).DesktopNotificationsEnabled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void WindowsMailNotificationsAreQueuedInsteadOfDiscarded()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "BetterMail.App", "DesktopNotifications.cs"));

        Assert.DoesNotContain("NifRealtime", source);
        Assert.DoesNotContain("NimDelete", source);
        Assert.Contains("data.uFlags = NifInfo;", source);
    }

    private static InboxNotificationContext Context(bool shared)
    {
        var account = new MailAccount(
            "microsoft365",
            "account",
            "tenant",
            "owner@example.com",
            "Owner",
            ProviderCapabilities.Mail);
        var mailbox = new Mailbox(
            account.AccountId,
            shared ? "team@example.com" : account.EmailAddress,
            shared ? "Team" : account.DisplayName,
            shared);
        return new InboxNotificationContext(
            account,
            mailbox,
            new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox"));
    }

    private static MailMessage Message(
        InboxNotificationContext context,
        string id,
        string subject) => new(
            context.Mailbox.Id,
            id,
            null,
            null,
            context.Folder.ProviderId,
            subject,
            new MailAddress("Sender", "sender@example.com"),
            [],
            DateTimeOffset.UtcNow,
            subject,
            subject,
            false,
            false,
            false,
            MailImportance.Normal,
            [],
            null);

    private sealed class RecordingNotificationService : IDesktopNotificationService
    {
        public List<DesktopNotification> Notifications { get; } = [];

        public ValueTask ShowAsync(DesktopNotification notification)
        {
            Notifications.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterMail.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("BetterMail repository root was not found.");
    }
}

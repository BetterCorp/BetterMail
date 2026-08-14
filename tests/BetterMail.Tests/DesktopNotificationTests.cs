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
        Assert.Equal(context.Mailbox.Id, notification.MailboxId);
        Assert.Equal(context.Folder.ProviderId, notification.FolderProviderId);
        Assert.Equal(added.ProviderId, notification.MessageProviderId);
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
    public void OnlyMessagesReceivedWithinThirtyMinutesNotifyAndAllBecomeSeen()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var service = new RecordingNotificationService();
        var coordinator = new NewMailNotificationCoordinator(service, () => now);
        var context = Context(shared: false);
        coordinator.Prime(context, []);

        var recent = Message(context, "recent", "Recent") with { ReceivedAt = now.AddMinutes(-29).AddSeconds(-59) };
        var boundary = Message(context, "boundary", "Boundary") with { ReceivedAt = now.AddMinutes(-30) };
        var old = Message(context, "old", "Old") with { ReceivedAt = now.AddMinutes(-30).AddSeconds(-1) };

        coordinator.Observe(context, [old, boundary, recent], enabled: true);
        coordinator.Observe(context, [old, boundary, recent], enabled: true);

        Assert.Equal(["Boundary", "Recent"], service.Notifications.Select(notification => notification.Subject));
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
                new AppPreferences(
                    DesktopNotificationsEnabled: false,
                    MailQuickActions: ["delete", "move", "junk", "archive"]));

            Assert.False(AppPreferencesStore.Load(directory).DesktopNotificationsEnabled);
            Assert.Equal("All mail", AppPreferencesStore.Load(directory).MailSyncRange);
            Assert.Equal(
                ["delete", "move", "junk", "archive"],
                AppPreferencesStore.Load(directory).MailQuickActions);

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
    public async Task WindowsMailNotificationsWaitForTheWindowHandle()
    {
        var attempts = 0;

        var handle = await WindowsDesktopNotificationService.WaitForOwnerHandleAsync(
            () => ++attempts == 3 ? 42 : 0,
            TimeSpan.Zero);

        Assert.Equal(42, handle);
        Assert.Equal(3, attempts);
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

}

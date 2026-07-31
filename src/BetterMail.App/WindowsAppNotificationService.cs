#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace BetterMail.App;

internal sealed class WindowsAppNotificationService : IDesktopNotificationService, IDisposable
{
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private readonly Action<string, string, string>? _activated;
    private bool _registered;

    public WindowsAppNotificationService(Action<string, string, string>? activated)
    {
        _activated = activated;
        _manager.NotificationInvoked += OnNotificationInvoked;
        _manager.Register();
        _registered = true;
    }

    public ValueTask ShowAsync(DesktopNotification notification)
    {
        var title = notification.IsSharedMailbox
            ? $"New mail - {notification.MailboxDisplayName} (shared)"
            : $"New mail - {notification.MailboxDisplayName}";
        var toast = new AppNotificationBuilder()
            .AddText(title)
            .AddText($"{notification.Sender} - {notification.Subject}")
            .AddText($"{notification.AccountAddress} / {notification.MailboxAddress} / {notification.FolderName}")
            .AddArgument("mailboxId", notification.MailboxId)
            .AddArgument("folderId", notification.FolderProviderId)
            .AddArgument("messageId", notification.MessageProviderId)
            .BuildNotification();
        _manager.Show(toast);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }
        _manager.NotificationInvoked -= OnNotificationInvoked;
        _manager.Unregister();
        _registered = false;
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("mailboxId", out var mailboxId) &&
            args.Arguments.TryGetValue("folderId", out var folderId) &&
            args.Arguments.TryGetValue("messageId", out var messageId))
        {
            _activated?.Invoke(mailboxId, folderId, messageId);
        }
    }
}
#endif

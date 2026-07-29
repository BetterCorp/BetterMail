#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace BetterMail.App;

internal sealed class WindowsAppNotificationService : IDesktopNotificationService, IDisposable
{
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private bool _registered;

    public WindowsAppNotificationService()
    {
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

    private static void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
    }
}
#endif

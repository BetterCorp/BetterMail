using System.Runtime.InteropServices;
using BetterMail.Core;

namespace BetterMail.App;

public sealed record DesktopNotification(
    string AccountAddress,
    string MailboxAddress,
    string MailboxDisplayName,
    bool IsSharedMailbox,
    string FolderName,
    string Sender,
    string Subject);

public interface IDesktopNotificationService
{
    ValueTask ShowAsync(DesktopNotification notification);
}

public sealed class NoOpDesktopNotificationService : IDesktopNotificationService
{
    public static NoOpDesktopNotificationService Instance { get; } = new();
    private NoOpDesktopNotificationService() { }
    public ValueTask ShowAsync(DesktopNotification notification) => ValueTask.CompletedTask;
}

public static class DesktopNotificationServices
{
    public static IDesktopNotificationService Create(Func<nint> ownerHandle) =>
        OperatingSystem.IsWindows()
            ? new WindowsDesktopNotificationService(ownerHandle)
            : NoOpDesktopNotificationService.Instance;
}

public sealed record InboxNotificationContext(
    MailAccount Account,
    Mailbox Mailbox,
    MailFolder Folder);

public sealed class NewMailNotificationCoordinator(IDesktopNotificationService service)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);

    public bool IsPrimed(InboxNotificationContext context)
    {
        lock (_gate)
        {
            return _seen.ContainsKey(FolderKey(context));
        }
    }

    public void Prime(InboxNotificationContext context, IEnumerable<MailMessage> messages)
    {
        if (!IsInbox(context.Folder))
        {
            return;
        }
        lock (_gate)
        {
            _seen[FolderKey(context)] = messages
                .Where(message => BelongsTo(context, message))
                .Select(MessageKey)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public void Observe(
        InboxNotificationContext context,
        IEnumerable<MailMessage> messages,
        bool enabled)
    {
        if (!IsInbox(context.Folder))
        {
            return;
        }

        List<MailMessage> added = [];
        lock (_gate)
        {
            var folderKey = FolderKey(context);
            if (!_seen.TryGetValue(folderKey, out var seen))
            {
                _seen[folderKey] = messages
                    .Where(message => BelongsTo(context, message))
                    .Select(MessageKey)
                    .ToHashSet(StringComparer.Ordinal);
                return;
            }
            foreach (var message in messages
                         .Where(message => BelongsTo(context, message))
                         .OrderBy(static message => message.ReceivedAt))
            {
                if (seen.Add(MessageKey(message)))
                {
                    added.Add(message);
                }
            }
        }

        if (!enabled)
        {
            return;
        }
        foreach (var message in added)
        {
            _ = DeliverAsync(new DesktopNotification(
                context.Account.EmailAddress,
                context.Mailbox.Address,
                context.Mailbox.DisplayName,
                context.Mailbox.IsShared,
                context.Folder.DisplayName,
                message.SenderDisplayName,
                string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject));
        }
    }

    private async Task DeliverAsync(DesktopNotification notification)
    {
        try
        {
            await service.ShowAsync(notification);
        }
        catch
        {
            // Notifications are optional and must never interrupt mail sync.
        }
    }

    private static bool IsInbox(MailFolder folder) =>
        folder.WellKnownName?.Equals("inbox", StringComparison.OrdinalIgnoreCase) == true;

    private static bool BelongsTo(InboxNotificationContext context, MailMessage message) =>
        message.MailboxId == context.Mailbox.Id &&
        message.FolderId == context.Folder.ProviderId;

    private static string FolderKey(InboxNotificationContext context) =>
        $"{context.Account.ProviderId}\n{context.Account.AccountId}\n{context.Mailbox.Id}\n{context.Folder.ProviderId}";

    private static string MessageKey(MailMessage message) =>
        $"{message.MailboxId}\n{message.ProviderId}";
}

internal sealed class WindowsDesktopNotificationService(Func<nint> ownerHandle)
    : IDesktopNotificationService
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NiifUser = 0x4;
    private const uint NiifRespectQuietTime = 0x80;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const uint LrDefaultSize = 0x40;
    private const uint IconId = 0xB377;
    private readonly object _gate = new();
    private bool _registered;
    private nint _applicationIcon;

    public ValueTask ShowAsync(DesktopNotification notification)
    {
        nint handle;
        try
        {
            handle = ownerHandle();
        }
        catch
        {
            return ValueTask.CompletedTask;
        }
        if (handle != 0)
        {
            _ = Task.Run(() => ShowInBackground(handle, notification));
        }
        return ValueTask.CompletedTask;
    }

    private void ShowInBackground(nint handle, DesktopNotification notification)
    {
        try
        {
            lock (_gate)
            {
                var data = CreateData(handle);
                if (!_registered)
                {
                    data.uFlags = NifIcon | NifTip;
                    _applicationIcon = _applicationIcon == 0 ? LoadApplicationIcon() : _applicationIcon;
                    data.hIcon = _applicationIcon;
                    data.szTip = "BetterMail";
                    if (!ShellNotifyIconW(NimAdd, ref data))
                    {
                        return;
                    }
                    _registered = true;
                }
                data.uFlags = NifInfo;
                data.szInfoTitle = Truncate(
                    notification.IsSharedMailbox
                        ? $"New mail — {notification.MailboxDisplayName} (shared)"
                        : $"New mail — {notification.MailboxDisplayName}",
                    63);
                data.szInfo = Truncate(
                    $"{notification.Sender}\n{notification.Subject}\n" +
                    $"{notification.AccountAddress} · {notification.MailboxAddress} · {notification.FolderName}",
                    255);
                data.hBalloonIcon = _applicationIcon;
                data.dwInfoFlags = NiifUser | NiifRespectQuietTime;
                ShellNotifyIconW(NimModify, ref data);
            }
        }
        catch
        {
            // Native notification availability varies by Windows policy and shell state.
        }
    }

    private static NotifyIconData CreateData(nint handle) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = handle,
        uID = IconId,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static nint LoadApplicationIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "BetterMail.ico");
        var icon = File.Exists(iconPath)
            ? LoadImageW(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize)
            : 0;
        return icon != 0 ? icon : LoadIconW(0, (nint)32512);
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(
        nint instance,
        string name,
        uint type,
        int width,
        int height,
        uint loadFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }
}

using System.Net;

namespace BetterMail.Core;

public static class LargeAttachmentPolicy
{
    // A conservative transport threshold, not a claim about an administrator's configured limit.
    public const long DirectAttachmentBudgetBytes = 20L * 1024 * 1024;
    public static bool UseDrive(long newSize, IEnumerable<DraftAttachment> attachments) =>
        newSize > DirectAttachmentBudgetBytes - attachments.Sum(attachment => attachment.Size);
    public static string LinkHtml(string name, DriveShareLink link) =>
        $"<p><a href=\"{WebUtility.HtmlEncode(link.Url.AbsoluteUri)}\">{WebUtility.HtmlEncode(name)}</a> (read-only link; expires {link.ExpiresAt:yyyy-MM-dd})</p>";

    public static async Task<CloudDriveItem> AttachmentsFolderAsync(IFilesProvider provider, MailAccount account, CancellationToken token = default)
    {
        var items = await provider.GetDriveItemsAsync(account, cancellationToken: token);
        return items.FirstOrDefault(item => item.IsFolder && item.Name.Equals("Attachments", StringComparison.OrdinalIgnoreCase))
            ?? await provider.CreateFolderAsync(account, null, "Attachments", token);
    }
}

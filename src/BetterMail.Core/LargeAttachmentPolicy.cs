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

    private sealed class FolderSelection
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? FolderId;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IFilesProvider,
        System.Collections.Concurrent.ConcurrentDictionary<(string Provider, string Account), FolderSelection>> FolderSelections = new();

    public static async Task<CloudDriveItem> AttachmentsFolderAsync(IFilesProvider provider, MailAccount account, CancellationToken token = default)
    {
        var selection = FolderSelections.GetOrCreateValue(provider).GetOrAdd((account.ProviderId, account.AccountId), _ => new());
        await selection.Gate.WaitAsync(token);
        try
        {
            var items = await provider.GetDriveItemsAsync(account, cancellationToken: token);
            var folder = items.FirstOrDefault(item => item.IsFolder && item.ProviderId == selection.FolderId)
                ?? items.FirstOrDefault(item => item.IsFolder && item.Name.Equals("Attachments", StringComparison.OrdinalIgnoreCase))
                // Recognize the provider's numbered conflict names after an app restart too.
                ?? items.Where(item => item.IsFolder && System.Text.RegularExpressions.Regex.IsMatch(item.Name,
                    @"^Attachments (?:[0-9]+|\([0-9]+\))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ProviderId, StringComparer.Ordinal).FirstOrDefault()
                ?? await provider.CreateFolderAsync(account, null, "Attachments", token);
            selection.FolderId = folder.ProviderId;
            return folder;
        }
        finally { selection.Gate.Release(); }
    }
}

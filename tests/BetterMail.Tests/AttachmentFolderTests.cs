using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class AttachmentFolderTests
{
    [Theory]
    [InlineData("Attachments 1")]
    [InlineData("Attachments (1)")]
    public async Task ReusesConflictFolderAcrossConcurrentUploadsAndProviderRestart(string conflictName)
    {
        var token = TestContext.Current.CancellationToken;
        var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Files);
        List<CloudDriveItem> root = [new("collision", "Attachments", 1, false, null, null, account.AccountId, account.ProviderId)];
        var provider = new FolderProvider(root, conflictName);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => LargeAttachmentPolicy.AttachmentsFolderAsync(provider, account, token)));
        Assert.Equal(1, provider.Created);
        Assert.All(results, item => Assert.Equal("created", item.ProviderId));
        var restarted = new FolderProvider(root, conflictName);
        Assert.Equal("created", (await LargeAttachmentPolicy.AttachmentsFolderAsync(restarted, account, token)).ProviderId);
        Assert.Equal(0, restarted.Created);
        // Retain the selected folder by ID if its name changes in the same session.
        root[1] = root[1] with { Name = "Mail files" };
        Assert.Equal("created", (await LargeAttachmentPolicy.AttachmentsFolderAsync(restarted, account, token)).ProviderId);
        Assert.Equal(0, restarted.Created);
    }

    private sealed class FolderProvider(List<CloudDriveItem> root, string conflictName) : IFilesProvider
    {
        public int Created;
        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(MailAccount account, string query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudFile>>([]);
        public Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(MailAccount account, CloudDriveItem? parent = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudDriveItem>>(root.ToArray());
        public async Task<CloudDriveItem> CreateFolderAsync(MailAccount account, CloudDriveItem? parent, string name, CancellationToken cancellationToken = default)
        {
            Created++;
            await Task.Yield(); // Expose duplicate creation without the per-account gate.
            var folder = new CloudDriveItem("created", conflictName, 0, true, null, null, account.AccountId, account.ProviderId);
            root.Add(folder);
            return folder;
        }
    }
}

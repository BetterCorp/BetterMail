using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class DriveWorkspaceViewModelTests
{
    [Fact]
    public void SearchFilePathIncludesTheFullNormalizedOneDrivePath()
    {
        var file = new CloudFile("file", "Budget.xlsx", 100, null, ParentPath: "/drive/root:/Finance/2026");
        var webFallback = new CloudFile(
            "fallback",
            "Budget Final.xlsx",
            100,
            new Uri("https://tenant.sharepoint.com/personal/user/Documents/Finance/Budget%20Final.xlsx"));

        Assert.Equal("/Finance/2026/Budget.xlsx", file.Path);
        Assert.Equal("/personal/user/Documents/Finance/Budget Final.xlsx", webFallback.Path);
    }

    [Fact]
    public async Task KeepsAccountRootsHierarchicalAndSearchFailuresLocal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var good = Account("good", "good@example.com");
        var bad = Account("bad", "bad@example.com");
        var provider = new FakeFilesProvider(good, bad);
        var viewModel = new DriveWorkspaceViewModel(provider, [good, bad]);

        await viewModel.InitializeAsync(cancellationToken);

        Assert.Equal(2, viewModel.Roots.Count);
        var goodRoot = viewModel.Roots[0];
        Assert.True(goodRoot.IsLoaded);
        var projects = Assert.Single(goodRoot.Children);
        Assert.Equal("Projects", projects.DisplayName);
        Assert.False(projects.IsLoaded);

        await viewModel.SelectDirectoryAsync(projects, cancellationToken);

        Assert.Equal("Projects", viewModel.SelectedDirectory?.DisplayName);
        Assert.Single(viewModel.CurrentItems);
        Assert.Equal("nested.txt", viewModel.CurrentItems[0].Item.Name);

        await viewModel.SelectDirectoryAsync(viewModel.Roots[1], cancellationToken);
        Assert.Contains(viewModel.LoadIssues, issue => issue.Contains(bad.EmailAddress));
        Assert.True(goodRoot.IsLoaded);

        viewModel.SearchQuery = "report";
        viewModel.SearchCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsSearchMode && viewModel.SearchResults.Count == 1, cancellationToken);

        var result = Assert.Single(viewModel.SearchResults);
        Assert.Contains("OneDrive", result.SourceText);
        Assert.Contains(good.EmailAddress, result.SourceText);
        Assert.Contains("Documents/report.docx", result.SourceText);
        Assert.Contains(viewModel.LoadIssues, issue => issue.Contains(bad.EmailAddress));

        DriveProviderSelection? chosen = null;
        DriveProviderSelection? linked = null;
        viewModel.ItemChosen += selection => chosen = selection;
        viewModel.LinkChosen += selection => linked = selection;
        viewModel.SelectedSearchResult = result;
        viewModel.ChooseItemCommand.Execute(null);
        viewModel.ShareLinkCommand.Execute(null);
        await WaitUntilAsync(() => chosen is not null && linked is not null, cancellationToken);
        Assert.Equal("report", chosen?.Item.ProviderId);
        Assert.Equal(good.AccountId, linked?.Account.AccountId);
    }

    [Fact]
    public async Task SupportsDriveOperationsAndProviderRelativeSelection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var account = Account("good", "good@example.com");
        var provider = new FakeFilesProvider(account);
        var viewModel = new DriveWorkspaceViewModel(provider, [account]);
        await viewModel.InitializeAsync(cancellationToken);

        viewModel.NewFolderName = "Invoices";
        viewModel.CreateFolderCommand.Execute(null);
        await WaitUntilAsync(() => provider.Created && viewModel.CurrentItems.Any(
            entry => entry.Item.Name == "Invoices"), cancellationToken);

        await using var upload = new MemoryStream("streamed upload"u8.ToArray());
        await viewModel.UploadAsync(
            new DriveUploadSource("upload.txt", upload, upload.Length, "text/plain"),
            cancellationToken);
        Assert.True(provider.Uploaded);
        var uploaded = Assert.Single(viewModel.CurrentItems, entry => entry.Item.Name == "upload.txt");

        viewModel.SelectedItem = uploaded;
        viewModel.RenameName = "renamed.txt";
        viewModel.RenameCommand.Execute(null);
        await WaitUntilAsync(() => provider.Renamed && viewModel.CurrentItems.Any(
            entry => entry.Item.Name == "renamed.txt"), cancellationToken);

        var renamed = Assert.Single(viewModel.CurrentItems, entry => entry.Item.Name == "renamed.txt");
        viewModel.SelectedItem = renamed;
        await using var destination = new MemoryStream();
        await viewModel.DownloadAsync(destination, cancellationToken);
        Assert.Equal("downloaded", System.Text.Encoding.UTF8.GetString(destination.ToArray()));

        DriveProviderSelection? chosen = null;
        viewModel.ItemChosen += selection => chosen = selection;
        viewModel.ChooseItemCommand.Execute(null);
        await WaitUntilAsync(() => chosen is not null, cancellationToken);
        Assert.Equal(account.AccountId, chosen?.Account.AccountId);
        Assert.Equal(renamed.Item.ProviderId, chosen?.Item.ProviderId);

        viewModel.RequestDeleteCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsDeletePending, cancellationToken);
        viewModel.ConfirmDeleteCommand.Execute(null);
        await WaitUntilAsync(() => provider.Deleted && viewModel.CurrentItems.All(
            entry => entry.Item.ProviderId != renamed.Item.ProviderId), cancellationToken);
    }

    [Theory]
    [InlineData(759, true)]
    [InlineData(760, false)]
    public void DriveLayoutRespondsAtCompactBreakpoint(double width, bool expected) =>
        Assert.Equal(expected, DriveWorkspaceView.IsCompactWidth(width));

    [Theory]
    [InlineData(559, true)]
    [InlineData(560, false)]
    public void DriveLayoutUsesOnePaneAtPhoneBreakpoint(double width, bool expected) =>
        Assert.Equal(expected, DriveWorkspaceView.IsPhoneWidth(width));

    private static MailAccount Account(string id, string email) => new(
        "microsoft365",
        id,
        "tenant",
        email,
        email,
        ProviderCapabilities.Files);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20, cancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class FakeFilesProvider : IFilesProvider
    {
        private readonly HashSet<string> _healthy;
        private readonly MailAccount _owner;
        private readonly Dictionary<string, List<CloudDriveItem>> _items = [];

        public bool Created { get; private set; }
        public bool Uploaded { get; private set; }
        public bool Renamed { get; private set; }
        public bool Deleted { get; private set; }

        public FakeFilesProvider(params MailAccount[] healthyAccounts)
        {
            _healthy = healthyAccounts.Take(1)
                .Select(static account => account.AccountId)
                .ToHashSet(StringComparer.Ordinal);
            _owner = healthyAccounts[0];
            var project = Item("projects", "Projects", true, null, null);
            _items["root"] =
            [
                project,
                Item("report", "report.docx", false, null, "Documents")
            ];
            _items[project.ProviderId] =
            [
                Item("nested", "nested.txt", false, project.ProviderId, "Projects")
            ];
        }

        public Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(
            MailAccount account,
            CloudDriveItem? parent = null,
            CancellationToken cancellationToken = default)
        {
            if (!_healthy.Contains(account.AccountId))
            {
                return Task.FromException<IReadOnlyList<CloudDriveItem>>(
                    new InvalidOperationException("Drive consent expired."));
            }
            var key = parent?.ProviderId ?? "root";
            IReadOnlyList<CloudDriveItem> result = _items.TryGetValue(key, out var items) ? items.ToArray() : [];
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(
            MailAccount account,
            string query,
            CancellationToken cancellationToken = default)
        {
            if (!_healthy.Contains(account.AccountId))
            {
                return Task.FromException<IReadOnlyList<CloudFile>>(
                    new InvalidOperationException("Drive search consent expired."));
            }
            IReadOnlyList<CloudFile> files =
            [
                new("report", "report.docx", 2048, new Uri("https://example.test/report"), account.AccountId, account.ProviderId, "Documents")
            ];
            return Task.FromResult(files);
        }

        public Task<CloudDriveItem> CreateFolderAsync(
            MailAccount account,
            CloudDriveItem? parent,
            string name,
            CancellationToken cancellationToken = default)
        {
            Created = true;
            var item = Item($"folder-{name}", name, true, parent?.ProviderId, parent?.Path);
            Items(parent).Add(item);
            _items[item.ProviderId] = [];
            return Task.FromResult(item);
        }

        public async Task<CloudDriveItem> UploadFileAsync(
            MailAccount account,
            CloudDriveItem? parent,
            string name,
            Stream content,
            long contentLength,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            var buffer = new byte[checked((int)contentLength)];
            await content.ReadExactlyAsync(buffer, cancellationToken);
            Uploaded = System.Text.Encoding.UTF8.GetString(buffer) == "streamed upload";
            var item = Item($"upload-{Guid.NewGuid():N}", name, false, parent?.ProviderId, parent?.Path) with
            {
                Size = contentLength,
                ContentType = contentType
            };
            Items(parent).Add(item);
            return item;
        }

        public async Task DownloadFileAsync(
            MailAccount account,
            CloudDriveItem file,
            Stream destination,
            CancellationToken cancellationToken = default) =>
            await destination.WriteAsync("downloaded"u8.ToArray(), cancellationToken);

        public Task<CloudDriveItem> RenameDriveItemAsync(
            MailAccount account,
            CloudDriveItem item,
            string name,
            CancellationToken cancellationToken = default)
        {
            Renamed = true;
            var list = ItemsFor(item);
            var index = list.FindIndex(candidate => candidate.ProviderId == item.ProviderId);
            var renamed = item with { Name = name };
            list[index] = renamed;
            return Task.FromResult(renamed);
        }

        public Task DeleteDriveItemAsync(
            MailAccount account,
            CloudDriveItem item,
            CancellationToken cancellationToken = default)
        {
            Deleted = ItemsFor(item).RemoveAll(candidate => candidate.ProviderId == item.ProviderId) > 0;
            return Task.CompletedTask;
        }

        private List<CloudDriveItem> Items(CloudDriveItem? parent)
        {
            var key = parent?.ProviderId ?? "root";
            if (!_items.TryGetValue(key, out var items))
            {
                items = [];
                _items[key] = items;
            }
            return items;
        }

        private List<CloudDriveItem> ItemsFor(CloudDriveItem item) =>
            _items.Values.Single(items => items.Any(candidate => candidate.ProviderId == item.ProviderId));

        private CloudDriveItem Item(
            string id,
            string name,
            bool folder,
            string? parentId,
            string? parentPath) =>
            new(id, name, folder ? 0 : 1024, folder, parentId, null,
                _owner.AccountId, _owner.ProviderId, folder ? null : "application/octet-stream", parentPath);
    }
}

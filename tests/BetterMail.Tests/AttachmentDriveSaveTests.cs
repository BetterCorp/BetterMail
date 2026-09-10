using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class AttachmentDriveSaveTests
{
    private static readonly MailAccount Account = new("microsoft365", "work", "tenant", "alex@example.test", "Alex", ProviderCapabilities.Files);
    private static readonly CloudDriveItem Folder = new("projects", "Projects", 0, true, null, null, Account.AccountId, Account.ProviderId);

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.pdf", "_con.pdf")]
    [InlineData("PRN.txt", "_PRN.txt")]
    [InlineData("AUX", "_AUX")]
    [InlineData("nul.tar.gz", "_nul.tar.gz")]
    [InlineData("COM0.txt", "_COM0.txt")]
    [InlineData("COM9", "_COM9")]
    [InlineData("LPT0", "_LPT0")]
    [InlineData("lpt9.txt", "_lpt9.txt")]
    [InlineData("desktop.ini", "_desktop.ini")]
    [InlineData("DESKTOP.INI", "_DESKTOP.INI")]
    [InlineData(".lock", "_.lock")]
    [InlineData("~$Budget.xlsx", "_~$Budget.xlsx")]
    [InlineData("report_VTI_final.pdf", "report_vti-final.pdf")]
    [InlineData("COM10.txt", "COM10.txt")]
    [InlineData("console.pdf", "console.pdf")]
    [InlineData("report:final.pdf", "report_final.pdf")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*.pdf", "a_b_c_d_e_f_g_h_i_.pdf")]
    [InlineData("report\nfinal.pdf", "report_final.pdf")]
    [InlineData("  résumé final.pdf  ", "résumé final.pdf")]
    [InlineData("report.pdf...", "report.pdf")]
    [InlineData("", "attachment")]
    [InlineData("   ", "attachment")]
    [InlineData("..", "attachment")]
    [InlineData(" . . ", "attachment")]
    public async Task DisplaysAndUploadsANormalizedAttachmentName(string original, string expected)
    {
        var provider = new Files();
        var model = new AttachmentDriveSaveViewModel(provider, [Account], original, "application/pdf", [1, 2]);
        Assert.Equal(expected, model.FileName);
        BetterMail.Microsoft365.Microsoft365WorkspaceProvider.ValidateDriveName(model.FileName);
        await model.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        await model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsSaved);
        Assert.Equal(expected, provider.Name);
        Assert.Equal(new byte[] { 1, 2 }, provider.Bytes);
    }

    [Fact]
    public async Task SavesExactBytesAndMetadataToChosenAccountAndFolderOnce()
    {
        var provider = new Files();
        var second = Account with { AccountId = "personal" };
        var model = new AttachmentDriveSaveViewModel(provider, [Account, second], "report.pdf", "application/pdf", [0, 1, 255]);
        await model.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        await model.Workspace.SelectDirectoryAsync(model.Workspace.Roots[1], TestContext.Current.CancellationToken);
        await model.Workspace.SelectDirectoryAsync(model.Workspace.Roots[1].Children[0], TestContext.Current.CancellationToken);
        await model.SaveAsync(TestContext.Current.CancellationToken);
        await model.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.Uploads);
        Assert.Equal(second, provider.Account);
        Assert.Equal(Folder.ProviderId, provider.Parent?.ProviderId);
        Assert.Equal("report.pdf", provider.Name);
        Assert.Equal("application/pdf", provider.ContentType);
        Assert.Equal(3, provider.Length);
        Assert.Equal(new byte[] { 0, 1, 255 }, provider.Bytes);
        Assert.True(model.IsSaved);
        Assert.False(model.CanSave);
        Assert.Contains("report (1).pdf", model.Status);
    }

    [Fact]
    public async Task KeepsPendingUploadResponsiveAndPreventsDuplicateSubmission()
    {
        var provider = new Files { Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = new AttachmentDriveSaveViewModel(provider, [Account], "report.pdf", "application/pdf", []);
        await model.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        var pending = model.SaveAsync(TestContext.Current.CancellationToken);
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsSaving);
        Assert.False(model.CanBrowse);
        Assert.False(model.CanSave);
        await model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Uploads);
        Assert.Null(provider.Parent); // Account root is a valid destination.
        provider.Wait.SetResult();
        await pending;
        Assert.False(model.IsSaving);
        Assert.True(model.IsSaved);
    }

    [Fact]
    public async Task FailedAndCancelledUploadsDoNotClaimSuccess()
    {
        var provider = new Files { Fail = true };
        var model = new AttachmentDriveSaveViewModel(provider, [Account], "report.pdf", "application/pdf", []);
        await model.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        await model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.False(model.IsSaved);
        Assert.True(model.CanSave);
        Assert.Contains("Could not confirm", model.Status);

        provider.Fail = false;
        provider.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        provider.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = model.SaveAsync(cancellation.Token);
        await provider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await pending;
        Assert.False(model.IsSaving);
        Assert.False(model.IsSaved);
        Assert.Contains("may already have arrived", model.Status);
    }

    [Fact]
    public async Task ExcludesMailOnlyAccountsAndCannotSaveBeforeChoosingLoadedDestination()
    {
        var provider = new Files();
        var model = new AttachmentDriveSaveViewModel(provider,
            [Account with { Capabilities = ProviderCapabilities.Mail }], "report.pdf", "application/pdf", []);
        Assert.False(model.CanSave);
        await model.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        await model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Empty(model.Workspace.Roots);
        Assert.Equal(0, provider.Uploads);
        Assert.Contains("Connect a OneDrive account", model.Status);
    }

    [Fact]
    public async Task CannotUploadIntoAnUnreadableDestination()
    {
        var provider = new Files { FailListing = true };
        var model = new AttachmentDriveSaveViewModel(provider, [Account], "report.pdf", "application/pdf", []);
        await model.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.False(model.CanSave);
        await model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, provider.Uploads);
        Assert.NotEmpty(model.Workspace.LoadIssues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingNavigationCannotUploadToPreviousDestination(bool changeAccount)
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new Files();
        var other = Account with { AccountId = "other" };
        var model = new AttachmentDriveSaveViewModel(provider, [Account, other], "report.pdf", "application/pdf", []);
        await model.Workspace.InitializeAsync(token);
        var destination = changeAccount ? model.Workspace.Roots[1] : model.Workspace.Roots[0].Children[0];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeListing = (_, _, cancellation) => release.Task.WaitAsync(cancellation);
        var notifications = new List<bool>();
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.CanSave)) notifications.Add(model.CanSave); };
        var navigation = model.Workspace.SelectDirectoryAsync(destination, token);
        Assert.True(model.Workspace.IsNavigating);
        Assert.False(model.CanSave);
        await model.SaveAsync(token);
        Assert.Equal(0, provider.Uploads);
        release.SetResult();
        await navigation;
        Assert.True(model.CanSave);
        Assert.Contains(false, notifications);
        Assert.True(notifications.Last());
        await model.SaveAsync(token);
        Assert.Equal(destination.Account, provider.Account);
        Assert.Equal(destination.Item, provider.Parent);
    }

    [Fact]
    public async Task OlderNavigationCannotReplaceTheLatestDestination()
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new Files();
        var model = new AttachmentDriveSaveViewModel(provider, [Account], "report.pdf", "application/pdf", []);
        await model.Workspace.InitializeAsync(token);
        var root = model.Workspace.Roots[0];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeListing = (_, _, cancellation) => release.Task.WaitAsync(cancellation);
        var oldNavigation = model.Workspace.SelectDirectoryAsync(root.Children[0], token);
        await model.Workspace.SelectDirectoryAsync(root, token);
        release.SetResult();
        await oldNavigation;
        Assert.Same(root, model.Workspace.SelectedDirectory);
        Assert.True(model.CanSave);
    }

    private sealed class Files : IFilesProvider
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Wait;
        public bool Fail;
        public bool FailListing;
        public Func<MailAccount, CloudDriveItem?, CancellationToken, Task>? BeforeListing;
        public int Uploads;
        public MailAccount? Account;
        public CloudDriveItem? Parent;
        public string? Name;
        public string? ContentType;
        public long Length;
        public byte[]? Bytes;
        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudFile>>([]);
        public async Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(MailAccount account, CloudDriveItem? parent = null, CancellationToken cancellationToken = default)
        {
            if (BeforeListing is not null) await BeforeListing(account, parent, cancellationToken);
            if (FailListing) throw new IOException("Unavailable");
            return parent is null ? [Folder with { AccountId = account.AccountId }] : [];
        }
        public async Task<CloudDriveItem> UploadFileAsync(MailAccount account, CloudDriveItem? parent, string name,
            Stream content, long contentLength, string? contentType = null, CancellationToken cancellationToken = default)
        {
            Uploads++;
            Account = account;
            Parent = parent;
            Name = name;
            ContentType = contentType;
            Length = contentLength;
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes, cancellationToken);
            Bytes = bytes.ToArray();
            Started.TrySetResult();
            if (Fail) throw new IOException("Network unavailable");
            if (Wait is not null) await Wait.Task.WaitAsync(cancellationToken);
            return Folder with { IsFolder = false, Name = "report (1).pdf" };
        }
    }
}

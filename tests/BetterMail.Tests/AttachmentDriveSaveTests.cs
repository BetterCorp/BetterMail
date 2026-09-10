using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class AttachmentDriveSaveTests
{
    private static readonly MailAccount Account = new("microsoft365", "work", "tenant", "alex@example.test", "Alex", ProviderCapabilities.Files);
    private static readonly CloudDriveItem Folder = new("projects", "Projects", 0, true, null, null, Account.AccountId, Account.ProviderId);

    [Theory]
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

    private sealed class Files : IFilesProvider
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Wait;
        public bool Fail;
        public bool FailListing;
        public int Uploads;
        public MailAccount? Account;
        public CloudDriveItem? Parent;
        public string? Name;
        public string? ContentType;
        public long Length;
        public byte[]? Bytes;
        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudFile>>([]);
        public Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(MailAccount account, CloudDriveItem? parent = null, CancellationToken cancellationToken = default) =>
            FailListing ? Task.FromException<IReadOnlyList<CloudDriveItem>>(new IOException("Unavailable")) :
            Task.FromResult<IReadOnlyList<CloudDriveItem>>(parent is null ? [Folder with { AccountId = account.AccountId }] : []);
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

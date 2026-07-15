using System.Net;
using System.Text;
using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class NotesWorkspaceViewModelTests
{
    private static readonly MailAccount Account = new(
        "microsoft365",
        "account-a",
        "tenant",
        "a@example.com",
        "Account A",
        ProviderCapabilities.Notes);

    [Fact]
    public void UsesPhoneFriendlyNotesBreakpoint()
    {
        Assert.True(NotesWorkspaceView.IsCompactWidth(759));
        Assert.False(NotesWorkspaceView.IsCompactWidth(760));
        Assert.True(NotesWorkspaceView.IsPhoneWidth(559));
        Assert.False(NotesWorkspaceView.IsPhoneWidth(560));
    }

    [Fact]
    public async Task LoadsAccountOwnedHierarchyLazily()
    {
        var second = Account with
        {
            AccountId = "account-b",
            EmailAddress = "b@example.com",
            DisplayName = "Account B"
        };
        var provider = new FakeNotesProvider();
        var viewModel = new NotesWorkspaceViewModel(provider, [Account, second]);

        var cancellationToken = TestContext.Current.CancellationToken;
        await viewModel.InitializeAsync(cancellationToken);

        Assert.Equal(2, viewModel.AccountRoots.Count);
        Assert.Equal(0, provider.NotebookCalls);

        var root = viewModel.AccountRoots[0];
        await viewModel.LoadChildrenAsync(root, cancellationToken: cancellationToken);
        var notebook = Assert.Single(root.AllChildren);
        Assert.Equal(1, provider.NotebookCalls);
        Assert.Equal(NoteNodeKind.Notebook, notebook.Kind);
        Assert.Equal(Account.AccountId, notebook.Notebook!.AccountId);

        await viewModel.LoadChildrenAsync(notebook, cancellationToken: cancellationToken);
        var section = Assert.Single(notebook.AllChildren);
        await viewModel.LoadChildrenAsync(section, cancellationToken: cancellationToken);
        var page = Assert.Single(section.AllChildren);

        Assert.Equal(NoteNodeKind.Section, section.Kind);
        Assert.Equal(notebook.Notebook.ProviderId, section.Section!.NotebookProviderId);
        Assert.Equal(NoteNodeKind.Page, page.Kind);
        Assert.Equal(section.Section.ProviderId, page.Page!.SectionProviderId);
        Assert.Equal(Account.AccountId, page.Page.AccountId);
    }

    [Fact]
    public async Task SearchesAcrossAccountsWithoutFlatteningAndKeepsPartialErrors()
    {
        var badAccount = Account with
        {
            AccountId = "bad",
            EmailAddress = "bad@example.com",
            DisplayName = "Broken account"
        };
        var provider = new FakeNotesProvider("bad");
        var viewModel = new NotesWorkspaceViewModel(provider, [Account, badAccount])
        {
            SearchText = "Roadmap"
        };
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.SearchAsync();

        var root = Assert.Single(viewModel.VisibleRoots);
        var notebook = Assert.Single(root.Children);
        var section = Assert.Single(notebook.Children);
        var page = Assert.Single(section.Children);
        Assert.Equal("Roadmap", page.DisplayName);
        Assert.True(viewModel.HasPartialErrors);
        Assert.Contains("bad@example.com", viewModel.PartialErrorText);
        Assert.Equal(2, viewModel.AccountRoots.Count);
    }

    [Fact]
    public async Task SanitizesPageContentAndRequiresRemoteContentOptIn()
    {
        var provider = new FakeNotesProvider
        {
            PageHtml = """
                <html><body>
                <script>alert('unsafe')</script>
                <img src="https://images.example/chart.png" alt="Chart">
                <a href="javascript:alert(1)">Bad link</a>
                </body></html>
                """
        };
        var viewModel = new NotesWorkspaceViewModel(provider, [Account]);
        var cancellationToken = TestContext.Current.CancellationToken;
        await viewModel.InitializeAsync(cancellationToken);
        var page = await LoadPageNodeAsync(viewModel, cancellationToken);

        viewModel.SelectedNode = page;
        await WaitForAsync(() => viewModel.PageBodyUri is not null, cancellationToken);
        var blocked = DecodeDataUri(viewModel.PageBodyUri!);

        Assert.True(viewModel.HasBlockedRemoteContent);
        Assert.DoesNotContain("<script", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Blocked picture: Chart", blocked);
        Assert.Contains("img-src data:", blocked);

        await viewModel.AllowRemoteContentAsync();
        var allowed = DecodeDataUri(viewModel.PageBodyUri!);
        Assert.False(viewModel.HasBlockedRemoteContent);
        Assert.Contains("https://images.example/chart.png", allowed);
        Assert.Contains("img-src data: http: https:", allowed);
    }

    [Fact]
    public async Task CreatesUpdatesAndDeletesPageWithSafeText()
    {
        var provider = new FakeNotesProvider();
        var viewModel = new NotesWorkspaceViewModel(provider, [Account]);
        var cancellationToken = TestContext.Current.CancellationToken;
        await viewModel.InitializeAsync(cancellationToken);
        var section = await LoadSectionNodeAsync(viewModel, cancellationToken);
        viewModel.SelectedNode = section;

        await viewModel.OpenNewPageAsync();
        viewModel.EditorTitle = "Plan <Q3>";
        viewModel.EditorBody = "Revenue < cost\nNext";
        await viewModel.SaveEditorAsync();

        Assert.Equal("Plan <Q3>", provider.CreatedDraft!.Title);
        Assert.Equal("<p>Revenue &lt; cost</p><p>Next</p>", provider.CreatedDraft.HtmlBody);
        Assert.Equal("created-page", viewModel.SelectedPage!.ProviderId);

        await viewModel.OpenEditPageAsync();
        viewModel.EditorTitle = "Updated <Q3>";
        viewModel.EditorBody = "Append & check";
        await viewModel.SaveEditorAsync();

        Assert.Equal("Updated &lt;Q3&gt;", provider.UpdatedChanges![0].HtmlContent);
        Assert.Equal("<p>Append &amp; check</p>", provider.UpdatedChanges[1].HtmlContent);

        await viewModel.RequestDeleteAsync();
        Assert.True(viewModel.IsDeleteConfirmationOpen);
        await viewModel.ConfirmDeleteAsync();

        Assert.Equal("created-page", provider.DeletedPageId);
        Assert.False(viewModel.IsDeleteConfirmationOpen);
        Assert.DoesNotContain(section.AllChildren, static node => node.Page?.ProviderId == "created-page");
    }

    private static async Task<NoteTreeNode> LoadSectionNodeAsync(
        NotesWorkspaceViewModel viewModel,
        CancellationToken cancellationToken)
    {
        var root = Assert.Single(viewModel.AccountRoots);
        await viewModel.LoadChildrenAsync(root, cancellationToken: cancellationToken);
        var notebook = Assert.Single(root.AllChildren);
        await viewModel.LoadChildrenAsync(notebook, cancellationToken: cancellationToken);
        return Assert.Single(notebook.AllChildren);
    }

    private static async Task<NoteTreeNode> LoadPageNodeAsync(
        NotesWorkspaceViewModel viewModel,
        CancellationToken cancellationToken)
    {
        var section = await LoadSectionNodeAsync(viewModel, cancellationToken);
        await viewModel.LoadChildrenAsync(section, cancellationToken: cancellationToken);
        return Assert.Single(section.AllChildren);
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, cancellationToken);
        }
        Assert.True(condition());
    }

    private static string DecodeDataUri(Uri uri)
    {
        var encoded = uri.OriginalString[(uri.OriginalString.IndexOf(',') + 1)..];
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private sealed class FakeNotesProvider(string? failingAccountId = null) : INotesProvider
    {
        private readonly List<NotePage> _pages =
        [
            new(
                "roadmap-page",
                "section-account-a",
                "Roadmap",
                new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero),
                1,
                0,
                Account.AccountId,
                Account.ProviderId)
        ];

        public int NotebookCalls { get; private set; }
        public string PageHtml { get; init; } = "<html><body><p>Roadmap</p></body></html>";
        public NotePageDraft? CreatedDraft { get; private set; }
        public IReadOnlyList<NotePagePatch>? UpdatedChanges { get; private set; }
        public string? DeletedPageId { get; private set; }

        public Task<IReadOnlyList<NoteInfo>> GetNotesAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteInfo>>([]);

        public Task<IReadOnlyList<NoteNotebook>> GetNotebooksAsync(
            MailAccount account, CancellationToken cancellationToken = default)
        {
            NotebookCalls++;
            if (account.AccountId == failingAccountId)
            {
                throw new HttpRequestException("Notes permission denied.");
            }
            return Task.FromResult<IReadOnlyList<NoteNotebook>>(
                [new($"notebook-{account.AccountId}", "Work", account.AccountId, account.ProviderId)]);
        }

        public Task<IReadOnlyList<NoteSection>> GetSectionsAsync(
            MailAccount account,
            NoteNotebook notebook,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteSection>>(
                [new($"section-{account.AccountId}", notebook.ProviderId, "Projects", account.AccountId, account.ProviderId)]);

        public Task<IReadOnlyList<NotePage>> GetPagesAsync(
            MailAccount account,
            NoteSection section,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NotePage>>(
                _pages.Where(page => page.AccountId == account.AccountId).ToArray());

        public Task<NotePageContent> GetPageContentAsync(
            MailAccount account,
            NotePage page,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotePageContent(
                page.ProviderId,
                page.SectionProviderId,
                page.AccountId,
                page.AccountProviderId,
                PageHtml));

        public Task<NotePage> CreatePageAsync(
            MailAccount account,
            NotePageDraft draft,
            CancellationToken cancellationToken = default)
        {
            CreatedDraft = draft;
            var page = new NotePage(
                "created-page",
                draft.SectionProviderId,
                draft.Title,
                DateTimeOffset.UtcNow,
                2,
                0,
                draft.AccountId,
                draft.AccountProviderId);
            _pages.Add(page);
            return Task.FromResult(page);
        }

        public Task UpdatePageAsync(
            MailAccount account,
            NotePage page,
            IReadOnlyList<NotePagePatch> changes,
            CancellationToken cancellationToken = default)
        {
            UpdatedChanges = changes;
            var index = _pages.FindIndex(item => item.ProviderId == page.ProviderId);
            var title = changes.FirstOrDefault(change => change.Target == "title")?.HtmlContent;
            if (index >= 0 && title is not null)
            {
                _pages[index] = _pages[index] with { Title = WebUtility.HtmlDecode(title) };
            }
            return Task.CompletedTask;
        }

        public Task DeletePageAsync(
            MailAccount account,
            NotePage page,
            CancellationToken cancellationToken = default)
        {
            DeletedPageId = page.ProviderId;
            _pages.RemoveAll(item => item.ProviderId == page.ProviderId);
            return Task.CompletedTask;
        }
    }
}

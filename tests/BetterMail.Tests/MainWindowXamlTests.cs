using System.Text.RegularExpressions;
using BetterMail.App;

namespace BetterMail.Tests;

public sealed class MainWindowXamlTests
{
    [Fact]
    public void SettingsIncludesDiscoverableBrandedAboutInformation()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "MainWindow.axaml"));
        var settings = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "SettingsView.axaml"));
        var appInfoSource = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "AppInfo.cs"));

        Assert.Contains("<app:SettingsView", xaml);
        Assert.Contains("/Assets/BetterMail.png", settings);
        Assert.Contains("/Assets/BetterMailSettingsBanner.png", settings);
        Assert.Contains("Background=" + (char)34 + "#040F2F", settings);
        Assert.Contains("Stretch=" + (char)34 + "Uniform", settings);
        Assert.DoesNotContain("UniformToFill", settings);
        Assert.Contains("Text=" + (char)34 + "Notifications" + (char)34, settings);
        Assert.Equal(1, Count(settings, "Content=" + (char)34 + "Desktop notifications for new Inbox mail" + (char)34));
        Assert.Contains("CheckForUpdatesClicked", settings);
        Assert.Contains("{Binding Version", settings);
        Assert.Contains("AGPL-3.0-only", settings);
        Assert.Contains("BetterCorp", settings);
        Assert.Contains("RepositoryUri", settings);
        Assert.Contains("ReleasesUri", settings);
        Assert.Contains("LicenseUri", settings);
        Assert.Equal(3, Count(settings, "Click=" + (char)34 + "OpenWorkspaceLinkClicked" + (char)34));
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding SettingsTabs}" + (char)34, settings);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding SignatureTemplates}" + (char)34, settings);
        Assert.Contains("SelectedItem=" + (char)34 + "{Binding SelectedSignatureTemplate}" + (char)34, settings);
        Assert.Contains("Source=" + (char)34 + "{Binding SelectedSignatureTemplatePreviewUri}" + (char)34, settings);
        Assert.Contains("Selected signature template preview", settings);
        Assert.Contains("<app:RichHtmlEditor", settings);
        Assert.Contains("typeof(AppInfo).Assembly.GetName().Version", appInfoSource);
        Assert.Equal(
            typeof(BetterMail.App.AppInfo).Assembly.GetName().Version?.ToString(3) ?? "Unknown",
            new BetterMail.App.AppInfo().Version);
    }

    [Fact]
    public void ResponsiveViewsRebuildOnlyWhenTheirBreakpointChanges()
    {
        var root = FindRepositoryRoot();
        foreach (var file in new[]
                 {
                     "MainWindow.axaml.cs",
                     "SettingsView.axaml.cs",
                     "ConversationThreadView.axaml.cs",
                     "CalendarWorkspaceView.axaml.cs",
                     "DriveWorkspaceView.axaml.cs",
                     "NotesWorkspaceView.axaml.cs",
                     "TasksWorkspaceView.axaml.cs"
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", file));
            var guard = source.IndexOf("_layoutInitialized &&", StringComparison.Ordinal);
            var rebuild = source.IndexOf(".ColumnDefinitions.Clear()", StringComparison.Ordinal);

            Assert.True(guard >= 0, $"{file} is missing its responsive layout guard.");
            Assert.True(rebuild < 0 || guard < rebuild, $"{file} rebuilds layout before checking its breakpoint.");
        }
    }

    [Theory]
    [InlineData("invoice.pdf", "application/octet-stream", "Pdf")]
    [InlineData("photo.bin", "image/png; name=photo.png", "Image")]
    [InlineData("notes.txt", "application/octet-stream", "Text")]
    [InlineData("report.docx", "application/octet-stream", "Unsupported")]
    public void AttachmentPreviewUsesSafeBuiltInRenderers(
        string name,
        string contentType,
        string expected) =>
        Assert.Equal(expected, FilePreviewWindow.PreviewKindFor(name, contentType).ToString());

    [Fact]
    public void PreviewWindowsRestoreFromLocalCacheAndSyncRestoresScrollAfterLayout()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "App.axaml.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "MainWindow.axaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "MainWindowViewModel.cs"));

        Assert.Contains("await mainWindow.RestorePreviewWindowsAsync()", app);
        Assert.Contains("GetCachedPreviewAsync", viewModel);
        Assert.Contains("new WindowSessionStore(_viewModel.DataDirectory)", window);
        Assert.Contains("MessageList.LayoutUpdated +=", window);
        Assert.DoesNotContain("DispatcherPriority.Loaded", window);
    }

    [Fact]
    public void ShellKeepsCanonicalActionsContextualAccountsAndAccessibleNavigation()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "MainWindow.axaml"));
        var conversationXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BetterMail.App", "ConversationThreadView.axaml"));
        var threadHeaders = Between(conversationXaml, "<ItemsControl x:Name=" + (char)34 + "ThreadMessages", "</ItemsControl>");
        var folderPane = Between(xaml, "<!-- Structured folder pane -->", "<!-- Compact message list -->");
        var commandBar = Between(xaml, "<!-- Mail command bar", "<!-- Structured folder pane -->");
        var settingsXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "SettingsView.axaml"));
        var accounts = Between(settingsXaml, "SettingsAccounts", "IsConfirmingAccountRemoval");
        var composeXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "ComposeWindow.axaml"));
        var calendarXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "CalendarWorkspaceView.axaml"));
        var appSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "App.axaml.cs"));
        var startupSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "StartupWindow.cs"));

        Assert.DoesNotContain("ConnectCommand", folderPane);
        Assert.DoesNotContain("Add shared mailbox", folderPane);
        Assert.Equal(1, ButtonCommandCount(xaml, "SyncCommand"));
        Assert.Equal(1, ButtonCommandCount(xaml, "OpenSettingsCommand"));
        foreach (var gesture in new[]
                 {
                     "Ctrl+N", "Ctrl+R", "Ctrl+Shift+R", "Ctrl+F", "Ctrl+E", "F9"
                 })
        {
            Assert.Contains("Gesture=" + (char)34 + gesture + (char)34, xaml);
        }
        Assert.Equal(1, ButtonCommandCount(xaml, "ReplyAllCommand"));
        Assert.DoesNotContain("HorizontalScrollBarVisibility", commandBar);
        Assert.Contains("x:Name=" + (char)34 + "InlineMailActions" + (char)34, commandBar);
        Assert.Contains("AutomationProperties.Name=" + (char)34 + "More mail actions" + (char)34, commandBar);
        Assert.Contains("Click=" + (char)34 + "MessageArchiveClicked" + (char)34, commandBar);
        Assert.DoesNotContain("TabIndex=" + (char)34 + "71" + (char)34, accounts);
        Assert.DoesNotContain("TabIndex=" + (char)34 + "72" + (char)34, accounts);
        Assert.DoesNotContain("TabIndex=" + (char)34 + "73" + (char)34, accounts);
        Assert.Contains("KeyDown=" + (char)34 + "ComposeWindowKeyDown" + (char)34, composeXaml);
        Assert.Contains("MessageReplyAllClicked", xaml);
        Assert.DoesNotContain("MessageRowPointerPressed", xaml);
        Assert.Contains("x:Name=" + (char)34 + "MailSearch" + (char)34, xaml);
        Assert.Contains("PlaceholderText=" + (char)34 + "Search everything" + (char)34, xaml);
        Assert.Contains("Command=" + (char)34 + "{Binding ClearGlobalSearchCommand}" + (char)34, xaml);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding GlobalSearchResults}" + (char)34, xaml);
        Assert.Contains("KeyDown=" + (char)34 + "GlobalSearchKeyDown" + (char)34, xaml);
        Assert.Contains("IsVisible=" + (char)34 + "{Binding StartsCategory}" + (char)34, xaml);
        Assert.Contains("SelectedItem=" + (char)34 + "{Binding SelectedMessage, Mode=TwoWay}" + (char)34, xaml);
        Assert.Contains("ShowDraftsCommand", folderPane);
        Assert.DoesNotContain("<Expander Header=" + (char)34 + "{Binding DraftCountText}", folderPane);
        Assert.Contains("QuickArchiveClicked", xaml);
        Assert.Contains("DoubleTapped=" + (char)34 + "MessageRowDoubleTapped" + (char)34, xaml);
        Assert.Contains("DoubleTapped=" + (char)34 + "CalendarEventDoubleTapped" + (char)34, calendarXaml);
        Assert.Contains(BindingAttribute("IsVisible", "ShowWorkspaceSurface"), xaml);
        Assert.Contains(BindingAttribute("IsVisible", "ShowMailSurface"), xaml);
        Assert.Contains("ToggleMessageCommand", conversationXaml);
        Assert.DoesNotContain("<SelectableTextBlock", threadHeaders);
        Assert.Contains("<SelectableTextBlock Text=" + (char)34 + "{Binding SelectedThread.Subject", conversationXaml);
        Assert.Contains("<TextBlock Text=" + (char)34 + "{Binding SenderAddress}", threadHeaders);
        Assert.Contains("TreeViewItem:pointerover /template/ ContentPresenter", folderPane);
        Assert.Contains("<Setter Property=" + (char)34 + "BorderThickness" + (char)34 + " Value=" + (char)34 + "0" + (char)34 + " />", folderPane);
        Assert.Contains("<TreeView", folderPane);
        Assert.Equal(1, Count(xaml, "<app:CalendarWorkspaceView"));
        Assert.DoesNotContain(BindingAttribute("ItemsSource", "CalendarEvents"), xaml);
        Assert.Equal(1, Count(xaml, "<app:NotesWorkspaceView"));
        Assert.DoesNotContain(BindingAttribute("ItemsSource", "Notes"), xaml);
        Assert.Equal(1, Count(xaml, "<app:DriveWorkspaceView"));
        Assert.DoesNotContain(BindingAttribute("ItemsSource", "Files"), xaml);
        Assert.Equal(1, Count(xaml, "<app:TasksWorkspaceView"));
        Assert.DoesNotContain(BindingAttribute("ItemsSource", "Tasks"), xaml);
        Assert.Equal(1, Count(xaml, "<app:ConversationThreadView"));
        Assert.Contains("Content=" + (char)34 + "{Binding ActiveWorkspace}" + (char)34, xaml);
        Assert.DoesNotContain("DataContext=" + (char)34 + "{Binding CalendarWorkspace}" + (char)34, xaml);
        Assert.DoesNotContain("<NativeWebView", xaml);
        Assert.Equal(1, Count(conversationXaml, "<NativeWebView"));
        Assert.Contains("Classes.selected=" + (char)34 + "{Binding IsSelected}" + (char)34, conversationXaml);
        Assert.Contains("x:Name=" + (char)34 + "CompactActions" + (char)34, conversationXaml);
        Assert.Contains(BindingAttribute("IsVisible", "IsGenericWorkspaceModule"), xaml);
        Assert.Contains("x:Name=" + (char)34 + "SettingsSurface" + (char)34 + " Grid.Row=" + (char)34 + "0" + (char)34 + " Grid.RowSpan=" + (char)34 + "3" + (char)34, xaml);
        Assert.Contains("x:Name=" + (char)34 + "FullAppLoader" + (char)34, xaml);
        Assert.Contains(BindingAttribute("IsVisible", "ShowFullScreenLoader"), xaml);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding Drafts}" + (char)34, xaml);
        Assert.Contains("Height=" + (char)34 + "{Binding $parent[Window].DataContext.MessageRowHeight}" + (char)34, xaml);
        Assert.Contains("x:Name=" + (char)34 + "PeopleCards" + (char)34, xaml);
        Assert.Contains("AutomationProperties.Name=" + (char)34 + "Copy error" + (char)34, xaml);
        Assert.Contains("AutomationProperties.Name=" + (char)34 + "Close error" + (char)34, xaml);
        Assert.Contains("desktop.MainWindow = startupWindow", appSource);
        Assert.Contains("startupWindow.Opened +=", appSource);
        Assert.True(appSource.IndexOf("mainWindow.Show()", StringComparison.Ordinal) <
            appSource.IndexOf("viewModel.InitializeAsync()", StringComparison.Ordinal));
        Assert.Contains("IsIndeterminate = true", startupSource);
        Assert.Contains("Loading BetterMail", startupSource);
        Assert.Contains("avares://BetterMail/Assets/BetterMail.png", startupSource);
        Assert.Contains("Background = background", startupSource);
        Assert.Contains("SelectionMode=" + (char)34 + "Multiple" + (char)34, xaml);
        Assert.Contains("SelectionChanged=" + (char)34 + "MessageSelectionChanged" + (char)34, xaml);
        Assert.Contains("DragDrop.AllowDrop=" + (char)34 + "True" + (char)34, folderPane);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding Categories}" + (char)34, xaml);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding SearchAccountFilters}" + (char)34, xaml);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding SearchFolderFilters}" + (char)34, xaml);
        Assert.Contains("SelectedItem=" + (char)34 + "{Binding MailSyncRange}" + (char)34, settingsXaml);
        Assert.Contains("x:Name=" + (char)34 + "MailStatisticsSection" + (char)34, settingsXaml);
        Assert.Contains("ItemsSource=" + (char)34 + "{Binding MailStatistics}" + (char)34, settingsXaml);
        Assert.Contains("OpenGlobalSearchGroupCommand", xaml);
        Assert.Contains("StringFormat='View {0}'", xaml);
        Assert.Contains("Text=" + (char)34 + "No mail here" + (char)34, xaml);
        Assert.DoesNotContain("Connect an account or sync this folder", xaml);
        Assert.Contains("Command=" + (char)34 + "{Binding ClearSearchResultsCommand}" + (char)34, xaml);

        var project = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BetterMail.App", "BetterMail.App.csproj"));
        Assert.Contains("CopyToOutputDirectory=" + (char)34 + "PreserveNewest" + (char)34, project);
        var packaging = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "package-release.ps1"));
        Assert.Contains("Join-Path $publishDirectory " + (char)34 + "BetterMail.ico" + (char)34, packaging);

        Assert.Contains("ReauthenticateAccountCommand", accounts);
        Assert.Contains("AddSharedMailboxForAccountCommand", accounts);
        Assert.Contains("RequestRemoveAccountCommand", accounts);
        Assert.Equal(3, Count(accounts, BindingAttribute("CommandParameter", "Account")));

        foreach (var command in new[]
                 {
                     "ShowCalendarCommand", "ShowContactsCommand", "ShowTasksCommand",
                     "ShowFilesCommand", "ShowNotesCommand"
                 })
        {
            Assert.Equal(1, ButtonCommandCount(xaml, command));
        }

        foreach (var accessibleName in new[]
                 {
                     "Sync mail", "Settings", "Folders for {0}", "Message list",
                    "Reply to selected message", "Reply all to selected message",
                    "Re-authenticate {0}",
                     "Add shared mailbox for {0}", "Remove {0}"
                 })
        {
            Assert.Contains(accessibleName, xaml + settingsXaml);
        }
        Assert.Contains("Load blocked pictures for the selected message", conversationXaml);
        Assert.Contains("PreviewAttachmentClicked", xaml);
        Assert.Contains("&#x1F4CE;", xaml);
        Assert.Contains("IsVisible=" + (char)34 + "{Binding IsMailActionRunning}" + (char)34, xaml);
        Assert.Contains("Text=" + (char)34 + "{Binding MailActionStatus}" + (char)34, xaml);
        Assert.DoesNotContain("Text=" + (char)34 + "⌕" + (char)34, xaml);
    }

    private static int ButtonCommandCount(string xaml, string command) =>
        Regex.Matches(
            xaml,
            "<Button[^>]*" + Regex.Escape(BindingAttribute("Command", command)),
            RegexOptions.Singleline).Count;

    private static string BindingAttribute(string attribute, string value) =>
        attribute + "=" + '"' + "{Binding " + value + "}" + '"';

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string Between(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterMail.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("BetterMail repository root was not found.");
    }
}

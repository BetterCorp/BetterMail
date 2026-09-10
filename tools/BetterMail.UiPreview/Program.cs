using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using BetterMail.App;
using BetterMail.Core;
using System.Reflection;
using System.Diagnostics;

// A standalone Linux visual-review host. Uses the real views and fictional, offline data.
// Never starts the production app lifetime, account authentication, or synchronization.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "docs/ui/screenshots");
        Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        AvaloniaSynchronizationContext.InstallIfNeeded();
        using var stop = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await CaptureAsync(output); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    private static async Task CaptureAsync(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var provider = DispatchProxy.Create<IWorkspaceProvider, PreviewProvider>();
        var vm = new MainWindowViewModel(null, directory, _ => { }, _ => { }, null, workspaceProvider: provider);
        var account = PreviewProvider.Account;
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Alex Morgan");
        vm.Accounts.Add(account);
        vm.Mailboxes.Add(mailbox);
        vm.ContactOwners.Add(new(account, mailbox));
        vm.Accounts.Add(account with { ProviderId = "google-workspace", AccountId = "studio", EmailAddress = "alex@studio.example", DisplayName = "Studio", Capabilities = ProviderCapabilities.Mail | ProviderCapabilities.Files });
        await vm.InitializeAsync();
        var folders = new[] { "Inbox", "Sent", "Drafts", "Archive", "Trash" }.Select(name => new MailFolderItem(new(mailbox.Id, name.ToLowerInvariant(), name, name == "Inbox" ? 3 : 0, 8), mailbox.DisplayName)).ToArray();
        foreach (var folder in folders) vm.Folders.Add(folder);
        vm.FolderGroups.Add(new(mailbox, folders.Select(f => new MailFolderNode(f, [])).ToArray()));
        var subjects = new[] { "A little space for our next big idea", "Design review · Thursday", "Your weekly project roundup", "Autumn launch — next steps", "Coffee next week?", "Updated research notes", "September planning", "Welcome to the workspace" };
        var senders = new[] { "Jamie Chen", "Priya Patel", "Studio team", "Sam Rivera", "Taylor Brooks", "Jordan Lee", "Alex Morgan", "Studio team" };
        for (var i = 0; i < subjects.Length; i++)
            vm.Messages.Add(new(mailbox.Id, "message-" + i, "thread-" + i, null, "inbox", subjects[i], new(senders[i], "hello@studio.example"), [new("Alex Morgan", account.EmailAddress)], PreviewProvider.Today.AddHours(10).AddMinutes(-i * 35), "A few thoughts before we get together. Looking forward to hearing what you think.", "Hi Alex,\n\nI've pulled together the ideas from our last conversation. The direction is simple: make everyday work feel a little more thoughtful.\n\nFor Thursday's review, let's focus on three things:\n\n• A clear place to start\n• Less noise, more room to think\n• Small details that make a difference\n\nThe updated notes are ready in our shared folder. Take a look when you have a moment, and bring any questions to the review.\n\nThanks,\nJamie", false, i > 2, false, MailImportance.Normal, [], null, IsFlagged: i == 3));
        var previewMessages = vm.Messages.ToArray();
        // Inbox overview: no native message web view is opened in this Linux capture.
        vm.SelectedMessage = null;
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 960, WindowDecorations = WindowDecorations.None, Position = new PixelPoint(0, 0) };
        window.Show();
        await Shot("mail-light");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        vm.ConversationThread.RefreshTheme();
        await Shot("mail-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        foreach (var (name, command) in new[] { ("calendar", vm.ShowCalendarCommand), ("files", vm.ShowFilesCommand), ("notes", vm.ShowNotesCommand), ("people", vm.ShowContactsCommand), ("todos", vm.ShowTasksCommand) })
        {
            await ((AsyncCommand)command).ExecuteAsync();
            if (vm.ActiveWorkspace is NotesWorkspaceViewModel notes)
            {
                foreach (var root in notes.AccountRoots.ToArray())
                {
                    await notes.LoadChildrenAsync(root); root.IsExpanded = true;
                    foreach (var notebook in root.Children.ToArray())
                    {
                        await notes.LoadChildrenAsync(notebook); notebook.IsExpanded = true;
                        foreach (var section in notebook.Children.ToArray())
                        {
                            await notes.LoadChildrenAsync(section); section.IsExpanded = true;
                        }
                    }
                }
                var page = Descendants(notes.AccountRoots).FirstOrDefault(n => n.Page is not null);
                // Keep the navigation overview visible without requiring an embedded Linux web engine.
                _ = page;
            }
            await Shot(name + "-light");
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await Shot(name + "-dark");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        }
        window.Width = 390;
        window.Height = 844;
        await ((AsyncCommand)vm.ShowUnifiedInboxCommand).ExecuteAsync();
        foreach (var message in previewMessages) vm.Messages.Add(message);
        await Shot("mail-phone");
        window.Close();
        Directory.Delete(directory, true);

        async Task Shot(string name)
        {
            await Task.Delay(1800);
            var process = Process.Start(new ProcessStartInfo("python3") { ArgumentList = { "tools/BetterMail.UiPreview/capture.py", Path.Combine(output, name + ".png"), ((int)window.Width).ToString(), ((int)window.Height).ToString() } })!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Screenshot capture failed.");
            Console.WriteLine(name);
        }
    }
    private static IEnumerable<NoteTreeNode> Descendants(IEnumerable<NoteTreeNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Descendants(n.Children)));
}

public class PreviewProvider : DispatchProxy
{
    public static readonly DateTimeOffset Today = new(DateTime.Today, TimeSpan.Zero);
    public static readonly MailAccount Account = new("microsoft365", "work", "sample", "alex@work.example", "Alex Morgan", ProviderCapabilities.Mail | ProviderCapabilities.Calendar | ProviderCapabilities.Contacts | ProviderCapabilities.Tasks | ProviderCapabilities.Files | ProviderCapabilities.Notes);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var m = method!;
        if (!m.Name.StartsWith("Get", StringComparison.Ordinal) && !m.Name.StartsWith("Search", StringComparison.Ordinal)) throw new NotSupportedException("The UI preview is read-only.");
        var account = args!.OfType<MailAccount>().FirstOrDefault() ?? Account;
        object value = m.Name switch
        {
            "GetCalendarsAsync" => new CalendarInfo[] { new("team", "Team calendar", "#5576CF", true, account.AccountId) },
            "GetEventsAsync" => new CalendarEvent[] { new("review", "team", "Design review", Today.AddHours(10), Today.AddHours(11), "Studio room", AccountId: account.AccountId), new("planning", "team", "Autumn planning", Today.AddDays(1).AddHours(13), Today.AddDays(1).AddHours(14), "Online", AccountId: account.AccountId) },
            "GetContactsAsync" or "SearchContactsAsync" => new ContactInfo[] { new("jamie", "Jamie Chen", ["jamie@studio.example"], account.AccountId), new("priya", "Priya Patel", ["priya@studio.example"], account.AccountId), new("sam", "Sam Rivera", ["sam@studio.example"], account.AccountId) },
            "GetTaskListsAsync" => new TaskListInfo[] { new("launch", "Autumn launch", account.AccountId), new("personal", "My day", account.AccountId) },
            "GetTasksAsync" when args!.OfType<TaskListInfo>().FirstOrDefault()?.ProviderId == "personal" => Array.Empty<TaskInfo>(),
            "GetTasksAsync" => new TaskInfo[] { new("brief", "launch", "Review the creative brief", Today, false, account.AccountId), new("research", "launch", "Share customer research", Today.AddDays(1), false, account.AccountId), new("plan", "launch", "Finalize the launch checklist", Today.AddDays(2), false, account.AccountId), new("done", "launch", "Set up the project workspace", Today, true, account.AccountId) },
            "GetDriveItemsAsync" => new CloudDriveItem[] { new("folder", "Autumn launch", 0, true, null, null, account.AccountId, account.ProviderId), new("brief", "Creative brief.pdf", 284000, false, null, null, account.AccountId, account.ProviderId), new("research", "Customer research.docx", 128000, false, null, null, account.AccountId, account.ProviderId), new("budget", "Launch budget.xlsx", 64000, false, null, null, account.AccountId, account.ProviderId) },
            "GetNotebooksAsync" => new NoteNotebook[] { new("notebook", "Studio notebook", account.AccountId, account.ProviderId) },
            "GetSectionsAsync" => new NoteSection[] { new("ideas", "notebook", "Ideas & planning", account.AccountId, account.ProviderId) },
            "GetPagesAsync" => new NotePage[] { new("page", "ideas", "A thoughtful workspace", Today.AddHours(9), 0, 0, account.AccountId, account.ProviderId), new("meeting", "ideas", "Design review notes", Today, 1, 0, account.AccountId, account.ProviderId) },
            "GetPageContentAsync" => new NotePageContent("page", "ideas", account.AccountId, account.ProviderId, "<html><head><title>A thoughtful workspace</title></head><body><h1>A thoughtful workspace</h1><p>A place for the work that matters.</p><h2>What we're exploring</h2><ul><li>Clear navigation across our tools</li><li>Fewer distractions and a little more breathing room</li><li>Thoughtful details that work together</li></ul><h2>Next steps</h2><p>Bring the first ideas to Thursday's design review.</p></body></html>"),
            _ => Empty(m.ReturnType.GenericTypeArguments[0])
        };
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(m.ReturnType.GenericTypeArguments[0]).Invoke(null, [value]);
    }
    private static object Empty(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? Array.CreateInstance(type.GenericTypeArguments[0], 0) : throw new NotSupportedException(type.Name);
}

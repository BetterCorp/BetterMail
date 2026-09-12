using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        vm.Accounts.Add(account with { ProviderId = "microsoft365", AccountId = "studio", EmailAddress = "alex@studio.example", DisplayName = "Studio", Capabilities = ProviderCapabilities.Mail | ProviderCapabilities.Files });
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
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 960, WindowDecorations = WindowDecorations.None, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(0, 0) };
        window.Show();
        await CheckImageConsentAsync();
        window.Activate();
        await Shot("mail-light");
        var senderImages = window.FindControl<ListBox>("MessageList")!.GetVisualDescendants().OfType<AsyncImage>().ToArray();
        if (senderImages.Length == 0 || senderImages.Any(i => i.AllowLoading || i.IsVisible))
            throw new InvalidOperationException("Mail images must start hidden with loading disabled.");
        vm.MailSenderImagesEnabled = true;
        await Task.Delay(200);
        if (senderImages.Any(i => !i.AllowLoading || !i.IsVisible || i.Request?.Key != "contact:hello@studio.example"))
            throw new InvalidOperationException("Mail sender binding did not enable the expected image request.");
        foreach (var senderImage in senderImages)
            senderImage.Request = new("mail-preview-photo", _ => Task.FromResult<byte[]?>(PreviewProvider.SampleThumbnail()));
        await Shot("mail-sender-images-light");
        vm.MailSenderImagesEnabled = false;
        if (senderImages.Any(i => i.AllowLoading || i.IsVisible || i.GetVisualDescendants().OfType<Image>().Single().Source is not null))
            throw new InvalidOperationException("Mail images remained visible or enabled after disabling the setting.");
        Console.WriteLine("Mail sender image binding and disable checks passed.");
        var list = window.FindControl<ListBox>("MessageList")!;
        async Task ClickRow(int index, string modifier)
        {
            var row = (Control)list.ContainerFromIndex(index)!;
            var point = row.PointToScreen(new Point(50, row.Bounds.Height / 2));
            await Input(modifier, "click", ((int)point.X).ToString(), ((int)point.Y).ToString());
        }
        await ClickRow(0, "none");
        await ClickRow(2, "Control_L");
        if (vm.SelectedMessages.Count != 2) throw new InvalidOperationException($"Ctrl-click selected {vm.SelectedMessages.Count}, expected 2.");
        await ClickRow(4, "Shift_L");
        if (vm.SelectedMessages.Count < 3) throw new InvalidOperationException("Shift-click did not select a range.");
        await Input("Control_L", "a");
        if (vm.SelectedMessages.Count != previewMessages.Length) throw new InvalidOperationException($"Ctrl+A selected {vm.SelectedMessages.Count}, expected {previewMessages.Length}.");
        vm.Messages[1] = vm.Messages[1] with { IsRead = true, IsFlagged = true };
        if (vm.SelectedMessages.Count != previewMessages.Length || list.SelectedItems!.Count != previewMessages.Length)
            throw new InvalidOperationException("A message metadata refresh collapsed multi-selection.");
        await Shot("mail-multiselect-light");
        var moveFolders = folders.Concat(new[]
        {
            new MailFolderItem(new(mailbox.Id, "projects", "Projects", 0, 0, ParentProviderId: "inbox"), mailbox.DisplayName),
            new MailFolderItem(new(mailbox.Id, "launch", "Autumn launch", 0, 0, ParentProviderId: "projects"), mailbox.DisplayName),
            new MailFolderItem(new("studio:alex@studio.example", "inbox", "Inbox", 0, 0), "Studio"),
            new MailFolderItem(new("studio:alex@studio.example", "archive", "Archive", 0, 0), "Studio")
        }).ToArray();
        var move = new MailMoveWindow(moveFolders, previewMessages.Length,
            folder => folder.MailboxId == mailbox.Id && folder.ProviderId != "inbox");
        var moveResult = move.ChooseAsync(window);
        move.Position = new PixelPoint(0, 0);
        await Task.Delay(200);
        var tree = move.FindControl<TreeView>("FoldersTree")!;
        var accountRoot = (TreeViewItem)tree.Items[0]!;
        var inboxNode = accountRoot.Items.Cast<TreeViewItem>().Single(item => (string)item.Header! == "Inbox");
        inboxNode.IsExpanded = true;
        var projectsNode = (TreeViewItem)inboxNode.Items[0]!;
        projectsNode.IsExpanded = true;
        tree.SelectedItem = inboxNode;
        if (move.FindControl<Button>("MoveButton")!.IsEnabled) throw new InvalidOperationException("Move accepted current folder.");
        tree.SelectedItem = projectsNode.Items[0];
        if (!move.FindControl<Button>("MoveButton")!.IsEnabled ||
            !move.FindControl<TextBlock>("DestinationPath")!.Text!.EndsWith("Inbox / Projects / Autumn launch"))
            throw new InvalidOperationException("Nested destination could not be selected.");
        await Shot("mail-move-browser-light", move);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Shot("mail-move-browser-dark", move);
        move.Close();
        if (await moveResult is not null) throw new InvalidOperationException("Closing Move should cancel.");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Console.WriteLine("Move browser nesting, validation and cancellation checks passed.");
        vm.MailSenderImagesEnabled = true;
        await Task.Delay(200);
        var threadView = window.GetVisualDescendants().OfType<ConversationThreadView>().Single();
        var headerImages = threadView.GetVisualDescendants().OfType<AsyncImage>().ToArray();
        if (!threadView.ShowSenderImages || headerImages.Length == 0 || headerImages.Any(i => !i.AllowLoading || !i.IsVisible))
            throw new InvalidOperationException("Conversation sender images did not follow the mail setting.");
        vm.MailSenderImagesEnabled = false;
        if (threadView.ShowSenderImages || headerImages.Any(i => i.AllowLoading || i.IsVisible))
            throw new InvalidOperationException("Conversation sender images remained enabled.");
        Console.WriteLine("Conversation sender image toggle checks passed.");
        list.SelectedItems!.Clear();
        vm.SelectedMessage = null;
        Console.WriteLine("Native Ctrl-click, Shift-click and Ctrl+A checks passed.");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        vm.ConversationThread.RefreshTheme();
        await Shot("mail-dark");
        // Synthetic progress state for visual review; no provider operation runs here.
        vm.SyncSteps.Add(new SyncStep("Send queued mail") { Detail = "Complete", Progress = 100, Indeterminate = false });
        vm.SyncSteps.Add(new SyncStep("Sync mailboxes") { Detail = "1 of 2 mailboxes complete", Running = true, Progress = 50, Indeterminate = false });
        vm.SyncSteps.Add(new SyncStep("alex@work.example") { Detail = "Inbox", Running = true });
        vm.SyncSteps.Add(new SyncStep("Reconcile drafts"));
        var syncButton = window.FindControl<Button>("SyncStatusButton")!;
        syncButton.Flyout!.ShowAt(syncButton);
        await Shot("sync-progress-dark");
        syncButton.Flyout.Hide();
        vm.SyncSteps.Clear();
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
            if (name == "files") await ((AsyncCommand)vm.DriveWorkspace!.GridViewCommand).ExecuteAsync();
            await Shot(name + "-light");
            if (name == "files")
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "NewFolderButton");
                button.Flyout!.ShowAt(button);
                await Shot("drive-new-folder-light");
                button.Flyout.Hide();
            }
            if (name == "people")
            {
                vm.PeopleCardView = true;
                await Shot("people-cards-light");
                if (!window.FindControl<ListBox>("PeopleBoxes")!.IsVisible || window.FindControl<ListBox>("PeopleCards")!.IsVisible)
                    throw new InvalidOperationException("People card view did not switch.");
                vm.PeopleCardView = false;
            }
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await Shot(name + "-dark");
            if (name == "people")
            {
                vm.PeopleCardView = true;
                await Shot("people-cards-dark");
                vm.PeopleCardView = false;
            }
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        }
        window.Width = 1024;
        window.Height = 480;
        await Shot("minimum-height");
        var rail = window.FindControl<Grid>("AppRailLayout")!;
        var modules = window.FindControl<StackPanel>("RailModules")!;
        var settings = window.FindControl<Button>("RailSettings")!;
        if (modules.Bounds.Bottom > settings.Bounds.Top || settings.Bounds.Bottom > rail.Bounds.Height)
            throw new InvalidOperationException("Navigation overlaps or clips at the minimum window height.");
        window.Width = 390;
        window.Height = 844;
        await ((AsyncCommand)vm.ShowUnifiedInboxCommand).ExecuteAsync();
        foreach (var message in previewMessages) vm.Messages.Add(message);
        await Shot("mail-phone");
        window.Close();
        var attachment = new FilePreviewWindow("Meeting notes.txt", "text/plain", 0,
            System.Text.Encoding.UTF8.GetBytes("Design review\n\nThursday, 10:00\n\nDiscuss the autumn launch and agree on next steps."),
            provider, vm.Accounts.ToArray())
        { WindowDecorations = WindowDecorations.None, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(0, 0) };
        attachment.Show();
        await Shot("attachment-preview-light", attachment);
        attachment.Close();
        var save = new AttachmentDriveSaveWindow(new AttachmentDriveSaveViewModel(
            provider, vm.Accounts.ToArray(), "Meeting notes.txt", "text/plain", []))
        { WindowDecorations = WindowDecorations.None, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(0, 0) };
        save.Show();
        await Shot("attachment-save-drive-light", save);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        await Shot("attachment-save-drive-dark", save);
        save.Close();
        Directory.Delete(directory, true);

        async Task Input(params string[] input)
        {
            var info = new ProcessStartInfo("python3");
            info.ArgumentList.Add("tools/BetterMail.UiPreview/input.py");
            foreach (var part in input) info.ArgumentList.Add(part);
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Input injection failed.");
            await Task.Delay(300);
        }

        async Task Shot(string name, Window? target = null)
        {
            target ??= window;
            await Task.Delay(1800);
            var process = Process.Start(new ProcessStartInfo("python3") { ArgumentList = { "tools/BetterMail.UiPreview/capture.py", Path.Combine(output, name + ".png"), ((int)target.Width).ToString(), ((int)target.Height).ToString() } })!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Screenshot capture failed.");
            Console.WriteLine(name);
        }
    }
    private static async Task CheckImageConsentAsync()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new AsyncImage { Width = 64, Height = 64, Request = new("consent-test-" + Guid.NewGuid(), async token =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return null;
        }) };
        var host = new Window { Width = 100, Height = 100, Content = image };
        host.Show();
        await Task.Delay(200);
        if (calls != 0) throw new InvalidOperationException("Image requested before consent.");
        image.AllowLoading = true;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        image.AllowLoading = false;
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        image.Request = new("consent-cached-test-" + Guid.NewGuid(), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<byte[]?>(PreviewProvider.SampleThumbnail());
        });
        await Task.Delay(100);
        if (calls != 1) throw new InvalidOperationException("Image requested after consent revoked.");
        image.AllowLoading = true;
        for (var i = 0; i < 50 && image.GetVisualDescendants().OfType<Image>().Single().Source is null; i++) await Task.Delay(100);
        if (image.GetVisualDescendants().OfType<Image>().Single().Source is null) throw new InvalidOperationException("Consented image did not load.");
        image.AllowLoading = false;
        if (image.GetVisualDescendants().OfType<Image>().Single().Source is not null) throw new InvalidOperationException("Revoked artwork remained visible.");
        host.Close();
        Console.WriteLine("Image consent default, cancellation and removal checks passed.");
    }

    private static IEnumerable<NoteTreeNode> Descendants(IEnumerable<NoteTreeNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Descendants(n.Children)));
}

public class PreviewProvider : DispatchProxy
{
    public static readonly DateTimeOffset Today = new(DateTime.Today);
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
            "GetThumbnailAsync" => SampleThumbnail(),
            "GetDriveItemsAsync" => new CloudDriveItem[] { new("landscape", "Mountain lake.jpg", 284000, false, null, null, account.AccountId, account.ProviderId, "image/jpeg"), new("poster", "Launch artwork.png", 128000, false, null, null, account.AccountId, account.ProviderId, "image/png"), new("folder", "Autumn launch", 0, true, null, null, account.AccountId, account.ProviderId), new("brief", "Creative brief.pdf", 284000, false, null, null, account.AccountId, account.ProviderId), new("research", "Customer research.docx", 128000, false, null, null, account.AccountId, account.ProviderId), new("budget", "Launch budget.xlsx", 64000, false, null, null, account.AccountId, account.ProviderId) },
            "GetNotebooksAsync" => new NoteNotebook[] { new("notebook", "Studio notebook", account.AccountId, account.ProviderId) },
            "GetSectionsAsync" => new NoteSection[] { new("ideas", "notebook", "Ideas & planning", account.AccountId, account.ProviderId) },
            "GetPagesAsync" => new NotePage[] { new("page", "ideas", "A thoughtful workspace", Today.AddHours(9), 0, 0, account.AccountId, account.ProviderId), new("meeting", "ideas", "Design review notes", Today, 1, 0, account.AccountId, account.ProviderId) },
            "GetPageContentAsync" => new NotePageContent("page", "ideas", account.AccountId, account.ProviderId, "<html><head><title>A thoughtful workspace</title></head><body><h1>A thoughtful workspace</h1><p>A place for the work that matters.</p><h2>What we're exploring</h2><ul><li>Clear navigation across our tools</li><li>Fewer distractions and a little more breathing room</li><li>Thoughtful details that work together</li></ul><h2>Next steps</h2><p>Bring the first ideas to Thursday's design review.</p></body></html>"),
            _ => Empty(m.ReturnType.GenericTypeArguments[0])
        };
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(m.ReturnType.GenericTypeArguments[0]).Invoke(null, [value]);
    }
    public static byte[] SampleThumbnail()
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(128, 128));
        var canvas = surface.Canvas;
        canvas.Clear(SkiaSharp.SKColor.Parse("#C8DEE8"));
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColor.Parse("#6D8F88"), IsAntialias = true };
        using var mountain = new SkiaSharp.SKPath();
        mountain.MoveTo(0, 90); mountain.LineTo(45, 32); mountain.LineTo(92, 90); mountain.Close();
        canvas.DrawPath(mountain, paint);
        paint.Color = SkiaSharp.SKColor.Parse("#548399"); canvas.DrawRect(0, 90, 128, 38, paint);
        using var image = surface.Snapshot();
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }
    private static object Empty(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? Array.CreateInstance(type.GenericTypeArguments[0], 0) : throw new NotSupportedException(type.Name);
}

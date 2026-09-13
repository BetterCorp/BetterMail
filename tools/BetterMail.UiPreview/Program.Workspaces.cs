using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using BetterMail.App;
using BetterMail.Core;
using System.Diagnostics;
using System.Reflection;

internal static partial class Program
{
    private static async Task CaptureWorkspaceFixesAsync(string output)
    {
        var vm = new MainWindowViewModel(null, Path.Combine(Path.GetTempPath(), "bettermail-workspaces-" + Guid.NewGuid()), _ => { }, _ => { }, null,
            workspaceProvider: DispatchProxy.Create<IWorkspaceProvider, PreviewProvider>());
        vm.Accounts.Add(PreviewProvider.Account);
        // Exercise initial contacts with no mailbox tree loaded yet.
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900, WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new(0, 0) };
        window.Show();
        await ((AsyncCommand)vm.ShowContactsCommand).ExecuteAsync();
        await (Task)typeof(MainWindowViewModel).GetProperty("PeopleBackgroundRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        if (vm.People.Count == 0) throw new InvalidOperationException("First People load requires a manual refresh.");
        vm.People.Add(PersonEntry.Discovered(new("taylor@studio.example", "Taylor Brooks", [], 3, DateTimeOffset.Now), "Mail history"));
        foreach (var dark in new[] { false, true })
        {
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            vm.PeopleCardView = false; await Shot("people-table-" + (dark ? "dark" : "light"));
            vm.PeopleCardView = true; await Shot("people-cards-" + (dark ? "dark" : "light"));
        }
        if (window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && Equals(button.Content, "Refresh")))
            throw new InvalidOperationException("People still has a Refresh button.");
        var eventMail = new MailMessage("preview", "event-email", null, null, "inbox", "Design review",
            new MailAddress("Jamie", "jamie@studio.example"), [], DateTimeOffset.Now, "Short preview",
            "<p>Hi Alex,</p><p>Let’s review the updated designs together.</p><p>Agenda: navigation, contacts, and calendar.</p>",
            true, true, false, MailImportance.Normal, [], null);
        await (Task)typeof(MainWindowViewModel).GetMethod("CreateEventFromEmailAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [eventMail, null])!;
        if (vm.CalendarWorkspace?.IsEditorOpen != true || !vm.CalendarWorkspace.EditorDescription.Contains("Agenda:"))
            throw new InvalidOperationException("Email did not open an event with its full content.");
        await Shot("event-from-email-dark");
        await ((AsyncCommand)vm.CalendarWorkspace.CloseEditorCommand).ExecuteAsync();
        await ((AsyncCommand)vm.ShowTasksCommand).ExecuteAsync();
        await Shot("tasks-dark");
        var tasksView = window.GetVisualDescendants().OfType<TasksWorkspaceView>().Single();
        var taskList = tasksView.FindControl<ListBox>("TaskList")!;
        var row = taskList.GetVisualDescendants().OfType<ListBoxItem>().First();
        var point = row.PointToScreen(new Point(120, 15));
        var input = new ProcessStartInfo("python3") { UseShellExecute = false };
        foreach (var arg in new[] { Path.GetFullPath("tools/BetterMail.UiPreview/input.py"), "none", "doubleclick", point.X.ToString(), point.Y.ToString() }) input.ArgumentList.Add(arg);
        using (var process = Process.Start(input)!) await process.WaitForExitAsync();
        await Task.Delay(200);
        var tasks = (TasksWorkspaceViewModel)tasksView.DataContext!;
        if (!tasks.IsEditorOpen) throw new InvalidOperationException("Task double-click did not open the editor.");
        var picker = tasksView.GetVisualDescendants().OfType<CalendarDatePicker>().Single();
        if (picker.SelectedDate != tasks.EditorDueDate?.LocalDateTime.Date) throw new InvalidOperationException("Task date binding failed.");
        picker.SelectedDate = new DateTime(2026, 10, 4);
        if (tasks.EditorCalendarDate != picker.SelectedDate) throw new InvalidOperationException("Task date edit did not update the model.");
        await Shot("task-editor-dark");
        await ((AsyncCommand)tasks.CloseEditorCommand).ExecuteAsync();
        await ((AsyncCommand)vm.ShowNotesCommand).ExecuteAsync();
        await Task.Delay(200);
        var notes = (NotesWorkspaceViewModel)vm.ActiveWorkspace!;
        var tree = window.GetVisualDescendants().OfType<NotesWorkspaceView>().Single().FindControl<TreeView>("NotesTree")!;
        foreach (var kind in new[] { NoteNodeKind.Account, NoteNodeKind.Notebook, NoteNodeKind.Section })
        {
            var node = tree.GetVisualDescendants().OfType<TreeViewItem>().First(item => item.DataContext is NoteTreeNode n && n.Kind == kind);
            node.IsExpanded = true;
            await Task.Delay(200);
            if (((NoteTreeNode)node.DataContext!).Children.Any(child => child.Kind == NoteNodeKind.Placeholder))
                throw new InvalidOperationException("Expanded Notes node retained its placeholder.");
        }
        await Shot("notes-expanded-dark");
        var notebook = notes.AccountRoots[0].AllChildren[0];
        notebook.Error = "This library exceeds Microsoft's item limit (10008). Cached notes are kept. Open it in OneNote or move it to a smaller library.";
        typeof(NotesWorkspaceViewModel).GetMethod("RaisePartialErrors", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(notes, null);
        await Shot("notes-limit-dark");
        typeof(MainWindowViewModel).GetProperty("ActiveModule")!.SetValue(vm, "Mail");
        typeof(MainWindowViewModel).GetProperty("NextCalendarEvent")!.SetValue(vm,
            new CalendarEventSource(PreviewProvider.Account, new CalendarChoice(new("team", "Team", "#5576CF", true, PreviewProvider.Account.AccountId), "#5576CF", () => { }),
                new("upcoming", "team", "Review the autumn launch plans", DateTimeOffset.Now.AddMinutes(15), DateTimeOffset.Now.AddMinutes(45), "Studio", AccountId: PreviewProvider.Account.AccountId)));
        window.Width = 700;
        await Shot("mail-header-narrow-dark");
        window.Close();
        var settings = new Window { DataContext = vm, Content = new SettingsView(), Width = 1100, Height = 900, Position = new(0, 0), WindowDecorations = WindowDecorations.None };
        vm.SelectedSettingsTab = vm.SettingsTabs.Single(tab => tab.Name == "Signatures");
        settings.Show();
        if (settings.GetVisualDescendants().OfType<NativeWebView>().Any()) throw new InvalidOperationException("Native signature surface remains in the settings scroll area.");
        await Shot("signature-settings-dark", settings);
        vm.SelectedSettingsTab = vm.SettingsTabs.Single(tab => tab.Name == "Mail & notifications");
        await Shot("default-reply-settings-dark", settings);
        settings.Close();
        Console.WriteLine("Initial contacts, task double-click/date binding, and real Notes tree expansion passed.");

        async Task Shot(string name, Window? target = null)
        {
            target ??= window;
            target.Position = new(0, 0); await Task.Delay(500);
            var info = new ProcessStartInfo("python3") { UseShellExecute = false };
            info.ArgumentList.Add(Path.GetFullPath("tools/BetterMail.UiPreview/capture.py"));
            info.ArgumentList.Add(Path.Combine(output, name + ".png"));
            info.ArgumentList.Add(((int)target.Width).ToString()); info.ArgumentList.Add(((int)target.Height).ToString());
            using var process = Process.Start(info)!; await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Screenshot failed.");
        }
    }
}

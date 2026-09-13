using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using BetterMail.App;
using BetterMail.Core;
using System.Diagnostics;
using System.Reflection;

internal static partial class Program
{
    private static async Task CaptureSearchAsync(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-search-ui-" + Guid.NewGuid());
        var vm = new MainWindowViewModel(null, directory, _ => { }, _ => { }, null,
            workspaceProvider: DispatchProxy.Create<IWorkspaceProvider, PreviewProvider>());
        var account = PreviewProvider.Account;
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Alex Morgan");
        vm.Accounts.Add(account); vm.Mailboxes.Add(mailbox);
        await vm.InitializeAsync();
        vm.Folders.Add(new(new(mailbox.Id, "inbox", "Inbox", 0, 0, "inbox"), mailbox.DisplayName));
        vm.Folders.Add(new(new(mailbox.Id, "projects", "Projects", 0, 0, ParentProviderId: "inbox"), mailbox.DisplayName));
        vm.Folders.Add(new(new(mailbox.Id, "old", "Old", 0, 0, ParentProviderId: "projects"), mailbox.DisplayName));
        var message = new MailMessage(mailbox.Id, "sample", null, null, "projects", "Quarterly budget review",
            new("Jamie Chen", "jamie@example.test"), [new("Alex Morgan", account.EmailAddress)], DateTimeOffset.Now,
            "The updated numbers are ready for review.", "The updated numbers are ready for review.", false, false, true, MailImportance.Normal, ["Finance", "Projects"], null);
        vm.Messages.Add(message);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 960, WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new(0, 0) };
        window.Show(); await Task.Delay(300);
        vm.SearchText = "budget type:mail type:Mails has:attachments";
        vm.IsSearchEditing = false;
        await Shot("search-badges-light", window);
        if (vm.SearchBadges.Count(item => !item.IsValid) != 1) throw new InvalidOperationException("Invalid type was not identified independently.");
        var badgeButton = window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Edit search filters");
        badgeButton.Focus(); await Task.Delay(300);
        var input = window.FindControl<TextBox>("MailSearch")!;
        if (!input.IsFocused || !input.IsVisible || input.Text != vm.SearchText) throw new InvalidOperationException("Badges did not restore exact editable text on focus.");
        window.FindControl<Button>("RailSettings")!.Focus(); await Task.Delay(200);
        if (!vm.ShowSearchBadges) throw new InvalidOperationException("Unfocused search did not show badges.");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Shot("search-badges-dark", window);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        vm.SearchText = "budget type:mail";
        vm.GlobalSearchResults.Add(new("Mail", message.Subject, message.Preview, "Mail", message));
        vm.IsGlobalSearchOpen = true;
        typeof(MainWindowViewModel).GetProperty(nameof(vm.IsGlobalSearchRunning))!.SetValue(vm, true);
        await Task.Delay(300);
        var panel = window.FindControl<Border>("GlobalSearchResultsPanel")!;
        var progress = panel.GetVisualDescendants().OfType<ProgressBar>().Single();
        if (Math.Abs(progress.Bounds.Width - ((Control)progress.Parent!).Bounds.Width) > 1 || progress.VerticalAlignment != Avalonia.Layout.VerticalAlignment.Top)
            throw new InvalidOperationException("Search progress does not span the results table.");
        if (panel.GetVisualDescendants().OfType<Button>().Any(button => button.Content as string == "×")) throw new InvalidOperationException("Search popup still has a close button.");
        await Shot("search-results-light", window);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Shot("search-results-dark", window);
        await Input("none", "click", "35", "600");
        if (vm.IsGlobalSearchOpen) throw new InvalidOperationException("Search popup did not close on outside click.");
        typeof(MainWindowViewModel).GetProperty(nameof(vm.IsGlobalSearchRunning))!.SetValue(vm, false);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var query = "budget type:mail account:" + SearchQuery.Encode(account.AccountId) + " in:{Inbox/Projects} notin:{Inbox/Projects/Old} category:Finance date:{>=2026-09-01}";
        string? applied = null;
        var options = new SearchOptionsWindow(query, result => applied = result, vm) { Position = new(0, 0), WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual };
        options.Show(); await Task.Delay(300);
        if (options.GetVisualDescendants().OfType<Button>().Any(button => button.Content as string == "Read query into fields")) throw new InvalidOperationException("Obsolete query import button remains.");
        var choices = options.FindControl<StackPanel>("FieldsPanel")!.Children.OfType<SearchMultiChoice>().ToArray();
        var typeControl = choices[0];
        var typeButton = typeControl.Children.OfType<Button>().Single();
        var typeFlyout = (Flyout)typeButton.Flyout!;
        typeFlyout.ShowAt(typeButton); await Task.Delay(200);
        await Shot("search-type-multiselect-light", options);
        var checks = ((Control)typeFlyout.Content!).GetVisualDescendants().OfType<CheckBox>().ToArray();
        checks.Single(box => box.Content as string == "Mail").IsChecked = false;
        checks.Single(box => box.Content as string == "Drive").IsChecked = true;
        typeFlyout.Hide(); await Task.Delay(100);
        var generated = SearchQuery.Parse(options.FindControl<TextBox>("QueryText")!.Text!);
        if (generated.Scope != "Drive" || generated.HasMailFilters || generated.Values("category").Count > 0) throw new InvalidOperationException("Hidden mail-only conditions leaked into Drive search.");
        checks.Single(box => box.Content as string == "Drive").IsChecked = false;
        checks.Single(box => box.Content as string == "Mail").IsChecked = true;
        await Shot("search-options-light", options);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Shot("search-options-dark", options);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var dateLabel = options.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "Received date / time");
        var dateScroll = dateLabel.GetVisualAncestors().OfType<ScrollViewer>().First();
        dateScroll.Offset = new Vector(0, dateScroll.Offset.Y + dateLabel.TranslatePoint(new Point(), dateScroll)!.Value.Y - 12);
        await Shot("search-date-filters-light", options);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Shot("search-date-filters-dark", options);
        var syntax = options.GetVisualDescendants().OfType<Expander>().Single(); syntax.IsExpanded = true;
        options.FindControl<SelectableTextBlock>("SyntaxHelp")!.BringIntoView();
        await Shot("search-syntax-dark", options);
        options.FindControl<Button>("ApplyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (applied is null || SearchQuery.Parse(applied).Values("notin").Count != 1) throw new InvalidOperationException("Advanced form did not apply folder exclusions.");
        var folders = new SearchFoldersWindow(vm, false, [account.AccountId], [], true) { Position = new(0, 0), WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual };
        folders.Show(); await Task.Delay(300);
        foreach (var node in folders.GetVisualDescendants().OfType<TreeViewItem>().ToArray()) node.IsExpanded = true;
        await Task.Delay(100);
        foreach (var node in folders.GetVisualDescendants().OfType<TreeViewItem>().ToArray()) node.IsExpanded = true;
        await Shot("search-folder-exclusions-dark", folders); folders.Close();
        var driveFolders = new SearchFoldersWindow(vm, true, [account.AccountId], [], false) { Position = new(0, 0), WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual };
        driveFolders.Show(); await Task.Delay(100);
        var driveTree = driveFolders.GetVisualDescendants().OfType<TreeView>().Single();
        var root = driveTree.Items.OfType<TreeViewItem>().Single(); root.IsExpanded = true;
        for (var i = 0; i < 20 && root.Items.OfType<TreeViewItem>().All(item => item.Tag is null); i++) await Task.Delay(100);
        if (!root.Items.OfType<TreeViewItem>().Any(item => item.Tag as string == "Autumn launch")) throw new InvalidOperationException("Drive folder picker did not load child folders.");
        if (root.Items.OfType<TreeViewItem>().Count() != 1) throw new InvalidOperationException("Drive folder picker included files.");
        await Shot("search-drive-folders-dark", driveFolders); driveFolders.Close();
        var settings = new Window { DataContext = vm, Content = new SettingsView(), Width = 1100, Height = 900,
            Position = new(0, 0), WindowDecorations = WindowDecorations.None, WindowStartupLocation = WindowStartupLocation.Manual };
        vm.SelectedSettingsTab = vm.SettingsTabs.Single(tab => tab.Name == "Mail & notifications");
        settings.Show(); await Task.Delay(200);
        var close = settings.GetVisualDescendants().OfType<Button>().First(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Close settings");
        var title = settings.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "Settings");
        if (close.Bounds.Right > title.Bounds.Left) throw new InvalidOperationException("Settings close button is not left of its title.");
        var heading = settings.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "Separate mail windows");
        var scroll = heading.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Offset.Y + heading.TranslatePoint(new Point(), scroll)!.Value.Y - 12);
        await Shot("preview-action-settings-dark", settings);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        await Shot("preview-action-settings-light", settings);
        settings.Close(); window.Close();
        Console.WriteLine("Search badges, focus, light dismissal, full-width progress, grouped multi-select form, exclusions, and left Settings close passed.");

        async Task Shot(string name, Window target)
        {
            target.Position = new PixelPoint(0, 0);
            await Task.Delay(1000);
            using var process = Process.Start(new ProcessStartInfo("python3") { ArgumentList = { "tools/BetterMail.UiPreview/capture.py", Path.Combine(output, name + ".png"), ((int)target.Width).ToString(), ((int)target.Height).ToString() } })!;
            await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException("Screenshot failed.");
            Console.WriteLine(name);
        }
        async Task Input(params string[] parts)
        {
            var info = new ProcessStartInfo("python3"); info.ArgumentList.Add("tools/BetterMail.UiPreview/input.py");
            foreach (var part in parts) info.ArgumentList.Add(part);
            using var process = Process.Start(info)!; await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Input failed.");
            await Task.Delay(200);
        }
    }
}

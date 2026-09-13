using Avalonia.Controls;
using Avalonia.Layout;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class SearchFoldersWindow : Window
{
    private readonly HashSet<string> _selected;
    private readonly TextBlock _notice = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TreeView _tree = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly MainWindowViewModel? _owner;
    private readonly Dictionary<string, CheckBox> _boxes = [];
    private readonly StackPanel _selectedRows = new() { Spacing = 3 };
    public SearchFoldersWindow(MainWindowViewModel? owner, bool drive, IReadOnlyList<string> accounts, IEnumerable<string> selected, bool excluding)
    {
        _owner = owner; _selected = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Title = excluding ? "Exclude folders" : "Include folders";
        Width = 620; Height = 720; MinWidth = 400; MinHeight = 400;
        var apply = new Button { Content = "Use selected folders" }; apply.Click += (_, _) => Close((IReadOnlyList<string>)_selected.ToArray());
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close();
        var clear = new Button { Content = "Clear selection" }; clear.Click += (_, _) => { _selected.Clear(); foreach (var box in _boxes.Values) box.IsChecked = false; RefreshSelected(); };
        var find = new TextBox { PlaceholderText = "Find loaded folders" };
        find.TextChanged += (_, _) => { foreach (var node in _tree.Items.OfType<TreeViewItem>()) Filter(node, find.Text ?? ""); };
        var grid = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 10 };
        grid.Children.Add(new TextBlock { Text = Title, FontSize = 20 });
        Grid.SetRow(find, 1); grid.Children.Add(find);
        var scroll = new ScrollViewer { Content = _tree }; Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
        var selection = new StackPanel { Spacing = 5, Children = { _notice, new ScrollViewer { MaxHeight = 100, Content = _selectedRows } } };
        Grid.SetRow(selection, 3); grid.Children.Add(selection);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { clear, cancel, apply } };
        Grid.SetRow(footer, 4); grid.Children.Add(footer); Content = grid;
        Closed += (_, _) => _closed.Cancel();
        if (owner is not null)
        {
            if (drive)
            {
                _tree.ItemsSource = owner.Accounts.Where(account => account.Capabilities.HasFlag(ProviderCapabilities.Files) &&
                    (accounts.Count == 0 || accounts.Contains(account.AccountId))).Select(account => DriveNode(account, null)).ToArray();
            }
            else
            {
                var mailboxIds = owner.Mailboxes.Where(mailbox => accounts.Count == 0 || accounts.Contains(mailbox.Id) || accounts.Contains(mailbox.AccountId)).Select(mailbox => mailbox.Id).ToHashSet();
                _tree.ItemsSource = MailMoveFolder.Build(owner.Folders.Where(folder => mailboxIds.Contains(folder.MailboxId))).Select(MailNode).ToArray();
            }
        }
        RefreshSelected();
        _notice.Text += drive ? " Expand folders to browse Drive." : " Folder choices are limited to the selected accounts.";
    }
    private TreeViewItem MailNode(MailMoveFolder folder) => new()
    {
        Header = folder.Folder is { } item ? Choice(item.MailboxId + "::" + _owner!.SearchFolderPath(item), folder.Name) : new TextBlock { Text = folder.Name },
        Tag = folder.Name, IsExpanded = folder.Folder is null,
        ItemsSource = folder.Children.Select(MailNode).ToArray()
    };
    private TreeViewItem DriveNode(MailAccount account, CloudDriveItem? item)
    {
        var path = item?.Path ?? "/";
        var node = new TreeViewItem { Header = Choice(account.AccountId + "::" + path, item?.Name ?? account.DisplayName + " · " + account.EmailAddress),
            Tag = item?.Name ?? account.EmailAddress, ItemsSource = new[] { new TreeViewItem { Header = "Expand to load folders" } } };
        var loaded = false; var loading = false;
        node.Expanded += async (_, args) =>
        {
            if (args.Source != node || loaded || loading) return;
            loading = true;
            try
            {
                if (_owner!.FilesProvider is null) throw new InvalidOperationException("Drive is unavailable.");
                var children = await _owner.FilesProvider.GetDriveItemsAsync(account, item, _closed.Token);
                if (_closed.IsCancellationRequested) return;
                node.ItemsSource = children.Where(child => child.IsFolder).OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => DriveNode(account, child)).ToArray();
                loaded = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _notice.Text = "Folders could not be loaded: " + error.Message + " Collapse and expand to retry."; }
            finally { loading = false; }
        };
        return node;
    }
    private CheckBox Choice(string value, string label)
    {
        var box = new CheckBox { Content = label, IsChecked = _selected.Contains(value) };
        _boxes[value] = box;
        box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) _selected.Add(value); else _selected.Remove(value); RefreshSelected(); };
        return box;
    }
    private void RefreshSelected()
    {
        _notice.Text = $"{_selected.Count} selected";
        _selectedRows.Children.Clear();
        foreach (var value in _selected.ToArray())
        {
            var remove = new Button { Content = "× " + value, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            remove.Click += (_, _) => { _selected.Remove(value); if (_boxes.TryGetValue(value, out var box)) box.IsChecked = false; RefreshSelected(); };
            _selectedRows.Children.Add(remove);
        }
    }
    private static bool Filter(TreeViewItem node, string text)
    {
        var childMatch = false;
        foreach (var child in node.Items.OfType<TreeViewItem>()) childMatch |= Filter(child, text);
        node.IsVisible = childMatch || (node.Tag as string ?? "").Contains(text, StringComparison.OrdinalIgnoreCase);
        if (text.Length > 0 && childMatch) node.IsExpanded = true;
        return node.IsVisible;
    }
}

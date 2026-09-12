using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BetterMail.App;

public sealed partial class MailMoveWindow : Window
{
    private readonly Func<MailFolderItem, bool> _canMove = _ => false;
    private MailFolderItem? _destination;

    public MailMoveWindow() => InitializeComponent();

    public MailMoveWindow(IEnumerable<MailFolderItem> folders, int messageCount,
        Func<MailFolderItem, bool> canMove) : this()
    {
        _canMove = canMove;
        Heading.Text = messageCount == 1 ? "Move message" : $"Move {messageCount:N0} messages";
        FoldersTree.ItemsSource = MailMoveFolder.Build(folders).Select(CreateItem).ToArray();
    }

    public Task<MailFolderItem?> ChooseAsync(Window owner) => ShowDialog<MailFolderItem?>(owner);

    private static TreeViewItem CreateItem(MailMoveFolder node) => new()
    {
        Header = node.Name,
        Tag = node,
        IsExpanded = node.Folder is null,
        ItemsSource = node.Children.Select(CreateItem).ToArray()
    };

    private void FolderSelected(object? sender, SelectionChangedEventArgs args)
    {
        var node = (FoldersTree.SelectedItem as TreeViewItem)?.Tag as MailMoveFolder;
        _destination = node?.Folder;
        DestinationPath.Text = node?.Path ?? "Select a folder";
        MoveButton.IsEnabled = _destination is not null && _canMove(_destination);
        SelectionHelp.Text = node is null || node.Folder is null
            ? "Expand an account and choose a folder."
            : MoveButton.IsEnabled ? ""
            : "Choose a different folder in the source mailbox. Cross-account moves are not available yet.";
    }

    private void MoveClicked(object? sender, RoutedEventArgs args)
    {
        if (_destination is not null && _canMove(_destination)) Close(_destination);
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close();
}

internal sealed record MailMoveFolder(string Name, string Path, MailFolderItem? Folder,
    IReadOnlyList<MailMoveFolder> Children)
{
    internal static IReadOnlyList<MailMoveFolder> Build(IEnumerable<MailFolderItem> source) =>
        source.GroupBy(folder => folder.MailboxId).Select(group =>
        {
            var folders = group.DistinctBy(folder => folder.ProviderId).ToArray();
            var byId = folders.ToDictionary(folder => folder.ProviderId, StringComparer.Ordinal);
            var children = folders.ToLookup(folder => folder.ParentProviderId, StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var accountName = group.First().MailboxDisplayName;
            var address = group.Key[(group.Key.LastIndexOf(':') + 1)..];
            if (!accountName.Contains(address, StringComparison.OrdinalIgnoreCase)) accountName += " · " + address;
            MailMoveFolder BuildNode(MailFolderItem folder, string parentPath)
            {
                visited.Add(folder.ProviderId);
                var path = parentPath + " / " + folder.DisplayName;
                return new(folder.DisplayName, path, folder,
                    children[folder.ProviderId].OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .Where(item => !visited.Contains(item.ProviderId)).Select(item => BuildNode(item, path)).ToArray());
            }
            var roots = folders.Where(folder => folder.ParentProviderId is null || !byId.ContainsKey(folder.ParentProviderId))
                .OrderBy(folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(folder => BuildNode(folder, accountName)).ToList();
            // Broken parent references and cycles must not hide an otherwise usable folder.
            foreach (var folder in folders)
                if (!visited.Contains(folder.ProviderId)) roots.Add(BuildNode(folder, accountName));
            return new MailMoveFolder(accountName, accountName, null, roots);
        }).ToArray();
}

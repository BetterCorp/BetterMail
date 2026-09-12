using BetterMail.App;
using BetterMail.Core;
using Xunit;

namespace BetterMail.Tests;

public sealed class MailMoveFolderTests
{
    private static MailFolderItem Folder(string mailbox, string id, string name, string? parent = null) =>
        new(new MailFolder(mailbox, id, name, 0, 0, ParentProviderId: parent), "Work");

    [Fact]
    public void AccountTreesKeepIdenticalFolderIdsSeparateAndShowFullPath()
    {
        var roots = MailMoveFolder.Build([
            Folder("a:alex@work.example", "inbox", "Inbox"),
            Folder("a:alex@work.example", "projects", "Projects", "inbox"),
            Folder("b:alex@studio.example", "inbox", "Inbox")]);
        Assert.Equal(2, roots.Count);
        Assert.Contains("alex@work.example", roots[0].Name);
        Assert.Equal("Work · alex@work.example / Inbox / Projects", roots[0].Children[0].Children[0].Path);
        Assert.Empty(roots[1].Children[0].Children);
    }

    [Fact]
    public void MissingParentsAndCyclesDoNotHideFoldersOrRecurseForever()
    {
        var roots = MailMoveFolder.Build([
            Folder("a", "orphan", "Orphan", "missing"),
            Folder("a", "a", "A", "b"), Folder("a", "b", "B", "a"),
            Folder("a", "self", "Self", "self")]);
        static IEnumerable<string> Ids(MailMoveFolder node) =>
            (node.Folder is { } folder ? new[] { folder.ProviderId } : []).Concat(node.Children.SelectMany(Ids));
        Assert.Equal(new[] { "a", "b", "orphan", "self" }, roots.SelectMany(Ids).Order());
    }
}

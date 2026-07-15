using BetterMail.App;

namespace BetterMail.Tests;

public sealed class WindowSessionStoreTests
{
    [Fact]
    public void SavesDistinctPreviewWindowsAndIgnoresInvalidState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-session-{Guid.NewGuid():N}");
        try
        {
            var store = new WindowSessionStore(directory);
            var first = new PreviewWindowSession("mailbox-one", "message-one");
            var second = new PreviewWindowSession("mailbox-two", "message-two");

            store.Save([first, first, second]);

            Assert.Equal([first, second], store.Load());
            File.WriteAllText(Path.Combine(directory, "window-session.json"), "not json");
            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

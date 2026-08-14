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

            var calendarEvent = new CalendarEventWindowSession("account", "calendar", "event");
            var compose = new ComposeWindowSession("draft");
            store.Save(new([first, first, second], [calendarEvent], [compose]));

            var saved = store.Load();
            Assert.Equal([first, second], saved.Previews);
            Assert.Equal([calendarEvent], saved.Events);
            Assert.Equal([compose], saved.Composes);
            File.WriteAllText(Path.Combine(directory, "window-session.json"), "not json");
            Assert.Empty(store.Load().Previews);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadsLegacyPreviewArray()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-session-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "window-session.json"),
                """[{"MailboxId":"mailbox","ProviderMessageId":"message"}]""");

            Assert.Equal(
                [new PreviewWindowSession("mailbox", "message")],
                new WindowSessionStore(directory).Load().Previews);
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

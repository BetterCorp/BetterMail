using System.Text;
using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class ConversationThreadTests
{
    [Fact]
    public void ProjectsProviderNeutralThreadsWithSafeMissingIdFallback()
    {
        var messages = new[]
        {
            Message("mailbox-a", "one", "conversation", "<one@example>", 1),
            Message("mailbox-a", "two", "conversation", "<two@example>", 2),
            Message("mailbox-b", "three", "conversation", "<three@example>", 3),
            Message("mailbox-a", "four", null, "<shared@example>", 4),
            Message("mailbox-a", "five", null, "<SHARED@example>", 5),
            Message("mailbox-a", "six", null, null, 6),
            Message("mailbox-a", "seven", null, null, 7)
        };

        var threads = ConversationThread.Project(messages);

        Assert.Equal(5, threads.Count);
        Assert.Equal(2, threads.Single(thread =>
            thread.Identity == "mailbox-a:conversation:conversation").Messages.Count);
        Assert.Equal(2, threads.Single(thread =>
            thread.Identity.Contains(":internet:shared@example", StringComparison.Ordinal)).Messages.Count);
        Assert.Equal(2, threads.Count(thread => thread.Identity.Contains(":message:", StringComparison.Ordinal)));
        Assert.NotEqual(
            ConversationThread.ThreadIdentity(messages[0]),
            ConversationThread.ThreadIdentity(messages[2]));
    }

    [Fact]
    public async Task ReconcilesStableItemsExpandsSelectionAndRoutesActions()
    {
        ConversationActionRequest? action = null;
        var renderer = new MailContentRenderer();
        var viewModel = new ConversationThreadViewModel(renderer, request => action = request);
        var old = Message(
            "mailbox-a", "old", "thread", "<old@example>", 1,
            "<p>Old</p><div class='gmail_signature'>Regards</div><img src='https://images.example/old.png' alt='Old chart'>");
        var middle = Message(
            "mailbox-a", "middle", "thread", "<middle@example>", 2,
            "<p>Middle</p><img src='https://images.example/middle.png' alt='Middle chart'>");
        var newest = Message("mailbox-a", "newest", "thread", "<newest@example>", 3, "<p>Newest</p>");

        viewModel.Reconcile([old, middle, newest], old);

        var thread = Assert.Single(viewModel.Threads);
        var oldItem = thread.Messages[0];
        var middleItem = thread.Messages[1];
        Assert.Same(oldItem, viewModel.SelectedMessage);
        Assert.True(oldItem.IsExpanded);
        Assert.False(middleItem.IsExpanded);
        Assert.True(thread.Messages[2].IsExpanded);
        await WaitUntilAsync(() => oldItem.HasBlockedRemoteContent && middleItem.HasBlockedRemoteContent);
        Assert.Equal(1, Count(Decode(oldItem.BodyUri), "Regards"));

        viewModel.AllowRemoteContentCommand.Execute(oldItem);
        await WaitUntilAsync(() => Decode(oldItem.BodyUri).Contains("https://images.example/old.png", StringComparison.Ordinal));
        Assert.Contains("https://images.example/old.png", Decode(oldItem.BodyUri));
        Assert.True(middleItem.HasBlockedRemoteContent);

        var updatedOld = old with { IsRead = true, Preview = "Updated preview" };
        viewModel.Reconcile([updatedOld, middle, newest], updatedOld);

        Assert.Same(thread, viewModel.SelectedThread);
        Assert.Same(oldItem, viewModel.SelectedMessage);
        Assert.True(oldItem.Message.IsRead);
        Assert.Contains("https://images.example/old.png", Decode(oldItem.BodyUri));

        viewModel.ToggleMessageCommand.Execute(middleItem);
        Assert.Same(middleItem, viewModel.SelectedMessage);
        Assert.True(middleItem.IsExpanded);
        Assert.True(oldItem.IsExpanded);
        viewModel.ReplyAllCommand.Execute(null);
        Assert.Equal(ConversationAction.ReplyAll, action?.Action);
        Assert.Equal("middle", action?.Message.ProviderId);
    }

    [Fact]
    public async Task BodyChangeResetsOnlyThatMessagesRemotePermission()
    {
        var viewModel = new ConversationThreadViewModel();
        var first = Message(
            "mailbox", "first", "thread", null, 1,
            "<img src='https://images.example/first.png' alt='First'>");
        var second = Message(
            "mailbox", "second", "thread", null, 2,
            "<img src='https://images.example/second.png' alt='Second'>");
        viewModel.Reconcile([first, second], first);
        var firstItem = viewModel.SelectedThread!.Messages[0];
        var secondItem = viewModel.SelectedThread.Messages[1];
        viewModel.AllowRemoteContentCommand.Execute(firstItem);
        viewModel.AllowRemoteContentCommand.Execute(secondItem);
        await WaitUntilAsync(() =>
            Decode(firstItem.BodyUri).Contains("https://images.example/first.png", StringComparison.Ordinal) &&
            Decode(secondItem.BodyUri).Contains("https://images.example/second.png", StringComparison.Ordinal));

        viewModel.Reconcile(
            [first with { Body = "<img src='https://images.example/changed.png' alt='Changed'>" }, second],
            first);

        await WaitUntilAsync(() => firstItem.HasBlockedRemoteContent);
        Assert.False(secondItem.HasBlockedRemoteContent);
        Assert.DoesNotContain("https://images.example/changed.png", Decode(firstItem.BodyUri));
        Assert.Contains("https://images.example/second.png", Decode(secondItem.BodyUri));
    }

    [Fact]
    public async Task SelectedBodyAcceptsLoadedInlineAttachments()
    {
        var viewModel = new ConversationThreadViewModel();
        var message = Message(
            "mailbox", "message", "thread", null, 1,
            "<p>Logo</p><img src='cid:logo@example' alt='Logo'>");
        viewModel.Reconcile([message], message);

        viewModel.SetAttachments(
            message,
            [new MailAttachment("attachment", "logo.png", "image/png", 3, true, "logo@example", [1, 2, 3])]);

        await WaitUntilAsync(() => Decode(viewModel.SelectedMessage!.BodyUri)
            .Contains("data:image/png;base64,AQID", StringComparison.Ordinal));
        Assert.Contains("data:image/png;base64,AQID", Decode(viewModel.SelectedMessage!.BodyUri));
    }

    [Fact]
    public async Task ReusesRenderedMessageWhenReturningToAThread()
    {
        var viewModel = new ConversationThreadViewModel();
        var first = Message("mailbox", "first", "thread-one", null, 1, "<p>cached-marker</p>");
        var second = Message("mailbox", "second", "thread-two", null, 2, "<p>second</p>");
        viewModel.Reconcile([first], first);
        var cached = viewModel.SelectedMessage!;
        await WaitUntilAsync(() => Decode(cached.BodyUri).Contains("cached-marker", StringComparison.Ordinal));

        viewModel.Reconcile([second], second);
        viewModel.Reconcile([first], first);

        Assert.Same(cached, viewModel.SelectedMessage);
        Assert.Contains("cached-marker", Decode(viewModel.SelectedMessage!.BodyUri));
    }

    [Fact]
    public void MetadataSyncDoesNotRebuildTheOpenConversation()
    {
        var viewModel = new ConversationThreadViewModel();
        var message = Message("mailbox", "message", "thread", null, 1, "<p>body</p>");
        viewModel.Reconcile([message], message);
        var thread = Assert.Single(viewModel.Threads);
        var threadChanges = 0;
        var messageChanges = 0;
        viewModel.Threads.CollectionChanged += (_, _) => threadChanges++;
        thread.Messages.CollectionChanged += (_, _) => messageChanges++;

        viewModel.Reconcile([message with { IsRead = true }], message with { IsRead = true });

        Assert.Equal(0, threadChanges);
        Assert.Equal(0, messageChanges);
        Assert.Same(thread, viewModel.SelectedThread);
    }

    [Fact]
    public async Task LargeCachedBodiesFinishRenderingAfterSelectionReturns()
    {
        var viewModel = new ConversationThreadViewModel();
        var body = $"<p>{new string('x', 40_000)} rendered-marker</p>";
        var message = Message("mailbox", "large", "thread", null, 1, body);

        viewModel.Reconcile([message], message);

        for (var attempt = 0; attempt < 100 &&
             !Decode(viewModel.SelectedMessage!.BodyUri).Contains("rendered-marker", StringComparison.Ordinal);
             attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Contains("rendered-marker", Decode(viewModel.SelectedMessage!.BodyUri));
    }

    [Fact]
    public void UsesResponsiveThreadBreakpoint()
    {
        Assert.True(ConversationThreadView.IsCompactWidth(639));
        Assert.False(ConversationThreadView.IsCompactWidth(640));
    }

    private static MailMessage Message(
        string mailbox,
        string providerId,
        string? conversationId,
        string? internetId,
        int hour,
        string? body = null) =>
        new(
            mailbox,
            providerId,
            conversationId,
            internetId,
            "inbox",
            $"Subject {providerId}",
            new("Sender", "sender@example.com"),
            [new("Recipient", "recipient@example.com")],
            new DateTimeOffset(2026, 7, 14, hour, 0, 0, TimeSpan.Zero),
            $"Preview {providerId}",
            body,
            body is not null,
            false,
            false,
            MailImportance.Normal,
            [],
            null);

    private static string Decode(Uri uri) => Encoding.UTF8.GetString(
        Convert.FromBase64String(uri.OriginalString.Split(',')[1]));

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

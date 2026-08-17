namespace BetterMail.Core;

public sealed class MailProviderRouter(IEnumerable<(string ProviderId, IMailProvider Provider)> providers) :
    IMailProvider,
    ISharedMailboxProvider
{
    private readonly IReadOnlyDictionary<string, IMailProvider> _providers = providers.ToDictionary(
        static item => item.ProviderId,
        static item => item.Provider,
        StringComparer.Ordinal);

    public bool SupportsCloudDrafts => _providers.Count > 0 &&
        _providers.Values.All(static provider => provider.SupportsCloudDrafts);
    public bool SupportsCloudDraftsFor(MailAccount account) => For(account).SupportsCloudDraftsFor(account);

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
        For(account).GetFoldersAsync(account, mailbox, cancellationToken);
    public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, CancellationToken cancellationToken = default) =>
        For(account).SyncFolderAsync(account, mailbox, folderId, cursor, cancellationToken);
    public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, DateTimeOffset? receivedSince, CancellationToken cancellationToken = default) =>
        For(account).SyncFolderAsync(account, mailbox, folderId, cursor, receivedSince, cancellationToken);
    public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) =>
        For(account).MarkReadAsync(account, mailbox, messageId, isRead, cancellationToken);
    public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
        For(account).GetMessageAsync(account, mailbox, messageId, cancellationToken);
    public Task<IReadOnlyList<MailMessage>> SearchMessagesAsync(MailAccount account, Mailbox mailbox, string query, int limit = 250, CancellationToken cancellationToken = default) =>
        For(account).SearchMessagesAsync(account, mailbox, query, limit, cancellationToken);
    public Task<IReadOnlyList<MailHeader>> GetMessageHeadersAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
        For(account).GetMessageHeadersAsync(account, mailbox, messageId, cancellationToken);
    public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) =>
        For(account).MoveMessageAsync(account, mailbox, messageId, destinationFolderId, cancellationToken);
    public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) =>
        For(account).SetFlaggedAsync(account, mailbox, messageId, isFlagged, cancellationToken);
    public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
        For(account).GetAttachmentsAsync(account, mailbox, messageId, cancellationToken);
    public Task<MailAttachment?> GetAttachmentAsync(MailAccount account, Mailbox mailbox, string messageId, string attachmentId, CancellationToken cancellationToken = default) =>
        For(account).GetAttachmentAsync(account, mailbox, messageId, attachmentId, cancellationToken);
    public Task DownloadAttachmentAsync(MailAccount account, Mailbox mailbox, string messageId, MailAttachment attachment, Stream destination, CancellationToken cancellationToken = default) =>
        For(account).DownloadAttachmentAsync(account, mailbox, messageId, attachment, destination, cancellationToken);
    public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) =>
        For(account).SendAsync(account, mailbox, draft, cancellationToken);
    public Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
        For(account).GetDraftsAsync(account, mailbox, cancellationToken);
    public Task<CloudDraft> GetDraftAsync(MailAccount account, Mailbox mailbox, string draftId, CancellationToken cancellationToken = default) =>
        For(account).GetDraftAsync(account, mailbox, draftId, cancellationToken);
    public Task<CloudDraft> CreateDraftAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) =>
        For(account).CreateDraftAsync(account, mailbox, draft, cancellationToken);
    public Task<CloudDraft> UpdateDraftAsync(MailAccount account, Mailbox mailbox, string draftId, DraftMessage draft, CancellationToken cancellationToken = default) =>
        For(account).UpdateDraftAsync(account, mailbox, draftId, draft, cancellationToken);
    public Task DeleteDraftAsync(MailAccount account, Mailbox mailbox, string draftId, CancellationToken cancellationToken = default) =>
        For(account).DeleteDraftAsync(account, mailbox, draftId, cancellationToken);
    public Task SendDraftAsync(MailAccount account, Mailbox mailbox, string draftId, CancellationToken cancellationToken = default) =>
        For(account).SendDraftAsync(account, mailbox, draftId, cancellationToken);
    public Task<Mailbox> ValidateSharedMailboxAsync(MailAccount account, string address, CancellationToken cancellationToken = default) =>
        For(account) is ISharedMailboxProvider shared
            ? shared.ValidateSharedMailboxAsync(account, address, cancellationToken)
            : Task.FromException<Mailbox>(new NotSupportedException("This provider does not support shared mailboxes."));

    private IMailProvider For(MailAccount account) =>
        _providers.TryGetValue(account.ProviderId, out var provider)
            ? provider
            : throw new InvalidOperationException($"No mail provider is registered for '{account.ProviderId}'.");
}

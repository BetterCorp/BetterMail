namespace BetterMail.Core;

// Values are persisted in mail_actions.
public enum MailActionKind { Move = 0, DeleteDraft = 1, Send = 2, UpdateState = 3 }

public sealed record MailAction(
    string Id,
    string AccountId,
    string MailboxId,
    string ItemId,
    MailActionKind Kind,
    string Subject,
    DateTimeOffset CreatedAt,
    string? ProviderId = null,
    string? DestinationId = null,
    string? DestinationName = null,
    bool Running = false,
    bool Accepted = false,
    string? Error = null,
    string[]? PreviousProviderIds = null,
    string? SourceFolderId = null,
    bool SourceWasUnread = false,
    bool SendAttempted = false,
    bool? ReadValue = null, bool? FlagValue = null, bool? PinValue = null,
    bool? PreviousRead = null, bool? PreviousFlagged = null, bool? PreviousPinned = null, int FailureCount = 0,
    int? RetryAuthorizedAtFailureCount = null, DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? LastFailureAt = null, string? LastError = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? StatusCheckDetails { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasStatusCheck => StatusCheckDetails is not null;
    public bool IsRetryPaused => !Accepted && !Running && FailureCount >= 3 && RetryAuthorizedAtFailureCount != FailureCount;
    public bool CanRetry => !Running && !Accepted && !SendAttempted && FailureCount > 0;
    public string? FailureDetails => Error ?? LastError;
    public bool HasFailure => FailureCount > 0 || FailureDetails is not null;
    public string RetryHistory => $"{FailureCount} failed attempt(s)" +
        (LastFailureAt is { } failed ? $" · Last failure {failed.ToLocalTime():g}" : "") +
        (LastAttemptAt is { } attempted ? $" · Last attempt {attempted.ToLocalTime():g}" : "");
    public string RecoveryGuidance => NeedsSendReview ? "Check Sent before taking any further action; delivery was not confirmed." :
        FailureDetails is { } error && (error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        ? "The message or destination may have moved or been deleted. Check status to look for the message on the server. Missing does not mean it was sent or deleted." :
        "Check connectivity and account access, then retry. If the provider reports a permission or folder error, fix that first. Your pending action is kept.";
    public bool CanCancel => !Running && !Accepted && !SendAttempted;
    public bool NeedsSendReview => Kind == MailActionKind.Send && SendAttempted && !Running && !Accepted;
    public string DisplaySubject => string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject;
    public string ActionText => Kind switch
    {
        MailActionKind.UpdateState => "Update message",
        MailActionKind.Send => "Send",
        MailActionKind.DeleteDraft => "Delete draft",
        _ => $"Move to {DestinationName}"
    };
    public string StatusText => NeedsSendReview ? "Delivery unconfirmed — check Sent" : Running ? Kind switch
    {
        MailActionKind.UpdateState => "Updating…",
        MailActionKind.Send => "Sending…",
        MailActionKind.DeleteDraft => "Deleting…",
        _ => "Moving…"
    } : IsRetryPaused ? "Paused — needs attention" : Error is null ? "Waiting for sync" : "Retrying next sync";
    public DateTimeOffset LocalCreatedAt => CreatedAt.ToLocalTime();
    public string MailboxAddress => MailboxId[(MailboxId.LastIndexOf(':') + 1)..];
}

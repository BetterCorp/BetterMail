namespace BetterMail.Core;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Mail = 1 << 0,
    SharedMailboxes = 1 << 1,
    SendAs = 1 << 2,
    SendOnBehalf = 1 << 3,
    ServerSearch = 1 << 4,
    Categories = 1 << 5,
    Calendar = 1 << 6,
    Contacts = 1 << 7,
    Tasks = 1 << 8,
    Files = 1 << 9,
    Notes = 1 << 10
}

public sealed record MailAccount(
    string ProviderId,
    string AccountId,
    string TenantId,
    string EmailAddress,
    string DisplayName,
    ProviderCapabilities Capabilities);

public sealed record Mailbox(
    string AccountId,
    string Address,
    string DisplayName,
    bool IsShared = false,
    bool CanSendAs = false,
    bool CanSendOnBehalf = false)
{
    public string Id => $"{AccountId}:{Address.ToLowerInvariant()}";
}

public sealed record MailFolder(
    string MailboxId,
    string ProviderId,
    string DisplayName,
    int UnreadCount,
    int TotalCount,
    string? WellKnownName = null,
    string? ParentProviderId = null);

public sealed record MailStoreCounts(int Total, int Unread, int Flagged);

public sealed record MailHeader(string Name, string Value);

public sealed record MailAddress(string Name, string Address)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public enum MailImportance
{
    Low,
    Normal,
    High
}

public sealed record MailMessage(
    string MailboxId,
    string ProviderId,
    string? ConversationId,
    string? InternetMessageId,
    string FolderId,
    string Subject,
    MailAddress From,
    IReadOnlyList<MailAddress> To,
    DateTimeOffset ReceivedAt,
    string Preview,
    string? Body,
    bool IsHtml,
    bool IsRead,
    bool HasAttachments,
    MailImportance Importance,
    IReadOnlyList<string> Categories,
    string? ETag,
    bool IsFlagged = false,
    bool IsDeleted = false,
    IReadOnlyList<MailAddress>? Cc = null)
{
    public bool IsUnread => !IsRead;
    public string SenderDisplayName => string.IsNullOrWhiteSpace(From.Name) ? From.Address : From.Name;
    public string DisplayPreview
    {
        get
        {
            var preview = string.Join(' ', Preview.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (!string.IsNullOrWhiteSpace(Subject) && preview.StartsWith(Subject, StringComparison.OrdinalIgnoreCase))
            {
                preview = preview[Subject.Length..].TrimStart(' ', '-', '–', '—', ':');
            }
            return preview;
        }
    }
}

public sealed record MailAttachment(
    string ProviderId,
    string Name,
    string ContentType,
    long Size,
    bool IsInline,
    string? ContentId,
    byte[]? ContentBytes);

public sealed record MailSyncPage(
    IReadOnlyList<MailMessage> Messages,
    string? NextCursor,
    bool HasMore);

public sealed record DraftMessage(
    string Subject,
    IReadOnlyList<MailAddress> To,
    string Body,
    bool IsHtml,
    IReadOnlyList<MailAddress>? Cc = null,
    IReadOnlyList<MailAddress>? Bcc = null,
    IReadOnlyList<DraftAttachment>? Attachments = null);

public sealed record CloudDraft(
    string ProviderId,
    string AccountId,
    string MailboxId,
    DraftMessage Message,
    DateTimeOffset UpdatedAt,
    string? ETag = null,
    bool HasUnsupportedAttachments = false);

public sealed record DraftAttachment(
    string Name,
    string ContentType,
    byte[] ContentBytes,
    bool IsInline = false,
    string? ContentId = null)
{
    public const long MaximumSizeBytes = 150L * 1024 * 1024;
    public long Size => ContentBytes.LongLength;
    public string SizeText => Size < 1024 * 1024
        ? $"{Math.Max(1, Size / 1024):N0} KB"
        : $"{Size / (1024d * 1024d):N1} MB";
}

public sealed record LocalDraft(
    string Id,
    string AccountId,
    string MailboxId,
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string Body,
    IReadOnlyList<DraftAttachment> Attachments,
    DateTimeOffset UpdatedAt,
    bool IsHtml = false,
    string? ProviderDraftId = null,
    DateTimeOffset? SyncedLocalUpdatedAt = null,
    DateTimeOffset? ProviderUpdatedAt = null,
    string? ProviderETag = null)
{
    public string DisplaySubject => string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject;
}

public sealed record CalendarInfo(
    string ProviderId,
    string Name,
    string? Color,
    bool CanEdit,
    string? AccountId = null);

public enum CalendarAttendeeType
{
    Required,
    Optional,
    Resource
}

public sealed record CalendarAttendee(MailAddress Address, CalendarAttendeeType Type = CalendarAttendeeType.Required);

public enum CalendarRecurrencePatternType
{
    Daily,
    Weekly,
    AbsoluteMonthly,
    RelativeMonthly,
    AbsoluteYearly,
    RelativeYearly
}

public enum CalendarRecurrenceRangeType
{
    NoEnd,
    EndDate,
    Numbered
}

public enum CalendarRecurrenceIndex
{
    First,
    Second,
    Third,
    Fourth,
    Last
}

public sealed record CalendarRecurrence(
    CalendarRecurrencePatternType PatternType,
    int Interval,
    DateOnly StartDate,
    CalendarRecurrenceRangeType RangeType = CalendarRecurrenceRangeType.NoEnd,
    DateOnly? EndDate = null,
    int? NumberOfOccurrences = null,
    IReadOnlyList<DayOfWeek>? DaysOfWeek = null,
    int? DayOfMonth = null,
    CalendarRecurrenceIndex? Index = null,
    int? Month = null,
    DayOfWeek? FirstDayOfWeek = null);

public sealed record CalendarEventDraft(
    string CalendarId,
    string Subject,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Location = null,
    IReadOnlyList<CalendarAttendee>? Attendees = null,
    bool IsReminderOn = true,
    int ReminderMinutesBeforeStart = 15,
    CalendarRecurrence? Recurrence = null);

public sealed record CalendarEvent(
    string ProviderId,
    string CalendarId,
    string Subject,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Location,
    IReadOnlyList<CalendarAttendee>? Attendees = null,
    bool IsReminderOn = false,
    int ReminderMinutesBeforeStart = 0,
    CalendarRecurrence? Recurrence = null,
    string? AccountId = null,
    CalendarAvailability Availability = CalendarAvailability.Unknown)
{
    public string TimeText => $"{StartsAt.ToLocalTime():ddd, MMM d · HH:mm}–{EndsAt.ToLocalTime():HH:mm}";
}

public enum CalendarAvailability
{
    Unknown,
    Free,
    WorkingElsewhere,
    Tentative,
    Busy,
    OutOfOffice
}
public sealed record ContactInfo(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<string> EmailAddresses,
    string? AccountId = null)
{
    public string EmailText => string.Join(", ", EmailAddresses);
}

public sealed record ContactDraft(
    string AccountId,
    string DisplayName,
    IReadOnlyList<string> EmailAddresses);

public sealed record DiscoveredPerson(
    string EmailAddress,
    string DisplayName,
    IReadOnlyList<string> MailboxIds,
    int Frequency,
    DateTimeOffset LastContactedAt);

public sealed record TaskListInfo(
    string ProviderId,
    string DisplayName,
    string AccountId,
    string? WellKnownName = null,
    bool IsOwner = true,
    bool IsShared = false);

public enum TaskBodyContentType
{
    Text,
    Html
}

public enum TaskImportance
{
    Low,
    Normal,
    High
}

public enum TodoTaskStatus
{
    NotStarted,
    InProgress,
    Completed,
    WaitingOnOthers,
    Deferred
}

public sealed record TaskDraft(
    string AccountId,
    string ListId,
    string Title,
    DateTimeOffset? DueAt = null,
    string? Notes = null,
    TaskBodyContentType NotesContentType = TaskBodyContentType.Text,
    TaskImportance? Importance = null,
    bool? IsReminderOn = null,
    DateTimeOffset? ReminderAt = null,
    CalendarRecurrence? Recurrence = null,
    bool ClearRecurrence = false,
    IReadOnlyList<string>? Categories = null,
    TodoTaskStatus? Status = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record TaskInfo(
    string ProviderId,
    string ListId,
    string Title,
    DateTimeOffset? DueAt,
    bool IsComplete,
    string? AccountId = null,
    string? Notes = null,
    TaskBodyContentType NotesContentType = TaskBodyContentType.Text,
    TaskImportance Importance = TaskImportance.Normal,
    bool IsReminderOn = false,
    DateTimeOffset? ReminderAt = null,
    CalendarRecurrence? Recurrence = null,
    IReadOnlyList<string>? Categories = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? CompletedAt = null,
    TodoTaskStatus Status = TodoTaskStatus.NotStarted)
{
    public string DueText => DueAt is null ? "" : $"Due {DueAt.Value.ToLocalTime():ddd, MMM d}";
}
public sealed record CloudFile(
    string ProviderId,
    string Name,
    long Size,
    Uri? WebUrl,
    string? AccountId = null,
    string? AccountProviderId = null,
    string? ParentPath = null)
{
    public string SizeText => Size < 1024 * 1024 ? $"{Math.Max(1, Size / 1024):N0} KB" : $"{Size / (1024d * 1024d):N1} MB";
    public string Path
    {
        get
        {
            var parent = (ParentPath ?? "")
                .Replace("/drive/root:", "", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return $"{parent}/{Name}";
            }
            return WebUrl is null || WebUrl.AbsolutePath.Length <= 1
                ? Name
                : Uri.UnescapeDataString(WebUrl.AbsolutePath).TrimEnd('/');
        }
    }
}

public sealed record CloudDriveItem(
    string ProviderId,
    string Name,
    long Size,
    bool IsFolder,
    string? ParentProviderId,
    Uri? WebUrl,
    string AccountId,
    string AccountProviderId,
    string? ContentType = null,
    string? ParentPath = null)
{
    public string SizeText => IsFolder ? "" : Size < 1024 * 1024
        ? $"{Math.Max(1, Size / 1024):N0} KB"
        : $"{Size / (1024d * 1024d):N1} MB";
    public string Path => string.IsNullOrWhiteSpace(ParentPath) ? Name : $"{ParentPath.TrimEnd('/')}/{Name}";
}
public sealed record NoteInfo(
    string ProviderId,
    string Title,
    DateTimeOffset ModifiedAt,
    Uri? WebUrl,
    string? AccountId = null,
    string? AccountProviderId = null,
    string? SectionProviderId = null)
{
    public string ModifiedText => $"Modified {ModifiedAt.ToLocalTime():g}";
}

public sealed record NoteNotebook(
    string ProviderId,
    string Name,
    string AccountId,
    string AccountProviderId,
    Uri? WebUrl = null);

public sealed record NoteSection(
    string ProviderId,
    string NotebookProviderId,
    string Name,
    string AccountId,
    string AccountProviderId,
    Uri? WebUrl = null);

public sealed record NotePage(
    string ProviderId,
    string SectionProviderId,
    string Title,
    DateTimeOffset ModifiedAt,
    int Order,
    int Level,
    string AccountId,
    string AccountProviderId,
    Uri? WebUrl = null);

public sealed record NotePageContent(
    string PageProviderId,
    string SectionProviderId,
    string AccountId,
    string AccountProviderId,
    string UntrustedHtml);

public sealed record NotePageDraft(
    string AccountId,
    string AccountProviderId,
    string SectionProviderId,
    string Title,
    string HtmlBody);

public enum NotePatchAction
{
    Replace,
    Append,
    Insert,
    Prepend
}

public enum NotePatchPosition
{
    After,
    Before
}

public sealed record NotePagePatch(
    string Target,
    NotePatchAction Action,
    string? HtmlContent = null,
    NotePatchPosition? Position = null);

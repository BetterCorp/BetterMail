using System.Reflection;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class WorkspaceInteractionTests
{
    private static MailAccount Account(string id) => new("microsoft365", id, "tenant", id + "@example.test", id, ProviderCapabilities.Contacts | ProviderCapabilities.Calendar);

    [Fact]
    public async Task DefaultContactAccountAppliesToNewAndDiscoveredContactsWithoutChangingSavedOwnership()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        var first = Account("first"); var second = Account("second");
        vm.ContactOwners.Add(new(first, new(first.AccountId, first.EmailAddress, first.DisplayName)));
        vm.ContactOwners.Add(new(second, new(second.AccountId, second.EmailAddress, second.DisplayName)));
        vm.DefaultContactOwner = vm.ContactOwners[1];
        await ((AsyncCommand)vm.NewContactCommand).ExecuteAsync();
        Assert.Equal(second.AccountId, vm.SelectedContactOwner?.Account.AccountId);
        var found = PersonEntry.Discovered(new("found@example.test", "Found", [], 1, DateTimeOffset.Now), "Mail history");
        await ((AsyncCommand<PersonEntry>)vm.EditContactCommand).ExecuteAsync(found);
        Assert.Equal(second.AccountId, vm.SelectedContactOwner?.Account.AccountId);
        Assert.Equal("found@example.test", vm.ContactEmails);
        var saved = PersonEntry.Saved(new("saved", "Saved", [], first.AccountId), "First");
        await ((AsyncCommand<PersonEntry>)vm.EditContactCommand).ExecuteAsync(saved);
        Assert.Equal(first.AccountId, vm.SelectedContactOwner?.Account.AccountId);
        Assert.False(saved.IsDiscovered);
        Assert.True(found.IsDiscovered);
    }

    [Fact]
    public async Task EditingContactPatchesOnlyChangedDetailsAndKeepsUnknownFields()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        var account = Account("one");
        vm.ContactOwners.Add(new(account, new(account.AccountId, account.EmailAddress, account.DisplayName)));
        var saved = PersonEntry.Saved(new("contact", "Person", ["person@example.test"], account.AccountId,
            Details: new(MobilePhone: "123", CompanyName: "Studio", PersonalNotes: "Existing notes")), "One");
        await ((AsyncCommand<PersonEntry>)vm.EditContactCommand).ExecuteAsync(saved);
        Assert.Equal("123", vm.ContactMobilePhone);
        Assert.Equal("Existing notes", vm.ContactPersonalNotes);
        vm.ContactMobilePhone = "456";
        vm.ContactCompanyName = "";
        var details = (ContactDetails)typeof(MainWindowViewModel).GetMethod("EditedContactDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        var payload = JsonSerializer.SerializeToElement(Microsoft365WorkspaceProvider.BuildContactPayload(account,
            new(account.AccountId, "Person", ["person@example.test"], Details: details)));
        Assert.Equal("456", payload.GetProperty("mobilePhone").GetString());
        Assert.Equal("", payload.GetProperty("companyName").GetString());
        Assert.False(payload.TryGetProperty("personalNotes", out _));
        Assert.False(payload.TryGetProperty("homePhones", out _));
    }

    [Fact]
    public void RichContactFieldsMapAndParticipateInSearch()
    {
        var item = JsonDocument.Parse("""{"id":"c","displayName":"Person","mobilePhone":"123","companyName":"Studio","businessPhones":["456"],"personalNotes":"Research"}""");
        var contact = Microsoft365WorkspaceProvider.MapContact(item.RootElement, "one");
        Assert.Equal("123", contact.Details?.MobilePhone);
        Assert.Equal("456", Assert.Single(contact.Details!.BusinessPhones!));
        Assert.Contains("Studio", contact.SearchText);
        Assert.Contains("Research", contact.SearchText);
        Assert.Equal(JsonSerializer.Serialize(contact), JsonSerializer.Serialize(JsonSerializer.Deserialize<ContactInfo>(JsonSerializer.Serialize(contact))));
    }

    [Fact]
    public async Task FailedSyncStepsEscalateTheBadgeUntilASuccessfulRetry()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        var step = new SyncStep("Draft reconciliation");
        vm.SyncSteps.Add(step);
        var run = typeof(MainWindowViewModel).GetMethod("RunSyncStepAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Func<Task> fail = () => Task.FromException(new InvalidOperationException("Sync failed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)run.Invoke(vm, [step, fail])!);
            Assert.False(step.Running);
            vm.RecordMailSyncOutcome(false);
            Assert.Equal(attempt, vm.SyncSeverity);
        }
        Func<Task> succeed = () => Task.CompletedTask;
        await (Task)run.Invoke(vm, [step, succeed])!;
        vm.RecordMailSyncOutcome(false);
        Assert.Equal(0, vm.SyncSeverity);
        step.Detail = "Failed: cache could not be refreshed";
        vm.RecordMailSyncOutcome(false);
        Assert.Equal(1, vm.SyncSeverity);
    }

    [Fact]
    public void BadgesEscalateAndResetOnlyWhenTheirWorkRecovers()
    {
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null);
        Assert.False(vm.HasSyncIssues);
        var notified = false;
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.HasSyncIssues)) notified = true; };
        vm.Drafts.Add(new("draft", "one", "mailbox", "", "", "", "Draft", "", [], DateTimeOffset.Now, SyncStatus: DraftSyncStatus.Conflict));
        Assert.True(vm.HasSyncIssues);
        vm.Drafts.Clear();
        Assert.True(notified);
        Assert.False(vm.HasSyncIssues);
        vm.RecordSyncOutcome(true); Assert.Equal(1, vm.SyncSeverity);
        vm.RecordSyncOutcome(true); Assert.Equal(2, vm.SyncSeverity);
        vm.RecordSyncOutcome(false); Assert.Equal(0, vm.SyncSeverity);
        var action = new MailAction("action", "one", "mailbox", "item", MailActionKind.Move, "Subject", DateTimeOffset.Now, FailureCount: 1);
        vm.BusyActions.Add(action); Assert.Equal(1, vm.BusySeverity);
        vm.BusyActions[0] = action with { FailureCount = 2 }; Assert.Equal(2, vm.SyncSeverity);
        vm.RecordSyncOutcome(false); Assert.Equal(2, vm.SyncSeverity);
        vm.BusyActions.Clear(); Assert.Equal(0, vm.SyncSeverity);
    }

    [Fact]
    public async Task TodayAgendaLoadsCachedOverlappingEventsAndOpensTheChosenSource()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-today-agenda-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('B', 64));
            await store.InitializeAsync(token);
            var account = Account("one");
            var calendar = new CalendarInfo("cal", "Work", "#0F6CBD", true, account.AccountId);
            await store.ReplaceWorkspaceItemsAsync("calendar", account.AccountId, "all", new[] { calendar }, item => item.ProviderId, item => item.Name, token);
            var today = new DateTimeOffset(DateTime.Today);
            var entry = new CalendarEvent("today", "cal", "Meeting", today.AddHours(10), today.AddHours(11), null, AccountId: account.AccountId);
            await store.ReplaceCalendarEventsAsync(account.AccountId, "cal", today.AddDays(-1), today.AddDays(2),
                [entry, entry with { ProviderId = "overnight", StartsAt = today.AddHours(-1), EndsAt = today.AddHours(1) },
                 entry with { ProviderId = "cancelled", IsCancelled = true },
                 entry with { ProviderId = "tomorrow", StartsAt = today.AddDays(1).AddHours(10), EndsAt = today.AddDays(1).AddHours(11) }], token);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            vm.Accounts.Add(account);
            await vm.RefreshDayAgendaAsync();
            Assert.Equal(new[] { "overnight", "today" }, vm.DayAgenda.Where(item => item.IsEvent).Select(item => item.Source!.Event.ProviderId));
            Assert.Single(vm.DayAgenda, item => item.IsNow);
            CalendarEventSource? opened = null;
            vm.CalendarEventDetailsRequested += source => opened = source;
            vm.OpenAgendaEvent(vm.DayAgenda.Single(item => item.Source?.Event.ProviderId == "today"));
            Assert.Equal("today", opened?.Event.ProviderId);
            Assert.Equal(account.AccountId, opened?.Account.AccountId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AgendaDistinguishesCurrentPastFutureAndAllDayEvents()
    {
        var now = DateTimeOffset.Now;
        var account = Account("one");
        var source = new CalendarEventSource(account, new(new("cal", "Work", null, true, account.AccountId), "#0F6CBD", () => { }),
            new("event", "cal", "Meeting", now.AddMinutes(-15), now.AddMinutes(15), null, null, false, 0, null, account.AccountId));
        Assert.Contains("In progress", new DayAgendaItem(source, now).Detail);
        Assert.Contains("Ended", new DayAgendaItem(source, now.AddHours(1)).Detail);
        Assert.Contains("Upcoming", new DayAgendaItem(source, now.AddHours(-1)).Detail);
        Assert.True(new DayAgendaItem(null, now).IsNow);
        Assert.Equal("All day", new DayAgendaItem(source with { Event = source.Event with { IsAllDay = true } }, now).Time);
    }
}

using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class SearchQueryTests
{
    [Fact]
    public void FormAndTextRoundTripPhrasesEscapesAndDateRanges()
    {
        var parsed = SearchQuery.Parse("\"quarterly review\" type:mail in:{Inbox/Project Alpha} from:{Jamie} date:{>=2026-09-01} date:{<2026-10-01}");
        Assert.Equal("Mail", parsed.Scope);
        Assert.Equal("quarterly review", Assert.Single(parsed.Terms));
        Assert.Equal("Inbox/Project Alpha", parsed["in"]);
        var form = parsed.Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        form["words"] = string.Join(' ', parsed.Terms.Select(SearchQuery.Encode));
        form["date"] = string.Join(';', parsed.Dates.Select(date => date.Source));
        Assert.Equal(parsed.Serialize(), SearchOptionsWindow.BuildQuery(form));
        Assert.Equal("a}b\\c", SearchQuery.Parse("subject:" + SearchQuery.Encode("a}b\\c"))["subject"]);
    }

    [Theory]
    [InlineData("unknown:foo")]
    [InlineData("in:{Inbox")]
    [InlineData("date:{2026-02-30}")]
    [InlineData("date:{>25:00}")]
    [InlineData("type:people from:Jamie")]
    [InlineData("type:invalid")]
    [InlineData("has:yes")]
    [InlineData("from:a from:b")]
    [InlineData("subject:")]
    public void InvalidQueriesAreActionableErrors(string query) => Assert.Throws<FormatException>(() => SearchQuery.Parse(query));

    [Fact]
    public async Task MailFiltersApplyBeforeLimitAndSupportFilterOnlyRanges()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-query-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var match = new MailMessage("a", "match", null, null, "projects", "Quarterly review", new("Jamie", "jamie@work.example"),
                [new("Alex", "alex@work.example")], new DateTimeOffset(2026, 9, 5, 15, 30, 0, TimeSpan.Zero), "Budget", "Budget", false, false, true, MailImportance.High, ["Finance"], null,
                IsFlagged: true, Cc: [new("Pat", "pat@work.example")]);
            var distractors = Enumerable.Range(0, 600).Select(i => match with { ProviderId = "other-" + i, ReceivedAt = match.ReceivedAt.AddDays(1), From = new("Other", "other@work.example") });
            await store.ApplySyncPageAsync("seed", new(distractors.Append(match).ToArray(), null, false), token);
            var query = SearchQuery.Parse("from:jamie to:alex cc:pat subject:{Quarterly review} has:attachments is:unread importance:high category:Finance date:{>=2026-09-01} date:{<2026-10-01}");
            var results = await store.SearchFilteredMailAsync(query, [new("a", "projects")], 1, token);
            Assert.Equal("match", Assert.Single(results).ProviderId);
            Assert.Empty(await store.SearchFilteredMailAsync(query, [new("b", "projects")], 1, token));
            Assert.Empty(await store.SearchFilteredMailAsync(SearchQuery.Parse("date:{>2026-10-01}"), [new("a", "projects")], 1, token));
            Assert.Equal("match", Assert.Single(await store.SearchFilteredMailAsync(SearchQuery.Parse("from:jamie date:{=2026-09-05T15:30Z}"), [new("a", "projects")], 1, token)).ProviderId);
            Assert.Equal("match", Assert.Single(await store.SearchFilteredMailAsync(SearchQuery.Parse("\"Quarterly review\" from:jamie"), [new("a", "projects")], 1, token)).ProviderId);
            Assert.Empty(await store.SearchFilteredMailAsync(SearchQuery.Parse("from:{%' OR 1=1 --}"), [new("a", "projects")], 1, token));
            var localTime = match.ReceivedAt.ToLocalTime().ToString("HH:mm:ss");
            Assert.Equal("match", Assert.Single(await store.SearchFilteredMailAsync(SearchQuery.Parse("from:jamie date:{=" + localTime + "}"), [new("a", "projects")], 1, token)).ProviderId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterMail.Core;

public enum DocumentKind { Other, Identity, Address, Registration, Contract, ProofOfPayment }
public enum VerificationStatus { Unverified, Verified, Rejected }

public sealed record EvidenceIssue(string Code, string Message, string? RecordId = null, bool Retryable = false);
public sealed class EvidenceException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed record EvidenceCompleteness(
    string Scope, bool Complete, long CachedMessages, long AttachmentMessages, long InspectedMessages,
    long Documents, long SearchableDocuments, long IncompleteDocuments,
    string Note = "Local cache only; mail outside sync history is not searched. OCR and document classification require human review.")
{
    public bool CachedAttachmentCoverageComplete => InspectedMessages == CachedMessages && IncompleteDocuments == 0;
}

public sealed record EvidencePage<T>(IReadOnlyList<T> Items, string? NextCursor, EvidenceCompleteness Completeness)
{
    public bool HasMore => NextCursor is not null;
}

public sealed record EvidenceMessage(string Id, MailMessage Message, DateTimeOffset CapturedAt,
    string AttachmentState = "Pending", EvidenceIssue? Issue = null, string? MimeSha256 = null)
{
    public string SourceLink => $"bettermail://evidence/{Id}";
}

public sealed record DocumentText(string Text, string Method, bool Complete, int PagesProcessed = 0,
    int? TotalPages = null, EvidenceIssue? Issue = null);

public sealed record EvidenceDocument(string Id, string MessageId, string MailboxId, string AttachmentId,
    string Name, string ContentType, long Size, bool IsInline, string? Sha256, string State,
    DocumentText Extraction, DocumentKind Kind = DocumentKind.Other,
    VerificationStatus Verification = VerificationStatus.Unverified, string Reviewer = "", string Note = "",
    DateTimeOffset? ReviewedAt = null)
{
    public string SourceLink => $"bettermail://evidence/{Id}";
    public string MessageLink => $"bettermail://evidence/{MessageId}";
    public string Summary => $"{Kind} · {Verification} · {State}";
}

public sealed record EvidenceMatch(string Field, string Term, string Snippet);
public sealed record EvidenceSearchResult(EvidenceMessage Message, EvidenceDocument? Document,
    IReadOnlyList<EvidenceMatch> Matches)
{
    public string Id => Document?.Id ?? Message.Id;
    public string Title => Document?.Name ?? Message.Message.Subject;
    public string SourceLink => Document?.SourceLink ?? Message.SourceLink;
    public string WhyMatched => string.Join("; ", Matches.Select(match => $"{match.Field}: {match.Term} — {match.Snippet}"));
}

public sealed record CorrespondenceTerms(string[]? EmailAliases = null, string[]? CompanyNames = null,
    string[]? Domains = null, string[]? InvoiceReferences = null)
{
    public IReadOnlyList<(string Field, string Term)> Validate()
    {
        if (new[] { EmailAliases, CompanyNames, Domains, InvoiceReferences }.Any(values => values?.Any(string.IsNullOrWhiteSpace) == true))
            throw new EvidenceException("invalid_terms", "Search terms cannot be null or blank.");
        var terms = (EmailAliases ?? []).Select(value => ("Email alias", value))
            .Concat((CompanyNames ?? []).Select(value => ("Company", value)))
            .Concat((Domains ?? []).Select(value => ("Domain", value.TrimStart('@'))))
            .Concat((InvoiceReferences ?? []).Select(value => ("Invoice reference", value)))
            .Select(item => (item.Item1, item.Item2.Trim())).Distinct().ToArray();
        if (terms.Length is < 1 or > 40 || terms.Any(item => item.Item2.Length is < 2 or > 200))
            throw new EvidenceException("invalid_terms", "Supply 1–40 aliases, company names, domains or invoice references, each 2–200 characters.");
        if (terms.Any(item => item.Item1 == "Email alias" && !System.Net.Mail.MailAddress.TryCreate(item.Item2, out _)))
            throw new EvidenceException("invalid_alias", "Email aliases must be valid email addresses.");
        if (terms.Any(item => item.Item1 == "Domain" && Uri.CheckHostName(item.Item2) != UriHostNameType.Dns))
            throw new EvidenceException("invalid_domain", "Use a domain name such as example.com, without a URL or path.");
        return terms.Select(item => (item.Item1, item.Item1 == "Email alias"
            ? new System.Net.Mail.MailAddress(item.Item2).Address : item.Item2)).ToArray();
    }
}

public sealed record EvidenceDuplicate(string Basis, string Fingerprint, bool ExactBytes,
    IReadOnlyList<string> RecordIds, long Occurrences, bool MembersTruncated)
{
    public IReadOnlyList<string> SourceLinks => RecordIds.Select(id => $"bettermail://evidence/{id}").ToArray();
}

public sealed record EvidenceExport(string Id, string Name, string Sha256, long Size, DateTimeOffset ExpiresAt,
    string[] MailboxIds, bool Complete, IReadOnlyList<EvidenceIssue> Issues);

// A cursor is an offset into a scoped, ordered snapshot, never an authorization credential.
public sealed record EvidenceCursor(string Scope, long After, long Upper)
{
    public static string ScopeFor(object scope) => EvidenceHash.Of(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope)));
    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
    public static EvidenceCursor? Parse(string? value, string scope)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            if (value.Length > 2048) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<EvidenceCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Scope != scope || cursor.After < 0 || cursor.Upper < 0) throw new FormatException();
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new EvidenceException("invalid_cursor", "The continuation token is invalid or belongs to another query. Restart from the first page."); }
    }
}

public static class EvidenceHash
{
    public static string Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (destination.Length + count > maximumBytes)
                throw new EvidenceException("file_too_large", $"File exceeds the {maximumBytes / 1024 / 1024} MiB limit. Download it directly from the mail provider.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return destination.ToArray();
    }
}

public static class EvidenceLink
{
    public static bool TryParse(string value, out string id)
    {
        id = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "bettermail" || uri.Host != "evidence" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var candidate = uri.AbsolutePath.TrimStart('/');
        if (candidate.Length is not (32 or 64) || !candidate.All(Uri.IsHexDigit)) return false;
        id = candidate.ToLowerInvariant();
        return true;
    }
}
